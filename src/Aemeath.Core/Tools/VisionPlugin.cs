using Aemeath.Core.AI;
using Aemeath.Core.Configuration;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Aemeath.Core.Tools;

/// <summary>
/// 图片识别插件：让纯文本模型也能「看」图片。
///
/// 工作原理（移植自 NousResearch/hermes-agent 的 vision_analyze 工具，精简后用 C# 实现）：
/// 当主对话模型不支持视觉时，本工具调用一个**辅助视觉模型**（OpenAI 兼容、支持 image_url 的多模态模型）
/// 把图片描述成文本，再把描述返回给主模型——从而让纯文本模型间接具备图片识别能力。
///
/// 关键设计（来自 hermes 的提示词工程）：
/// - 工具 description 明确列举触发场景（用户提到图片路径/截图/URL 时必须调用），防止模型偷懒不调用。
/// - 发给辅助模型的提示词用「Fully describe and explain everything about this image, then answer...」模板。
///
/// 注意：若主模型本身已支持视觉，图片会通过 ChatAttachment 直接随消息发送（见 KernelMixinBase），
/// 此时模型不需要本工具也能看图。本工具主要服务于「用户给了路径/截图，但主模型是纯文本」的场景。
/// </summary>
public class VisionPlugin
{
    private const string FallbackVisionModel = "gpt-4o-mini";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly Func<(string Model, string Endpoint, string ApiKey)?> _configFactory;

    /// <summary>
    /// 构造。configFactory 在每次调用时返回当前视觉辅助配置（模型/endpoint/key），
    /// 通常传入 AemiChatService.BuildVisionConfig。
    /// </summary>
    public VisionPlugin(Func<(string Model, string Endpoint, string ApiKey)?> configFactory)
    {
        _configFactory = configFactory;
    }

    [KernelFunction("vision_analyze")]
    [Description(
        "Analyze and understand an image so you can answer questions about it. " +
        "Call this whenever the user references an image and you cannot see it directly: " +
        "a filepath in their message (e.g. C:\\...\\photo.png), a screenshot you just took with take_screenshot, " +
        "an http(s) URL of an image, or any file the user asks you to 'look at / read / 看一下 / 识别' that is a picture. " +
        "Returns a detailed text description of the image. Pass the image location in image_source and what you need to know in question. " +
        "Do NOT say you cannot see images or ask the user to describe them — call this tool instead. " +
        "当用户提到任何图片（路径、截图、网址）并询问内容时，必须调用本工具而不是回复看不到。")]
    public async Task<string> AnalyzeImageAsync(
        [Description("图片来源：本地文件绝对路径、或 http(s) 图片网址")] string image_source,
        [Description("你想了解图片的什么，用自然语言提问（如：这张图里有什么文字？这张截图怎么操作？）")] string question)
    {
        if (string.IsNullOrWhiteSpace(image_source))
        {
            return "图片识别失败：image_source（图片来源）不能为空。";
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            question = "请详细描述这张图片的内容。";
        }

        Debug.WriteLine($"[vision-analyze] 调用开始: image_source={image_source}, question={question}");

        try
        {
            var config = _configFactory();
            if (config is null)
            {
                Debug.WriteLine("[vision-analyze] 视觉辅助配置为 null，无法调用");
                return "图片识别失败：尚未配置视觉辅助模型。请在「设置 → 记忆管理」里配置辅助视觉模型，或确保当前 Provider 的模型支持图片。";
            }

            var (model, endpoint, apiKey) = config.Value;
            Debug.WriteLine($"[vision-analyze] 配置: model={model}, endpoint={endpoint}");
            if (string.IsNullOrWhiteSpace(model))
            {
                model = FallbackVisionModel;
            }

            // 取图片并转 base64 data URL（本地路径有白名单校验，URL 则直接传给远端下载）
            var dataUrl = await ResolveAsDataUrlAsync(image_source);
            if (dataUrl is null)
            {
                return $"图片识别失败：无法读取图片来源 {image_source}（路径不在允许范围内或文件不存在）。";
            }

            Debug.WriteLine($"[vision-analyze] 图片已转 data URL (长度={dataUrl.Length}), 开始调用视觉模型...");
            var prompt = "Fully describe and explain everything about this image, then answer the following question:\n\n" + question;
            var sw = Stopwatch.StartNew();
            var description = await CallVisionModelAsync(model, endpoint, apiKey, prompt, dataUrl);
            sw.Stop();
            Debug.WriteLine($"[vision-analyze] 视觉模型返回成功, 描述长度={description.Length}, 耗时={sw.ElapsedMilliseconds}ms");
            // 把来源信息附上，方便主模型后续追问时复用
            return $"[图片识别结果]\n来源：{image_source}\n\n{description}";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[vision-analyze] 调用失败: {ex.Message}");
            return $"图片识别失败：{ex.Message}";
        }
    }

    /// <summary>把本地路径或 http(s) URL 统一转成 base64 data URL。</summary>
    private static async Task<string?> ResolveAsDataUrlAsync(string source)
    {
        source = source.Trim();

        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // 远端图片：下载后转 base64。本地桌面应用场景，不做 SSRF 限制（用户主动指定）。
            try
            {
                var bytes = await HttpClient.GetByteArrayAsync(source);
                var mime = GuessMimeFromUrl(source);
                return ToDataUrl(bytes, mime);
            }
            catch
            {
                return null;
            }
        }

        // 本地路径：走与 FileSystemPlugin 一致的白名单（用户主目录 + 临时目录，禁止 AppData\Aemeath）
        if (!IsAllowedPath(source) || !File.Exists(source))
        {
            return null;
        }

        var data = await File.ReadAllBytesAsync(source);
        if (data.Length == 0)
        {
            return null;
        }

        // 本地图片压缩：缩放到长边 ≤ 2048px + JPEG 85% 质量，
        // 将 10MB 图片降到 ~200-500KB，避免 Cloudflare 524 超时。
        // 压缩后统一为 JPEG，故 mime 用 image/jpeg（与 KernelMixinBase 一致）。
        var compressed = CompressImageForChat(data);
        return ToDataUrl(compressed, "image/jpeg");
    }

    /// <summary>
    /// 压缩图片：缩放到长边 ≤ 2048px + JPEG 85% 质量。
    /// 压缩失败（如 SVG/HEIC 等非标准格式）时回退原始字节，让 API 自行处理。
    /// 实现与 KernelMixinBase.CompressImageForChat 等价。
    /// </summary>
    private static byte[] CompressImageForChat(byte[] sourceBytes)
    {
        try
        {
            using var ms = new MemoryStream(sourceBytes);
            using var src = Image.FromStream(ms);
            const int maxLongSide = 2048;
            var scale = Math.Min(1.0, (double)maxLongSide / Math.Max(src.Width, src.Height));
            // 已经足够小且是 JPEG → 直接返回原数据
            if (scale >= 1.0 && src.RawFormat.Equals(ImageFormat.Jpeg))
            {
                return sourceBytes;
            }

            var w = (int)(src.Width * scale);
            var h = (int)(src.Height * scale);
            if (w < 1) w = 1;
            if (h < 1) h = 1;

            using var dst = new Bitmap(w, h);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }

            using var outMs = new MemoryStream();
            var jpegEncoder = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(e => e.FormatID == ImageFormat.Jpeg.Guid);
            var parms = new EncoderParameters(1);
            parms.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
            if (jpegEncoder is not null)
            {
                dst.Save(outMs, jpegEncoder, parms);
            }
            else
            {
                dst.Save(outMs, ImageFormat.Jpeg);
            }
            return outMs.ToArray();
        }
        catch
        {
            // 压缩失败（如 SVG/HEIC 等非标准格式）→ 返回原始数据，让 API 自行处理
            return sourceBytes;
        }
    }

    private static string ToDataUrl(byte[] bytes, string mime)
        => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

    private static string GuessMimeFromUrl(string url)
    {
        var path = url.Split('?')[0].Split('#')[0];
        return GuessMimeFromExtension(path);
    }

    private static string GuessMimeFromExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "image/jpeg"
        };
    }

    /// <summary>
    /// 调用 OpenAI 兼容的 chat completions 接口，用 image_url 内容块请求视觉描述。
    /// 协议格式与 hermes-agent/auxiliary_client 一致：messages 含一个 image_url + text 的 user 消息。
    /// 对瞬态异常（网络错误 / 5xx / Cloudflare 524）做最多 3 次重试，指数退避 1s → 2s → 4s。
    /// </summary>
    private static async Task<string> CallVisionModelAsync(string model, string endpoint, string apiKey, string prompt, string dataUrl)
    {
        var baseUrl = OpenAIUrlHelper.NormalizeBaseUrlWithDefault(endpoint);
        var url = baseUrl.TrimEnd('/') + "/chat/completions";

        var body = new
        {
            model,
            temperature = 0.1,
            max_tokens = 1500,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            }
        };

        // 预先序列化请求体，重试时复用（HttpClient 发送后 HttpRequestMessage 会被消费，需重新构造）
        var jsonBody = JsonSerializer.Serialize(body);

        Exception? lastException = null;
        for (int attempt = 0; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("Authorization", "Bearer " + apiKey);
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                using var resp = await HttpClient.SendAsync(req);
                var json = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"视觉模型返回 {(int)resp.StatusCode}：{Truncate(json, 300)}");
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        return content.GetString() ?? string.Empty;
                    }
                }

                throw new InvalidOperationException("视觉模型未返回有效内容：" + Truncate(json, 300));
            }
            catch (Exception ex) when (attempt < 3 && IsTransientException(ex))
            {
                Debug.WriteLine($"[vision-retry] 第 {attempt + 1} 次重试: {ex.Message}");
                lastException = ex;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            }
        }

        throw new InvalidOperationException($"视觉模型调用失败（已重试 3 次）：{lastException?.Message}", lastException);
    }

    /// <summary>
    /// 判定异常是否为可重试的瞬态异常：
    /// - OperationCanceledException：用户取消，不重试
    /// - HttpRequestException：网络层错误，重试
    /// - 包含 5xx / Cloudflare 524 / 530 状态码的 InvalidOperationException：重试
    /// </summary>
    private static bool IsTransientException(Exception ex)
    {
        // 用户取消不重试（这里没有 CancellationToken，所以 OperationCanceledException 视为不可重试）
        if (ex is OperationCanceledException) return false;
        // 网络层错误重试
        if (ex is HttpRequestException) return true;
        // InvalidOperationException 中包含 5xx 状态码的也重试
        var msg = ex.Message ?? string.Empty;
        if (msg.Contains("524") || msg.Contains("500") || msg.Contains("502") ||
            msg.Contains("503") || msg.Contains("504") || msg.Contains("530"))
        {
            return true;
        }
        return false;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    /// <summary>与 FileSystemPlugin 一致的路径白名单：用户主目录 + 临时目录，禁止 AppData\Aemeath。</summary>
    private static bool IsAllowedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim());
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var temp = Path.GetTempPath();
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aemeath");

            var inUser = !string.IsNullOrEmpty(userProfile) &&
                         (full.StartsWith(userProfile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(full, userProfile, StringComparison.OrdinalIgnoreCase));
            var inTemp = full.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
            var inAppData = full.StartsWith(appData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(full, appData, StringComparison.OrdinalIgnoreCase);

            return (inUser || inTemp) && !inAppData;
        }
        catch
        {
            return false;
        }
    }
}
