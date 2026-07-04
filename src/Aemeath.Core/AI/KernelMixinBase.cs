using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;

namespace Aemeath.Core.AI;

public abstract class KernelMixinBase
{
    private const int MaxTextAttachmentChars = 120_000;

    protected Kernel? _kernel;
    protected IChatCompletionService? _chatService;
    protected ChatHistory _chatHistory;
    protected string _systemPrompt;
    protected bool _isInitialized;

    public string ProviderName { get; protected set; } = "Unknown";
    public bool IsInitialized => _isInitialized;
    /// <summary>
    /// 当前模型是否支持图片输入（视觉能力）。
    /// true: 图片以 ImageContent 直接发送给模型。
    /// false: 图片不直接发送，改为文本提示让模型调用 vision_analyze 工具。
    /// 默认 true（向后兼容），由 AemiChatService 根据模型配置设置。
    /// </summary>
    public bool SupportsVision { get; set; } = true;
    public ChatHistory ChatHistory => _chatHistory;

    protected KernelMixinBase(string systemPrompt)
    {
        _systemPrompt = systemPrompt;
        _chatHistory = new ChatHistory(systemPrompt);
    }

    public abstract Task InitializeAsync(string apiKey, string? endpoint = null);

    protected abstract Kernel BuildKernel(IKernelBuilder builder);

    public Task<string> SendMessageAsync(string message, CancellationToken cancellationToken = default)
        => SendMessageAsync(message, null, cancellationToken);

    public async Task<string> SendMessageAsync(
        string message,
        IReadOnlyList<ChatAttachment>? attachments,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (attachments is { Count: > 0 })
        {
            var contentItems = await BuildUserContentItemsAsync(message, attachments, cancellationToken);
            _chatHistory.AddUserMessage(contentItems);
        }
        else
        {
            _chatHistory.AddUserMessage(message);
        }

        var settings = new OpenAIPromptExecutionSettings
        {
#pragma warning disable SKEXP0001
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
#pragma warning restore SKEXP0001
        };

        // 重试循环：最多重试 3 次（共 4 次尝试），指数退避 1s → 2s → 4s。
        // 仅对瞬态异常（超时/5xx/网络错误等）重试，用户主动取消不重试。
        ChatMessageContent? response = null;
        Exception? lastException = null;
        for (int attempt = 0; attempt <= 3; attempt++)
        {
            try
            {
                response = await _chatService!.GetChatMessageContentAsync(
                    _chatHistory,
                    executionSettings: settings,
                    kernel: _kernel,
                    cancellationToken: cancellationToken);
                break;
            }
            catch (Exception ex) when (attempt < 3 && IsTransientException(ex, cancellationToken))
            {
                Debug.WriteLine($"[retry] 第 {attempt + 1} 次重试: {ex.Message}");
                lastException = ex;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
        }

        if (response is null)
        {
            // 重试耗尽或不可重试异常：抛出原始异常
            throw lastException!;
        }

        var text = response.Content ?? string.Empty;
        _chatHistory.AddAssistantMessage(text);
        return text;
    }

    private async Task<ChatMessageContentItemCollection> BuildUserContentItemsAsync(
        string message,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken)
    {
        var contentItems = new ChatMessageContentItemCollection();
        var textBuilder = new StringBuilder();
        textBuilder.AppendLine(message);

        foreach (var attachment in attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            textBuilder.AppendLine();
            textBuilder.AppendLine($"[附件] {attachment.Name} ({attachment.MimeType}, {FormatBytes(attachment.SizeBytes)})");

            if (attachment.Kind == ChatAttachmentKind.Text)
            {
                await AppendTextAttachmentAsync(textBuilder, attachment, cancellationToken);
                continue;
            }

            if (attachment.Kind == ChatAttachmentKind.Image)
            {
                await AppendImageAttachmentAsync(contentItems, textBuilder, attachment, SupportsVision, cancellationToken);
                continue;
            }

            textBuilder.AppendLine($"文件路径：{attachment.Path}");
            textBuilder.AppendLine("该文件不是可直接读取的文本或图片，必要时请说明无法直接解析其内容。");
        }

        contentItems.Insert(0, new TextContent(textBuilder.ToString().Trim()));
        return contentItems;
    }

    private static async Task AppendTextAttachmentAsync(
        StringBuilder textBuilder,
        ChatAttachment attachment,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = await File.ReadAllTextAsync(attachment.Path, cancellationToken);
            if (text.Length > MaxTextAttachmentChars)
            {
                textBuilder.AppendLine($"以下文本内容已截断到 {MaxTextAttachmentChars} 个字符：");
                text = text[..MaxTextAttachmentChars];
            }
            else
            {
                textBuilder.AppendLine("以下是文本文件内容：");
            }

            textBuilder.AppendLine("```");
            textBuilder.AppendLine(text);
            textBuilder.AppendLine("```");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            textBuilder.AppendLine($"文本读取失败：{ex.Message}");
        }
    }

    private static async Task AppendImageAttachmentAsync(
        ChatMessageContentItemCollection contentItems,
        StringBuilder textBuilder,
        ChatAttachment attachment,
        bool supportsVision,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(attachment.Path))
            {
                textBuilder.AppendLine($"图片文件不存在：{attachment.Path}");
                return;
            }

            var bytes = await File.ReadAllBytesAsync(attachment.Path, cancellationToken);
            if (bytes.Length == 0)
            {
                textBuilder.AppendLine("图片文件为空。");
                return;
            }

            if (supportsVision)
            {
                // 压缩图片：缩放到长边 ≤ 2048px + JPEG 85% 质量，
                // 将 10MB 图片降到 ~200-500KB，避免 Cloudflare 524 超时。
                var compressed = CompressImageForChat(bytes);
                contentItems.Add(new ImageContent(compressed, "image/jpeg"));
                textBuilder.AppendLine($"已附加图片内容（{FormatBytes(compressed.Length)}），请查看并结合图片回答。");
            }
            else
            {
                // 模型不支持视觉：不发送 ImageContent（会导致 API 报错），
                // 改为文本提示。此分支为兜底路径——通常 AemiChatService 会先自动调用
                // vision_analyze 并注入 [图片自动分析结果]；若走到这里说明自动调用未触发
                // （如视觉辅助未配置），仍提示模型可主动调用工具，并明确禁止编造内容。
                textBuilder.AppendLine($"已检测到图片附件 {attachment.Name}，路径 {attachment.Path}。你必须调用 vision_analyze(path=\"{attachment.Path}\", question=\"...\") 获取图片描述后再回答用户问题。严禁编造图片内容。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            textBuilder.AppendLine($"图片读取失败：{ex.Message}，路径：{attachment.Path}");
        }
    }

    /// <summary>
    /// 压缩图片用于聊天上传：缩放到长边 ≤ 2048px、转 JPEG 85% 质量。
    /// 将 10MB 原图降到 ~200-500KB，避免 Cloudflare 524 超时。
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

    public IAsyncEnumerable<string> SendMessageStreamingAsync(
        string message,
        CancellationToken cancellationToken = default)
        => SendMessageStreamingAsync(message, null, cancellationToken);

    public async IAsyncEnumerable<string> SendMessageStreamingAsync(
        string message,
        IReadOnlyList<ChatAttachment>? attachments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (attachments is { Count: > 0 })
        {
            var contentItems = await BuildUserContentItemsAsync(message, attachments, cancellationToken);
            _chatHistory.AddUserMessage(contentItems);
        }
        else
        {
            _chatHistory.AddUserMessage(message);
        }

        var settings = new OpenAIPromptExecutionSettings
        {
#pragma warning disable SKEXP0001
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
#pragma warning restore SKEXP0001
        };

        var builder = new StringBuilder();
        // 阶段一：启动流并获取第一个非空 chunk（带重试）。
        // 重试逻辑封装在辅助方法中，因为 C# 不允许在带 catch 的 try 块内使用 yield。
        var (enumerator, firstChunkText) = await StartStreamWithRetryAsync(settings, cancellationToken);

        // 阶段二：yield 第一个 chunk 并继续流式输出（不再重试）。
        // try-finally 允许包含 yield，确保 enumerator 被释放。
        try
        {
            if (firstChunkText is not null)
            {
                builder.Append(firstChunkText);
                yield return firstChunkText;
            }

            while (await enumerator.MoveNextAsync())
            {
                var text = enumerator.Current.Content;
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }
                builder.Append(text);
                yield return text;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        _chatHistory.AddAssistantMessage(builder.ToString());
    }

    /// <summary>
    /// 启动流式请求并获取第一个非空 chunk，带重试机制。
    /// 仅在第一个 chunk 到达之前重试（最多 3 次，指数退避 1s → 2s → 4s）。
    /// 返回的 enumerator 已定位在第一个非空 chunk 之后，调用方负责继续枚举和释放。
    /// 如果流以空内容结束，firstChunkText 为 null。
    /// </summary>
    private async Task<(IAsyncEnumerator<StreamingChatMessageContent> enumerator, string? firstChunkText)>
        StartStreamWithRetryAsync(OpenAIPromptExecutionSettings settings, CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (true)
        {
            IAsyncEnumerator<StreamingChatMessageContent>? enumerator = null;
            try
            {
                enumerator = _chatService!.GetStreamingChatMessageContentsAsync(
                    _chatHistory,
                    executionSettings: settings,
                    kernel: _kernel,
                    cancellationToken: cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);

                // 尝试获取第一个非空 chunk
                while (await enumerator.MoveNextAsync())
                {
                    var text = enumerator.Current.Content;
                    if (!string.IsNullOrEmpty(text))
                    {
                        return (enumerator, text);
                    }
                }
                // 流正常结束但无内容
                return (enumerator, null);
            }
            catch (Exception ex)
            {
                // 任何异常都先释放 enumerator
                if (enumerator is not null)
                {
                    try { await enumerator.DisposeAsync(); } catch { /* 忽略释放异常 */ }
                }

                // 仅在尚未输出 chunk、可重试、重试次数未耗尽时重试
                if (attempt < 3 && IsTransientException(ex, cancellationToken))
                {
                    Debug.WriteLine($"[retry] 第 {attempt + 1} 次重试: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                    attempt++;
                    continue;
                }

                // 不可重试或重试耗尽：抛出原始异常
                throw;
            }
        }
    }

    public void ClearHistory()
    {
        _chatHistory.Clear();
        _chatHistory.AddSystemMessage(_systemPrompt);
    }

    protected void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("Kernel 未初始化，请先调用 InitializeAsync()");
        }
    }

    /// <summary>
    /// 判断异常是否为可重试的瞬态异常。
    /// 超时（非用户取消）、网络错误、5xx 服务端错误、SK 响应解析异常均视为可重试。
    /// 用户主动取消（cancellationToken 已触发）不重试。
    /// </summary>
    private static bool IsTransientException(Exception ex, CancellationToken cancellationToken)
    {
        // 用户主动取消：不重试
        if (ex is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        // HTTP 请求异常（网络层、DNS、连接失败等）
        if (ex is HttpRequestException)
        {
            return true;
        }

        // 异常消息匹配常见瞬态错误标识
        var msg = ex.Message ?? string.Empty;
        if (msg.Contains("530") || msg.Contains("500") || msg.Contains("502") ||
            msg.Contains("503") || msg.Contains("504") ||
            msg.Contains("bad_response_status_code", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("do_request_failed", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Unknown ChatFinishReason", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // SK 响应解析时可能抛出 NullReferenceException
        if (ex is NullReferenceException)
        {
            return true;
        }

        return false;
    }

    public void RegisterPlugin(object plugin)
    {
        if (_kernel is not null)
        {
            _kernel.Plugins.AddFromObject(plugin);
        }
    }

    public void RegisterPlugin(KernelPlugin plugin)
    {
        if (_kernel is not null)
        {
            _kernel.Plugins.Add(plugin);
        }
    }

    public void ReplacePlugin(KernelPlugin? plugin)
    {
        if (_kernel is null)
        {
            return;
        }

        ReplacePluginCore(plugin);
    }

    private void ReplacePluginCore(KernelPlugin? plugin)
    {
        if (_kernel is null)
        {
            return;
        }

        if (plugin is null)
        {
            var existing = _kernel.Plugins.FirstOrDefault(x => string.Equals(x.Name, "mcp", StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                _kernel.Plugins.Remove(existing);
            }
            return;
        }

        var existingPlugin = _kernel.Plugins.FirstOrDefault(x => string.Equals(x.Name, plugin.Name, StringComparison.OrdinalIgnoreCase));
        if (existingPlugin is not null)
        {
            _kernel.Plugins.Remove(existingPlugin);
        }

        _kernel.Plugins.Add(plugin);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.##} MB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:0.##} KB" : $"{bytes} B";
    }
}

