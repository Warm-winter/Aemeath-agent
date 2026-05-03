using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Aemeath.Core.AI;

/// <summary>
/// Kernel 混合基类
/// 提供 Semantic Kernel 的基础功能和上下文管理
/// </summary>
public abstract class KernelMixinBase
{
    protected Kernel? _kernel;
    protected IChatCompletionService? _chatService;
    protected ChatHistory _chatHistory;
    protected string _systemPrompt;
    protected bool _isInitialized;

    /// <summary>
    /// 当前 Provider 名称
    /// </summary>
    public string ProviderName { get; protected set; } = "Unknown";

    /// <summary>
    /// 是否已初始化
    /// </summary>
    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// 聊天历史
    /// </summary>
    public ChatHistory ChatHistory => _chatHistory;

    protected KernelMixinBase(string systemPrompt)
    {
        _systemPrompt = systemPrompt;
        _chatHistory = new ChatHistory(systemPrompt);
    }

    /// <summary>
    /// 初始化 Kernel
    /// </summary>
    /// <param name="apiKey">API Key</param>
    /// <param name="endpoint">自定义端点（可选）</param>
    /// <returns>初始化任务</returns>
    public abstract Task InitializeAsync(string apiKey, string? endpoint = null);

    /// <summary>
    /// 构建 Kernel 实例
    /// </summary>
    /// <param name="builder">KernelBuilder</param>
    /// <returns>配置后的 Kernel</returns>
    protected abstract Kernel BuildKernel(IKernelBuilder builder);

    /// <summary>
    /// 发送消息并获取响应
    /// </summary>
    /// <param name="message">用户消息</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>AI 响应</returns>
    public async Task<string> SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        
        _chatHistory.AddUserMessage(message);

        ChatMessageContent response;
        try
        {
            var settings = new OpenAIPromptExecutionSettings
            {
#pragma warning disable SKEXP0001
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
#pragma warning restore SKEXP0001
            };

            response = await _chatService!.GetChatMessageContentAsync(
                _chatHistory,
                executionSettings: settings,
                kernel: _kernel,
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex) when (IsToolCallIdException(ex))
        {
            var fallbackSettings = new OpenAIPromptExecutionSettings();
            response = await _chatService!.GetChatMessageContentAsync(
                _chatHistory,
                executionSettings: fallbackSettings,
                kernel: _kernel,
                cancellationToken: cancellationToken
            );
        }

        var text = response.Content ?? string.Empty;
        _chatHistory.AddAssistantMessage(text);
        return text;
    }

    /// <summary>
    /// 发送流式消息
    /// </summary>
    /// <param name="message">用户消息</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>流式响应片段</returns>
    public async IAsyncEnumerable<string> SendMessageStreamingAsync(
        string message, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await SendMessageAsync(message, cancellationToken);
        const int chunkSize = 28;
        for (int i = 0; i < response.Length; i += chunkSize)
        {
            var len = Math.Min(chunkSize, response.Length - i);
            yield return response.Substring(i, len);
        }
    }

    private static bool IsToolCallIdException(Exception ex)
    {
        if (ex is ArgumentException argumentException &&
            !string.IsNullOrWhiteSpace(argumentException.ParamName) &&
            argumentException.ParamName.Contains("toolCallId", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ex.Message.Contains("toolCallId", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("toolCallld", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 清空聊天历史
    /// </summary>
    public void ClearHistory()
    {
        _chatHistory.Clear();
        _chatHistory.AddSystemMessage(_systemPrompt);
    }

    /// <summary>
    /// 确保已初始化
    /// </summary>
    protected void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("Kernel 未初始化，请先调用 InitializeAsync()");
        }
    }

    /// <summary>
    /// 注册插件
    /// </summary>
    /// <param name="plugin">插件实例</param>
    public void RegisterPlugin(object plugin)
    {
        if (_kernel is not null)
        {
            _kernel.Plugins.AddFromObject(plugin);
        }
    }
}
