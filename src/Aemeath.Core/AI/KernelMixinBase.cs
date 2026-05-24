using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
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

    private static async Task<ChatMessageContentItemCollection> BuildUserContentItemsAsync(
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
                await AppendImageAttachmentAsync(contentItems, textBuilder, attachment, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(attachment.Path, cancellationToken);
            contentItems.Add(new ImageContent(bytes, attachment.MimeType));
            textBuilder.AppendLine("这是一张随消息一同发送的图片，请结合图片内容回答。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            textBuilder.AppendLine($"图片读取失败：{ex.Message}");
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

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.##} MB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:0.##} KB" : $"{bytes} B";
    }
}

