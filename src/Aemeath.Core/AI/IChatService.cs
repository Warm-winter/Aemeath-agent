namespace Aemeath.Core.AI;

/// <summary>
/// AI 聊天服务接口
/// 提供与大模型对话的核心功能
/// </summary>
public interface IChatService
{
    /// <summary>
    /// 当前使用的 AI 助手名称
    /// </summary>
    string CurrentAssistantName { get; }

    /// <summary>
    /// 是否正在处理请求
    /// </summary>
    bool IsProcessing { get; }

    /// <summary>
    /// 发送消息并获取响应（非流式）
    /// </summary>
    /// <param name="message">用户消息</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>AI 响应文本</returns>
    Task<string> SendMessageAsync(string message, CancellationToken cancellationToken = default);

    Task<string> SendMessageAsync(string message, IReadOnlyList<ChatAttachment>? attachments, CancellationToken cancellationToken = default);

    Task<string> SummarizeAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送消息并获取流式响应
    /// </summary>
    /// <param name="message">用户消息</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>流式响应文本片段</returns>
    IAsyncEnumerable<string> SendMessageStreamingAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// 清空对话历史
    /// </summary>
    void ClearHistory();

    /// <summary>
    /// 切换 AI Provider
    /// </summary>
    /// <param name="providerName">Provider 名称（当前建议 OpenAI）</param>
    /// <param name="apiKey">API Key</param>
    /// <param name="endpoint">自定义端点（可选）</param>
    /// <returns>是否切换成功</returns>
    Task<bool> SwitchProviderAsync(string providerName, string apiKey, string? endpoint = null);

    /// <summary>
    /// 注册工具函数（用于 Function Calling）
    /// </summary>
    /// <param name="toolName">工具名称</param>
    /// <param name="description">工具描述</param>
    /// <param name="handler">工具处理函数</param>
    void RegisterTool(string toolName, string description, Func<string, Task<string>> handler);
}
