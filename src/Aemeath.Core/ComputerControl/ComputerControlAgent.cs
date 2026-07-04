using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aemeath.Core.AI;

namespace Aemeath.Core.ComputerControl;

/// <summary>
/// 电脑控制 Agent（轨 A）：基于 C# 原生 UIAutomation + 截图视觉 LLM，
/// 移植自 Microsoft UFO（ufo/agents/agent/、ufo/prompter/、ufo/prompts/share/base/app_agent.yaml）的 ReAct 规划逻辑。
///
/// 算法骨架（单 AppAgent 简化版，省去 UFO 的 HostAgent/AppAgent 分层——见 CLAUDE.md 说明）：
/// 每一步 step：
///   1. 截图当前前台窗口
///   2. UIA 枚举可交互控件 → 标号 → 生成带标注截图
///   3. 调视觉 LLM（OpenAI 兼容，image_url+base64）：传入「带标注截图 + 控件列表 + 任务 + 历史」
///      → LLM 返回 JSON {observation, thought, action:{function,args,status}, plan}
///   4. 按 action.function 执行（click_input/set_edit_text/keyboard_input/wheel_mouse_input/...）
///   5. status：FINISH→完成；CONTINUE→回到 1；FAIL→报错；CONFIRM→视为敏感动作，记录后继续（前置确认已在调用层完成）
///   6. MAX_STEP 兜底
///
/// 输入输出设计参考 UFO 的 app_agent_example.yaml（one-step action + JSON 响应）。
/// </summary>
public sealed class ComputerControlAgent
{
    private const int MaxSteps = 30;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(200) };

    private readonly Func<(string Model, string Endpoint, string ApiKey)?> _visionConfig;
    private Win32Interop.MinimizeScope? _minimizeScope;

    public ComputerControlAgent(Func<(string Model, string Endpoint, string ApiKey)?> visionConfig)
    {
        _visionConfig = visionConfig;
    }

    /// <summary>执行一个自然语言电脑操作任务。返回执行结果摘要（成功/失败 + 步骤数 + 最终观察）。</summary>
    public async Task<string> RunAsync(string request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request))
        {
            return "任务为空。";
        }

        // 统一物理像素坐标系：Per-Monitor DPI v2 感知，确保截图/UIA/SetCursorPos 三者坐标一致。
        Win32Interop.EnsurePerMonitorDpiAware();

        var workDir = Path.Combine(Path.GetTempPath(), "aemeath-uia", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        // 跟踪目标窗口句柄（用于 MinimizeAllWindows 排除 + FocusWindow 聚焦）
        IntPtr targetHwnd = IntPtr.Zero;

        // 微信等自定义渲染 UI 优先走 Win32 直控（UIA 对微信几乎无效）。
        if (MentionsWeChat(request))
        {
            var wechatHwnd = Win32Interop.FindWeChatWindow();
            if (wechatHwnd == IntPtr.Zero)
            {
                // 检查微信进程是否在运行（WeChat 4.x 进程是 WeChatAppEx）
                var wxProcs = System.Diagnostics.Process.GetProcessesByName("WeChatAppEx");
                if (wxProcs.Length > 0)
                {
                    // 微信在运行但窗口不可见（托盘隐藏），尝试启动 Launcher 激活
                    progress?.Report("检测到微信在后台运行，正在尝试激活窗口…");
                    WeChatDirectController.TryStartWeChat();
                }
                else
                {
                    // 微信未运行，尝试启动
                    progress?.Report("未检测到微信，正在尝试启动微信…");
                    var started = WeChatDirectController.TryStartWeChat();
                    if (!started)
                    {
                        progress?.Report("未能自动找到微信安装路径，将尝试用视觉 Agent 操作。");
                    }
                }

                // 等待最多 20 秒让微信窗口出现（WeChat 4.x 启动较慢）
                for (int i = 0; i < 40 && Win32Interop.FindWeChatWindow() == IntPtr.Zero; i++)
                {
                    await Task.Delay(500, cancellationToken);
                }
            }

            wechatHwnd = Win32Interop.FindWeChatWindow();
            if (wechatHwnd != IntPtr.Zero)
            {
                targetHwnd = wechatHwnd;
                progress?.Report("检测到微信窗口，使用微信直控方案（绕过 UIA）…");
                var wechat = new WeChatDirectController(m => progress?.Report(m));
                var (handled, result) = await wechat.TryHandleAsync(request, cancellationToken);
                if (handled)
                {
                    return result;
                }
                // 直控未处理（如不是发消息任务），落到视觉 Agent
                progress?.Report("微信直控未能处理此任务，切换到视觉 Agent 模式…");
                // 聚焦微信窗口，确保视觉 Agent 的 CaptureForeground 捕获微信控件而非 Aemeath 窗口
                Win32Interop.FocusWindow(wechatHwnd);
            }
            else
            {
                // 微信窗口仍未出现（WeChat 4.x 托盘隐藏时无可检测窗口），
                // 不直接报错，而是落到视觉 Agent，让它通过截图+托盘图标点击来激活微信。
                progress?.Report("未检测到微信可见窗口，将用视觉 Agent 尝试点击托盘图标激活微信…");
            }
        }

        // 不再硬编码最小化窗口——改为 LLM 通过 minimize_all_windows 动作自行决定。
        // 仅聚焦目标窗口（如果有），确保它在前台供视觉 Agent 截图和控件枚举。
        if (targetHwnd != IntPtr.Zero)
        {
            Win32Interop.FocusWindow(targetHwnd);
        }

        try
        {
            var history = new StringBuilder();
            string? prevScreenshot = null;

            // 上一步操作的控件编号（用于截图标注时高亮）
            int? lastActionControlId = null;
            // 最近 3 步的 (function, argsSignature) 签名，用于检测重复操作循环。
            // 签名包含关键区分参数（如 click_on_coordinates 的 x/y），避免不同坐标被误判为重复。
            var recentActions = new List<(string Func, string ArgsSig)>(3);

            for (int step = 1; step <= MaxSteps; step++)
            {
                progress?.Report($"第 {step} 步：截图并分析当前界面…");
                cancellationToken.ThrowIfCancellationRequested();

                var rawShot = Path.Combine(workDir, $"step{step}_raw.png");
                ScreenCapture.CaptureFullScreen(rawShot);

                var controls = UiaControlTree.CaptureForeground();
                var annotatedShot = Path.Combine(workDir, $"step{step}_annotated.png");
                try
                {
                    UiaControlTree.Annotate(rawShot, controls, annotatedShot, lastActionControlId);
                }
                catch
                {
                    // 标注失败就用原图
                    File.Copy(rawShot, annotatedShot, overwrite: true);
                }

                progress?.Report($"第 {step} 步：识别到 {controls.Count} 个控件，正在让模型决策…");

                var decision = await DecideAsync(request, annotatedShot, controls, history.ToString(), prevScreenshot, step, cancellationToken);
                if (decision is null)
                {
                    return $"第 {step} 步决策失败（模型未返回有效结果），任务中止。已完成 {step - 1} 步。";
                }

                history.AppendLine($"[Step {step}] Observation: {decision.Observation}");
                history.AppendLine($"[Step {step}] Thought: {decision.Thought}");
                history.AppendLine($"[Step {step}] Action: {decision.Function}({decision.Args}) -> Status={decision.Status}");
                prevScreenshot = annotatedShot;

                progress?.Report($"第 {step} 步：{decision.Function}({decision.Args})");

                // 提取本步操作的控件编号，用于截图标注高亮
                var stepArgs = ParseArgs(decision.Args);
                int? stepControlId = null;
                if (stepArgs.TryGetValue("control", out var sc) && int.TryParse(sc?.ToString(), out var scid))
                {
                    stepControlId = scid;
                }

                // 重复操作检测：连续 3 步执行相同的 function + 相同关键参数 → 可能陷入循环
                var stepSig = ComputeActionSignature(decision.Function, stepArgs);
                recentActions.Add((decision.Function.ToLowerInvariant(), stepSig));
                if (recentActions.Count > 3) recentActions.RemoveAt(0);
                if (recentActions.Count == 3 &&
                    recentActions.All(a => a.Func == recentActions[0].Func && a.ArgsSig == recentActions[0].ArgsSig))
                {
                    return $"检测到重复操作，可能陷入循环（连续 3 步执行 {recentActions[0].Func}，签名：{recentActions[0].ArgsSig}）。任务中止。已完成 {step - 1} 步。";
                }

                // 记录本步控件编号，供下一步截图标注高亮
                lastActionControlId = stepControlId;

                // 执行动作
                string execResult;
                try
                {
                    execResult = ExecuteAction(decision, controls);
                }
                catch (Exception ex)
                {
                    history.AppendLine($"[Step {step}] 执行异常：{ex.Message}");
                    execResult = $"执行失败：{ex.Message}";
                }

                history.AppendLine($"[Step {step}] Result: {execResult}");
                Thread.Sleep(400); // 等待界面响应

                if (decision.Status.Equals("FINISH", StringComparison.OrdinalIgnoreCase))
                {
                    var summary = string.IsNullOrWhiteSpace(decision.Comment) ? decision.Observation : decision.Comment;
                    return $"任务完成（{step} 步）。{summary}";
                }

                if (decision.Status.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
                {
                    return $"任务未能完成（{step} 步）：{decision.Comment}";
                }

                // CONTINUE / CONFIRM 都继续下一步
            }

            return $"已达最大步数（{MaxSteps}），任务未明确完成。最近观察：{history.ToString().Trim()}";
        }
        finally
        {
            // 恢复 LLM 通过 minimize_all_windows 最小化的窗口
            _minimizeScope?.Dispose();
            _minimizeScope = null;
            // 清理临时截图目录（失败也不阻断返回）
            try { Directory.Delete(workDir, recursive: true); } catch { /* 忽略 */ }
        }
    }

    /// <summary>调用视觉 LLM 决策下一步。移植自 UFO app_agent.yaml 的 prompt 结构。</summary>
    private async Task<AgentDecision?> DecideAsync(
        string request, string annotatedShotPath, IReadOnlyList<AnnotatedControl> controls,
        string history, string? prevShot, int step, CancellationToken cancellationToken)
    {
        var config = _visionConfig();
        if (config is null)
        {
            return new AgentDecision
            {
                Observation = "未配置视觉辅助模型",
                Thought = "无法决策：视觉模型未配置",
                Function = "none",
                Args = string.Empty,
                Status = "FAIL",
                Comment = "请在设置 → 电脑控制 里配置一个支持图片输入的辅助视觉模型（如 gpt-4o）。"
            };
        }

        var (model, endpoint, apiKey) = config.Value;

        // 读取截图原始字节（重试时可复用，避免二次文件 IO）
        byte[] rawBytes;
        try
        {
            rawBytes = await File.ReadAllBytesAsync(annotatedShotPath, cancellationToken);
        }
        catch (Exception ex)
        {
            return Fail($"无法读取截图：{ex.Message}");
        }

        var prompt = BuildDecisionPrompt(request, controls, history, step);
        var baseUrl = OpenAIUrlHelper.NormalizeBaseUrlWithDefault(endpoint);
        var url = baseUrl.TrimEnd('/') + "/chat/completions";

        // 三轮重试：图片越小处理越快，越不容易触发 Cloudflare 524 超时。
        // 首轮 1024px 清晰度足够；若 524/超时则降到 800px；最后兜底 640px。
        var result = await TrySendVisionRequestAsync(url, apiKey, model, prompt, rawBytes, 1024, cancellationToken);
        if (result is not null)
        {
            return result;
        }

        var retry = await TrySendVisionRequestAsync(url, apiKey, model, prompt, rawBytes, 800, cancellationToken);
        if (retry is not null)
        {
            return retry;
        }

        var lastChance = await TrySendVisionRequestAsync(url, apiKey, model, prompt, rawBytes, 640, cancellationToken);
        return lastChance ?? Fail($"视觉模型在三轮重试后仍返回 HTTP 524 或超时（endpoint={baseUrl}）。已尝试 1024/800/640 三种图片尺寸。");
    }

    /// <summary>发送视觉模型请求并解析响应。返回 null 表示需要重试（HTTP 524/超时），返回 AgentDecision 表示成功或失败（非重试类错误）。
    /// 使用 streaming 模式（stream=true）避免 Cloudflare 524 超时：流式响应持续发送 SSE chunk，Cloudflare 看到数据流动不会断连。</summary>
    private async Task<AgentDecision?> TrySendVisionRequestAsync(
        string url, string apiKey, string model, string prompt,
        byte[] imageBytes, int maxLongSide, CancellationToken cancellationToken)
    {
        // 截图体积治理：缩放到指定长边，转 JPEG 降体积。
        string dataUrl;
        try
        {
            var compressed = ResizeForVision(imageBytes, maxLongSide);
            dataUrl = $"data:image/jpeg;base64,{Convert.ToBase64String(compressed)}";
        }
        catch (Exception ex)
        {
            return Fail($"无法缩放截图：{ex.Message}");
        }

        var body = new
        {
            model,
            temperature = 0.1,
            max_tokens = 4096,
            stream = true, // streaming 模式：避免 Cloudflare 524
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = (object)SystemPromptText
                },
                new
                {
                    role = "user",
                    content = (object)new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            }
        };

        HttpResponseMessage resp;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("Authorization", "Bearer " + apiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            // ResponseHeadersRead：响应头一到达就返回，不等 body——streaming 必需
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            // 超时 → 返回 null 表示需要重试
            return null;
        }
        catch (Exception ex)
        {
            return Fail($"视觉模型网络请求失败：{ex.Message}");
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken);
                var statusCode = (int)resp.StatusCode;
                // HTTP 524（Cloudflare 超时）或 5xx 服务端错误 → 返回 null 表示需要重试
                if (statusCode == 524 || (statusCode >= 500 && statusCode < 600))
                {
                    return null;
                }
                // 其他错误（401 key 错 / 404 模型名错 / 413 图太大 / 429 限流）→ 不重试
                var detail = json.Length > 400 ? json[..400] : json;
                return Fail($"视觉模型返回 HTTP {statusCode}：{detail}");
            }

            // 根据 Content-Type 决定解析方式
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            string? content;
            string? reasoning = null;

            if (contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // SSE 流式响应
                try
                {
                    var (c, r) = await ReadSseStreamAsync(resp, cancellationToken);
                    content = c;
                    reasoning = r;
                }
                catch (TaskCanceledException)
                {
                    return null; // 超时 → 重试
                }
                catch (Exception ex)
                {
                    return Fail($"视觉模型流式响应读取失败：{ex.Message}");
                }
            }
            else
            {
                // 非 SSE（服务器可能不支持 streaming，返回了常规 JSON）
                try
                {
                    var json = await resp.Content.ReadAsStringAsync(cancellationToken);
                    content = ExtractContentFromJson(json);
                }
                catch (Exception ex)
                {
                    return Fail($"视觉模型响应读取失败：{ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                // 推理模型可能因 max_tokens 不足在推理中途耗尽 token，未输出 content（JSON）
                if (!string.IsNullOrWhiteSpace(reasoning))
                {
                    var preview = reasoning.Length > 200 ? reasoning[..200] : reasoning;
                    return Fail($"模型推理耗尽 token 预算，未生成决策 JSON。推理末尾：{preview}。请增大 max_tokens 或使用非推理模型。");
                }
                return Fail("视觉模型返回空内容。可能是模型不支持图片输入或被内容过滤。");
            }

            var decision = ParseDecision(content);
            if (decision is null)
            {
                var raw = content.Length > 400 ? content[..400] : content;
                return Fail($"模型未输出可解析的 JSON 决策。原文前 400 字：{raw}");
            }

            return decision;
        }
    }

    /// <summary>
    /// 读取 SSE 流式响应，分别累积 content 和 reasoning_content 片段。
    /// streaming 模式下 Cloudflare 看到数据持续流动不会返回 524。
    /// 兼容：data: 和 data:（不带空格）两种 SSE 格式；
    /// 推理模型的 reasoning_content 字段（与 content 分离，不混入）；非 SSE 响应的 JSON fallback。
    /// </summary>
    private async Task<(string Content, string Reasoning)> ReadSseStreamAsync(HttpResponseMessage resp, CancellationToken cancellationToken)
    {
        var contentBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        var rawBuilder = new StringBuilder();
        var hasSseLines = false;
        string? finishReason = null;

        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;

            rawBuilder.AppendLine(line);

            // SSE 格式：每行以 "data:" 开头（冒号后空格可选，SSE 规范允许 data: 和 data: text）
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            hasSseLines = true;
            // 去掉 "data:" 前缀，再跳过至多一个前导空格
            var data = line["data:".Length..];
            if (data.StartsWith(' '))
                data = data[1..];

            if (data == "[DONE]")
                break;

            // 解析 JSON chunk，提取 delta.content 或 delta.reasoning_content
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];

                    // 检查 finish_reason
                    if (choice.TryGetProperty("finish_reason", out var fr) &&
                        fr.ValueKind == JsonValueKind.String)
                    {
                        finishReason = fr.GetString();
                    }

                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        // content 是模型的正式输出（JSON 决策）
                        if (delta.TryGetProperty("content", out var c) &&
                            c.ValueKind == JsonValueKind.String)
                        {
                            contentBuilder.Append(c.GetString());
                        }
                        // reasoning_content 是推理模型的思考过程（DeepSeek-R1、o1 等）
                        // 与 content 分离——思考过程不是 JSON，混入会导致 ParseDecision 失败
                        if (delta.TryGetProperty("reasoning_content", out var rc) &&
                            rc.ValueKind == JsonValueKind.String)
                        {
                            reasoningBuilder.Append(rc.GetString());
                        }
                    }
                }
            }
            catch
            {
                // 单个 chunk 解析失败，跳过继续
            }
        }

        // 如果没有 SSE 格式的行，尝试将整个响应作为常规 JSON 解析
        // （某些代理设置 Content-Type: text/event-stream 但实际缓冲返回了完整 JSON）
        if (!hasSseLines)
        {
            var raw = rawBuilder.ToString().Trim();
            if (raw.StartsWith('{'))
            {
                var extracted = ExtractContentFromJson(raw);
                if (!string.IsNullOrWhiteSpace(extracted))
                    return (extracted, "");
            }
        }

        // 如果内容为空且 finish_reason 是 content_filter，附加诊断信息
        if (contentBuilder.Length == 0 && finishReason == "content_filter")
        {
            return ("", reasoningBuilder.ToString()); // 返回空 content 让上层报错
        }

        return (contentBuilder.ToString(), reasoningBuilder.ToString());
    }

    /// <summary>从常规 JSON 响应中提取 choices[0].message.content。</summary>
    private static string? ExtractContentFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                if (choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                {
                    return c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetRawText();
                }
            }
        }
        catch { }
        return null;
    }

    private static AgentDecision Fail(string reason) => new()
    {
        Observation = reason,
        Thought = "决策失败",
        Function = "none",
        Args = string.Empty,
        Status = "FAIL",
        Comment = reason
    };

    /// <summary>把图片缩放到长边 ≤ maxLongSide 并转 JPEG（85 质量），大幅降低 base64 体积。</summary>
    private static byte[] ResizeForVision(byte[] sourceBytes, int maxLongSide)
    {
        using var ms = new MemoryStream(sourceBytes);
        using var src = System.Drawing.Image.FromStream(ms);
        var scale = Math.Min(1.0, (double)maxLongSide / Math.Max(src.Width, src.Height));
        var w = (int)(src.Width * scale);
        var h = (int)(src.Height * scale);
        if (w < 1) w = 1;
        if (h < 1) h = 1;

        using var dst = new Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(dst))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, w, h);
        }

        using var outMs = new MemoryStream();
        // 保存为 JPEG
        var jpegEncoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(e => e.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        var parms = new System.Drawing.Imaging.EncoderParameters(1);
        parms.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        if (jpegEncoder is not null)
        {
            dst.Save(outMs, jpegEncoder, parms);
        }
        else
        {
            dst.Save(outMs, System.Drawing.Imaging.ImageFormat.Jpeg);
        }
        return outMs.ToArray();
    }

    private static string BuildDecisionPrompt(string request, IReadOnlyList<AnnotatedControl> controls, string history, int step)
    {
        var sb = new StringBuilder();
        var (sw, sh) = Win32ScreenSize();
        sb.AppendLine($"User Request: {request}");
        sb.AppendLine($"屏幕分辨率：{sw}x{sh} 像素");
        sb.AppendLine();
        sb.AppendLine("[Current Control List]");
        sb.AppendLine("每个控件的格式：编号 | 类型 | 名称 | 位置");
        foreach (var c in controls.Take(80))
        {
            sb.AppendLine($"- [{c.Id}] {c.TypeName} | {c.Name} | ({c.Left},{c.Top}) {c.Width}x{c.Height}");
        }

        sb.AppendLine();
        sb.AppendLine("[Step History]");
        sb.AppendLine(string.IsNullOrWhiteSpace(history) ? "(刚开始，无历史)" : history);
        sb.AppendLine();
        sb.AppendLine($"当前是第 {step} 步（最多 {MaxSteps} 步）。请只输出 ONE step 的 JSON 决策。");
        return sb.ToString();
    }

    private static AgentDecision? ParseDecision(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        // 策略 1：从 ```json 代码块中提取（某些模型会用 markdown 包裹 JSON）
        var codeBlockStart = content.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (codeBlockStart >= 0)
        {
            var jsonStart = codeBlockStart + "```json".Length;
            var codeBlockEnd = content.IndexOf("```", jsonStart, StringComparison.OrdinalIgnoreCase);
            if (codeBlockEnd > jsonStart)
            {
                var jsonFromBlock = content.Substring(jsonStart, codeBlockEnd - jsonStart).Trim();
                var decision = TryParseJson(jsonFromBlock);
                if (decision is not null) return decision;
            }
        }

        // 策略 2：暴力提取——第一个 { 到最后一个 }
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var json = content[start..(end + 1)];

        return TryParseJson(json);
    }

    private static AgentDecision? TryParseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var observation = root.TryGetProperty("observation", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "";
            var thought = root.TryGetProperty("thought", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            var comment = root.TryGetProperty("comment", out var cm) && cm.ValueKind == JsonValueKind.String ? cm.GetString() ?? "" : "";

            string function = "none", args = "", status = "CONTINUE";
            if (root.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object)
            {
                if (action.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.String) function = f.GetString() ?? "none";
                args = action.TryGetProperty("args", out var a) ? a.GetRawText().Trim('"') : "";
                if (action.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String) status = s.GetString() ?? "CONTINUE";
            }

            return new AgentDecision
            {
                Observation = observation,
                Thought = thought,
                Comment = comment,
                Function = function,
                Args = args,
                Status = status
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>执行模型决策的动作。动作集移植自 UFO 的 api.yaml。</summary>
    private string ExecuteAction(AgentDecision decision, IReadOnlyList<AnnotatedControl> controls)
    {
        var args = ParseArgs(decision.Args);
        switch (decision.Function.ToLowerInvariant())
        {
            case "click_input":
                {
                    if (!args.TryGetValue("control", out var ctrl) || !int.TryParse(ctrl?.ToString(), out var id))
                    {
                        return "click_input 缺少 control 参数";
                    }

                    var c = controls.FirstOrDefault(x => x.Id == id);
                    if (c is null) return $"控件 {id} 不存在";
                    var (x, y) = c.Center;
                    var button = args.TryGetValue("button", out var b) ? b?.ToString() ?? "left" : "left";
                    var dbl = args.TryGetValue("double", out var d) && bool.TryParse(d?.ToString(), out var dd) && dd;
                    InputExecutor.Click(x, y, button, dbl);
                    return $"已点击控件 {id}（{c.Name}）";
                }
            case "set_edit_text":
                {
                    if (!args.TryGetValue("control", out var ctrl) || !int.TryParse(ctrl?.ToString(), out var id))
                    {
                        return "set_edit_text 缺少 control 参数";
                    }

                    var c = controls.FirstOrDefault(x => x.Id == id);
                    if (c is null) return $"控件 {id} 不存在";
                    var (x, y) = c.Center;
                    var text = args.TryGetValue("text", out var tx) ? tx?.ToString() ?? "" : "";
                    var clear = args.TryGetValue("clear_current_text", out var cl) && bool.TryParse(cl?.ToString(), out var clb) && clb;
                    InputExecutor.Click(x, y);
                    Thread.Sleep(80);
                    if (clear)
                    {
                        InputExecutor.KeyboardInput("{VK_CONTROL}a");
                        Thread.Sleep(40);
                    }
                    InputExecutor.TypeText(text);
                    return $"已在控件 {id} 输入文本";
                }
            case "keyboard_input":
                {
                    var keys = args.TryGetValue("keys", out var k) ? k?.ToString() ?? "" : "";
                    InputExecutor.KeyboardInput(keys);
                    return $"已发送键盘输入：{keys}";
                }
            case "wheel_mouse_input":
                {
                    if (!args.TryGetValue("control", out var ctrl) || !int.TryParse(ctrl?.ToString(), out var id))
                    {
                        return "wheel_mouse_input 缺少 control 参数";
                    }

                    var c = controls.FirstOrDefault(x => x.Id == id);
                    if (c is null) return $"控件 {id} 不存在";
                    var (x, y) = c.Center;
                    var dist = args.TryGetValue("wheel_dist", out var wd) && int.TryParse(wd?.ToString(), out var dv) ? dv : -3;
                    InputExecutor.Click(x, y);
                    InputExecutor.MouseWheel(dist);
                    return $"已滚轮 {dist}";
                }
            case "click_on_coordinates":
                {
                    if (!args.TryGetValue("x", out var xv) || !args.TryGetValue("y", out var yv) ||
                        !float.TryParse(xv?.ToString(), out var fx) || !float.TryParse(yv?.ToString(), out var fy))
                    {
                        return "click_on_coordinates 需要 x, y（0~1 比例）";
                    }

                    var (sw, sh) = Win32ScreenSize();
                    var px = (int)(fx * sw);
                    var py = (int)(fy * sh);
                    InputExecutor.Click(px, py);
                    return $"已点击坐标 ({fx},{fy})";
                }
            case "launch_application":
                {
                    var appName = args.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(appName))
                    {
                        return "launch_application 缺少 name 参数";
                    }

                    var exePath = AppLauncher.ResolveAppExecutable(appName);
                    if (exePath is null)
                    {
                        return $"未找到应用「{appName}」的安装路径或快捷方式";
                    }

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = true });
                    return $"已启动应用「{appName}」";
                }
            case "drag_on_coordinates":
                {
                    if (!args.TryGetValue("start_x", out var sxv) || !args.TryGetValue("start_y", out var syv) ||
                        !args.TryGetValue("end_x", out var exv) || !args.TryGetValue("end_y", out var eyv) ||
                        !float.TryParse(sxv?.ToString(), out var sfx) || !float.TryParse(syv?.ToString(), out var sfy) ||
                        !float.TryParse(exv?.ToString(), out var efx) || !float.TryParse(eyv?.ToString(), out var efy))
                    {
                        return "drag_on_coordinates 需要 start_x, start_y, end_x, end_y（0~1 比例）";
                    }

                    var (sw, sh) = Win32ScreenSize();
                    var startX = (int)(sfx * sw);
                    var startY = (int)(sfy * sh);
                    var endX = (int)(efx * sw);
                    var endY = (int)(efy * sh);
                    InputExecutor.Click(startX, startY);
                    Thread.Sleep(50);
                    InputExecutor.DragTo(endX, endY);
                    return $"已从 ({sfx},{sfy}) 拖拽到 ({efx},{efy})";
                }
            case "minimize_all_windows":
                {
                    // LLM 驱动的窗口最小化：最小化所有窗口（当前前台窗口除外）。
                    // 释放上一次的 scope（如有），确保不会叠加多个 scope。
                    var fg = GetForegroundWindow();
                    _minimizeScope?.Dispose();
                    _minimizeScope = Win32Interop.MinimizeAllWindows(fg);
                    Thread.Sleep(200); // 等待最小化动画完成
                    return "已最小化所有窗口（当前前台窗口除外）";
                }
            case "summary":
                return "已观察当前界面，等待下一步操作";
            case "texts":
                {
                    if (!args.TryGetValue("control", out var ctrl) || !int.TryParse(ctrl?.ToString(), out var id))
                    {
                        return "texts 缺少 control 参数";
                    }

                    var c = controls.FirstOrDefault(x => x.Id == id);
                    if (c is null) return $"控件 {id} 不存在";
                    var text = c.GetText();
                    return string.IsNullOrEmpty(text)
                        ? $"控件 {id} 未读取到文本"
                        : $"控件 {id} 文本：{text}";
                }
            case "annotation":
                return "仅观察动作，无副作用";
            default:
                return $"未知动作：{decision.Function}";
        }
    }

    private static Dictionary<string, object?> ParseArgs(string argsRaw)
    {
        // 模型可能返回 JSON 字符串或裸字符串。尽量解析成 dict。
        if (string.IsNullOrWhiteSpace(argsRaw)) return new();
        argsRaw = argsRaw.Trim();
        try
        {
            if (argsRaw.StartsWith("{"))
            {
                using var doc = JsonDocument.Parse(argsRaw);
                var result = new Dictionary<string, object?>();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    result[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString(),
                        JsonValueKind.Number => p.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => p.Value.GetRawText()
                    };
                }
                return result;
            }
        }
        catch { /* fallthrough */ }

        return new() { ["text"] = argsRaw };
    }

    /// <summary>
    /// 计算动作签名，用于重复检测。按动作类型提取关键区分参数：
    /// click_on_coordinates 用 x/y，keyboard_input 用 keys，launch_application 用 name 等。
    /// 这样连续 3 步 click_on_coordinates 不同坐标不会被误判为循环。
    /// </summary>
    private static string ComputeActionSignature(string function, Dictionary<string, object?> args)
    {
        var f = function.ToLowerInvariant();
        static string Val(Dictionary<string, object?> a, string k)
            => a.TryGetValue(k, out var v) ? (v?.ToString() ?? "null") : "null";
        return f switch
        {
            "click_input" or "texts" => $"{f}|ctrl={Val(args, "control")}",
            "set_edit_text" => $"{f}|ctrl={Val(args, "control")}|text={Val(args, "text")}",
            "wheel_mouse_input" => $"{f}|ctrl={Val(args, "control")}|dist={Val(args, "wheel_dist")}",
            "click_on_coordinates" => $"{f}|x={Val(args, "x")},y={Val(args, "y")}",
            "drag_on_coordinates" => $"{f}|{Val(args, "start_x")},{Val(args, "start_y")}->{Val(args, "end_x")},{Val(args, "end_y")}",
            "keyboard_input" => $"{f}|keys={Val(args, "keys")}",
            "launch_application" => $"{f}|name={Val(args, "name")}",
            _ => $"{f}|{args.Count}"
        };
    }

    /// <summary>任务是否涉及微信（中英文关键词）。</summary>
    private static bool MentionsWeChat(string request)
    {
        return request.Contains("微信", StringComparison.OrdinalIgnoreCase)
               || request.Contains("WeChat", StringComparison.OrdinalIgnoreCase)
               || request.Contains("wechat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断任务是否涉及"打开新应用"（而非在现有窗口上操作）。</summary>
    private static bool IsOpenAppTask(string request)
    {
        var lower = request.ToLowerInvariant();
        return lower.Contains("打开") || lower.Contains("启动") || lower.Contains("开启")
               || lower.Contains("open ") || lower.Contains("launch ")
               || lower.Contains("start ") || lower.Contains("运行");
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private static (int w, int h)? _screenSizeCache;
    private static (int w, int h) Win32ScreenSize()
    {
        if (_screenSizeCache is { } cached) return cached;
        var size = (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        _screenSizeCache = size;
        return size;
    }

    /// <summary>System prompt：移植自 UFO app_agent.yaml 的 system 字段（精简 + 中文化关键约束）。</summary>
    private const string SystemPromptText = """
你是一个 Windows 电脑控制助手。你会收到当前桌面的全屏带标注截图（每个可交互控件用按类型颜色编码的数字编号）和控件列表。
你的任务是：根据用户请求和当前桌面状态，选择**一个**控件或坐标并执行**一步**动作，逐步完成任务。

【输入说明】
- 截图：全屏截图，每个数字对应控件列表里同编号的控件。截图包含任务栏和系统托盘。
  控件边框和编号背景颜色按类型编码：
  - 蓝色(#2196F3)：Button 按钮
  - 绿色(#4CAF50)：Edit/TextBox 输入框
  - 橙色(#FF9800)：ListItem/TreeItem 列表项/树项
  - 紫色(#9C27B0)：Hyperlink 超链接
  - 青色(#00BCD4)：ComboBox 下拉框
  - 棕色(#795548)：Menu/MenuItem 菜单项
  - 红色(#F44336)：其他类型
  - 粗红色边框：上一步操作过的控件位置（帮助你追踪操作效果，确认上一步是否产生了预期变化）
- 控件列表：[编号] 类型 | 名称 | (坐标) 宽x高。坐标是屏幕绝对像素坐标。

【可执行动作】（在 action.function 里选一个）
- click_input：点击某控件。args 形如 {"control": <编号>, "button": "left", "double": false}
- set_edit_text：在输入框输入文本。args 形如 {"control": <编号>, "text": "要输入的内容", "clear_current_text": true}
- keyboard_input：发送快捷键/特殊键。args 形如 {"keys": "{VK_CONTROL}c"}。常见：{VK_RETURN}回车、{VK_CONTROL}、{VK_SHIFT}、{VK_MENU}Alt、{VK_TAB}、{VK_BACK}退格。连按写成 {VK_TAB 2}。
- wheel_mouse_input：滚动。args 形如 {"control": <编号>, "wheel_dist": -3}（正=上，负=下）
- click_on_coordinates：控件列表里没有时，按比例坐标点击。args 形如 {"x": 0.5, "y": 0.5}（左上角为 0,0，右下角为 1,1）
- drag_on_coordinates：从一点拖拽到另一点。args 形如 {"start_x": 0.3, "start_y": 0.4, "end_x": 0.7, "end_y": 0.6}（0~1 比例坐标）。用于拖动滑块、移动文件、调整窗口大小等场景。
- launch_application：通过桌面快捷方式或开始菜单启动应用。args 形如 {"name": "微信"}。当用户要求打开某个应用且截图里看不到该应用窗口时，优先使用此动作。
- summary：仅观察描述，不操作。当需要先看清界面或等待加载时用。返回"已观察当前界面，等待下一步操作"。
- texts：读取某控件的文本内容。args 形如 {"control": <编号>}。用于读取输入框、文档等的文本内容并带回结果。
- minimize_all_windows：最小化所有窗口（当前前台窗口除外），清理桌面遮挡。当截图里大量无关窗口遮挡目标时使用。args 为空 {}。

【响应格式】必须只输出一个 JSON，不要 Markdown、不要多余解释：
{
  "observation": "描述当前截图里看到的内容和当前状态，必要时对比上一步。注意观察粗红色边框标记的上一步操作控件是否产生了预期效果。",
  "thought": "这一步为什么这么做，逻辑推理。",
  "action": {
    "function": "动作名",
    "args": { ... },
    "status": "CONTINUE | FINISH | FAIL | CONFIRM"
  },
  "comment": "（任务完成或失败时必填）结果摘要，给用户看的。"
}

【status 含义】
- CONTINUE：还没做完，需要继续下一步。
- FINISH：当前任务已完成，不再需要操作。
- FAIL：无法完成（界面不对、卡住、重复无效动作）。
- CONFIRM：这一步是敏感操作（如发送消息的「发送」按钮），需要用户确认——但你仍按正常动作输出，确认由上层处理。

【关键规则】
- 每次只输出一步动作。
- 必须基于截图和控件列表选择，不要凭空编造编号。
- 不要重复已经执行过且无效的动作。系统会检测连续 3 步执行相同的 function + 相同关键参数（如 click_on_coordinates 的 x/y 坐标），检测到重复将自动中止任务并返回 FAIL。
- 完成后立即输出 FINISH，不要多余操作。
- 你的输出必须是纯 JSON，能被 json.loads 解析，否则会出错。
- 应用启动策略：用户要求打开某个应用时，先检查屏幕右下角系统托盘是否有该应用图标——有则说明应用已登录/后台运行，用 click_on_coordinates 点击托盘图标来激活窗口（注意：微信、QQ等在已登录状态下，运行桌面快捷方式会导致多账户登录，所以优先检查托盘）；如果托盘没有该应用，用 launch_application 通过桌面快捷方式启动；如果 launch_application 返回未找到，再用 click_on_coordinates 点击桌面上的快捷方式图标。
- 打开新应用后，用 summary 动作等待 2-3 秒让窗口加载完成，不要急于操作。
- 如果某个应用窗口出现后需要等待（如登录、加载），用 summary 动作观察状态，不要急于操作。
- click_on_coordinates 和 drag_on_coordinates 的坐标都是 0~1 的比例值。屏幕分辨率会在提示中给出，乘以分辨率即为像素坐标。
- 窗口管理：不要在任务开始时自动最小化窗口。只有当无关窗口严重遮挡目标应用、导致你无法看清界面或无法操作时，才使用 minimize_all_windows。任务完成后不需要恢复窗口。
- 不要主动打开文件资源管理器（explorer.exe），除非用户明确要求浏览文件或打开磁盘。
- 打开特定磁盘或文件夹时：用 keyboard_input 发送 {VK_LWIN}e 打开文件资源管理器，然后用 click_input 或 click_on_coordinates 点击目标磁盘。仔细确认盘符（如"本地磁盘 (E:)"），不要点错相邻磁盘。
- 导航到文件夹后，如果目标路径在地址栏可见，也可以用 set_edit_text 在地址栏输入路径（如"E:\"）直接跳转。
""";
}

/// <summary>模型的一步决策。</summary>
internal sealed class AgentDecision
{
    public string Observation { get; set; } = "";
    public string Thought { get; set; } = "";
    public string Function { get; set; } = "none";
    public string Args { get; set; } = "";
    public string Status { get; set; } = "CONTINUE";
    public string Comment { get; set; } = "";
}
