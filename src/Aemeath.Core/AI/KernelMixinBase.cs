using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
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

        var response = await _chatService!.GetChatMessageContentAsync(
            _chatHistory,
            executionSettings: settings,
            kernel: _kernel,
            cancellationToken: cancellationToken);

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
                // 改为文本提示，让模型调用 vision_analyze 工具分析图片。
                textBuilder.AppendLine($"图片已附加但当前模型不支持直接查看图片。");
                textBuilder.AppendLine($"图片路径：{attachment.Path}");
                textBuilder.AppendLine($"请调用 vision_analyze 工具（传入此路径）来获取图片描述，然后基于描述回答用户。");
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
        await foreach (var chunk in _chatService!.GetStreamingChatMessageContentsAsync(
                           _chatHistory,
                           executionSettings: settings,
                           kernel: _kernel,
                           cancellationToken: cancellationToken))
        {
            var text = chunk.Content;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            builder.Append(text);
            yield return text;
        }

        _chatHistory.AddAssistantMessage(builder.ToString());
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

