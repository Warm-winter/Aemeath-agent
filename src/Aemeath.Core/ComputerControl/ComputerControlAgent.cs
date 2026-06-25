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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private readonly Func<(string Model, string Endpoint, string ApiKey)?> _visionConfig;

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

        // 微信等自定义渲染 UI 优先走 Win32 直控（UIA 对微信几乎无效）。
        if (MentionsWeChat(request))
        {
            // 微信未运行时尝试启动它（登录需要用户手动完成，启动后等待窗口出现）
            if (Win32Interop.FindWeChatWindow() == IntPtr.Zero)
            {
                progress?.Report("未检测到微信窗口，正在尝试启动微信…");
                WeChatDirectController.TryStartWeChat();
                // 等待最多 15 秒让微信窗口出现（登录界面或主界面）
                for (int i = 0; i < 30 && Win32Interop.FindWeChatWindow() == IntPtr.Zero; i++)
                {
                    await Task.Delay(500, cancellationToken);
                }
            }

            if (Win32Interop.FindWeChatWindow() != IntPtr.Zero)
            {
                progress?.Report("检测到微信任务，使用微信直控方案（绕过 UIA）…");
                var wechat = new WeChatDirectController(m => progress?.Report(m));
                var (handled, result) = await wechat.TryHandleAsync(request, cancellationToken);
                if (handled)
                {
                    return result;
                }
            }
            else
            {
                // 微信没起来，给清晰反馈，而不是盲目让视觉 agent 去点
                return "未能启动微信。请手动打开并登录微信桌面版后，再发送「给某某发消息」的任务。";
            }
        }

        // 任务前最小化所有顶层窗口，避免遮挡目标应用（含桌宠自身窗口）。
        // 注意：目标应用由模型用工具打开后会成为新的前台窗口，最小化发生在应用启动前，
        // 故此处最小化的是「开始任务时已存在的」遮挡窗口；用 using 在任务结束时恢复。
        using var minimizeScope = Win32Interop.MinimizeAllWindows();

        try
        {
            var history = new StringBuilder();
            string? prevScreenshot = null;

            for (int step = 1; step <= MaxSteps; step++)
            {
                progress?.Report($"第 {step} 步：截图并分析当前界面…");
                cancellationToken.ThrowIfCancellationRequested();

                var rawShot = Path.Combine(workDir, $"step{step}_raw.png");
                ScreenCapture.CaptureForeground(rawShot);

                var controls = UiaControlTree.CaptureForeground();
                var annotatedShot = Path.Combine(workDir, $"step{step}_annotated.png");
                try
                {
                    UiaControlTree.Annotate(rawShot, controls, annotatedShot);
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

        // 截图体积治理：DPI 感知后全屏物理像素 PNG 可能数 MB（4K 屏 base64 后超 10MB），
        // 多数视觉 endpoint 会以 413 拒绝。统一缩放到长边 ≤1280，再转 JPEG 降体积。
        string dataUrl;
        try
        {
            var bytes = await File.ReadAllBytesAsync(annotatedShotPath, cancellationToken);
            var compressed = ResizeForVision(bytes, maxLongSide: 1280);
            dataUrl = $"data:image/jpeg;base64,{Convert.ToBase64String(compressed)}";
        }
        catch (Exception ex)
        {
            return Fail($"无法读取/缩放截图：{ex.Message}");
        }

        var prompt = BuildDecisionPrompt(request, controls, history, step);

        var body = new
        {
            model,
            temperature = 0.1,
            max_tokens = 1500,
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

        var baseUrl = NormalizeBaseUrl(endpoint);
        var url = baseUrl.TrimEnd('/') + "/chat/completions";

        HttpResponseMessage resp;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("Authorization", "Bearer " + apiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            resp = await Http.SendAsync(req, cancellationToken);
        }
        catch (Exception ex)
        {
            return Fail($"视觉模型网络请求失败（endpoint={baseUrl}）：{ex.Message}");
        }

        using (resp)
        {
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                // 把真实错误原因带回去，便于用户诊断（401 key 错 / 404 模型名错 / 413 图太大 / 429 限流）
                var detail = json.Length > 400 ? json[..400] : json;
                return Fail($"视觉模型返回 HTTP {(int)resp.StatusCode}：{detail}");
            }

            string? content = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    if (choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                    {
                        content = c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetRawText();
                    }
                }
            }
            catch (Exception ex)
            {
                return Fail($"视觉模型返回无法解析为 JSON：{ex.Message}；原文前 400 字：{(json.Length > 400 ? json[..400] : json)}");
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return Fail("视觉模型返回空内容（choices[0].message.content 为空）。可能是模型不支持图片输入或被内容过滤。");
            }

            var decision = ParseDecision(content);
            if (decision is null)
            {
                // 模型没按 JSON 格式输出——把原文作为 FAIL 原因，便于用户判断模型能力
                var raw = content.Length > 400 ? content[..400] : content;
                return Fail($"模型未输出可解析的 JSON 决策。原文前 400 字：{raw}");
            }

            return decision;
        }
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
        sb.AppendLine("User Request: " + request);
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
        // 提取第一个 JSON 对象
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var json = content[start..(end + 1)];

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
    private static string ExecuteAction(AgentDecision decision, IReadOnlyList<AnnotatedControl> controls)
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
            case "summary":
            case "texts":
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

    /// <summary>任务是否涉及微信（中英文关键词）。</summary>
    private static bool MentionsWeChat(string request)
    {
        return request.Contains("微信", StringComparison.OrdinalIgnoreCase)
               || request.Contains("WeChat", StringComparison.OrdinalIgnoreCase)
               || request.Contains("wechat", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBaseUrl(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return "https://api.openai.com/v1";
        var url = endpoint.Trim();
        var idx = url.IndexOf("/v1", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) url = url[..(idx + 3)];
        else if (!url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) url = url.TrimEnd('/') + "/v1";
        return url;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private static (int w, int h) Win32ScreenSize() => (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));

    /// <summary>System prompt：移植自 UFO app_agent.yaml 的 system 字段（精简 + 中文化关键约束）。</summary>
    private const string SystemPromptText = """
你是一个 Windows 电脑控制助手。你会收到当前应用窗口的带标注截图（每个可交互控件用红色数字编号）和控件列表。
你的任务是：根据用户请求和当前界面状态，选择**一个**控件并执行**一步**动作，逐步完成任务。

【输入说明】
- 截图：每个红色数字对应控件列表里同编号的控件。
- 控件列表：[编号] 类型 | 名称 | (坐标) 宽x高。

【可执行动作】（在 action.function 里选一个）
- click_input：点击某控件。args 形如 {"control": <编号>, "button": "left", "double": false}
- set_edit_text：在输入框输入文本。args 形如 {"control": <编号>, "text": "要输入的内容", "clear_current_text": true}
- keyboard_input：发送快捷键/特殊键。args 形如 {"keys": "{VK_CONTROL}c"}。常见：{VK_RETURN}回车、{VK_CONTROL}、{VK_SHIFT}、{VK_MENU}Alt、{VK_TAB}、{VK_BACK}退格。连按写成 {VK_TAB 2}。
- wheel_mouse_input：滚动。args 形如 {"control": <编号>, "wheel_dist": -3}（正=上，负=下）
- click_on_coordinates：控件列表里没有时，按比例坐标点击。args 形如 {"x": 0.5, "y": 0.5}（左上角为 0,0）
- summary：仅观察描述，不操作。当需要先看清界面时用。
- texts：读取某输入框/文档的文本内容（用于把结果带回去）。

【响应格式】必须只输出一个 JSON，不要 Markdown、不要多余解释：
{
  "observation": "描述当前截图里看到的内容和当前状态，必要时对比上一步。",
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
- 不要重复已经执行过且无效的动作。
- 完成后立即输出 FINISH，不要多余操作。
- 你的输出必须是纯 JSON，能被 json.loads 解析，否则会出错。
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
