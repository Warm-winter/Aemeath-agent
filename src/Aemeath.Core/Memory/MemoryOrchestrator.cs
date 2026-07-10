using System.Text;
using System.Text.Json;
using Aemeath.Core.Configuration;

namespace Aemeath.Core.Memory;

/// <summary>
/// Mem0 记忆编排器：负责「把对话喂给 Mem0 抽取记忆」和「发送前检索记忆拼进提示词」。
///
/// 这一层替代旧的 MemorySummarizer（每 N 轮 AI 压缩）+ LongTermMemoryStore.BuildPromptBlock。
/// 与旧方案的区别：Mem0 在 add 时内部自动调 LLM 抽取事实，不需要我们再做「每 N 轮」
/// 的显式压缩——每完成一轮就把该轮 user/assistant 直接 add，Mem0 自行去重/抽取。
/// </summary>
public sealed class MemoryOrchestrator
{
    private readonly Func<Mem0ConnectionConfig?> _configFactory;
    private readonly Func<string?> _pythonFactory;
    private readonly object _clientLock = new();
    private Mem0Client? _client;
    private string? _lastPython;
    private Mem0ConnectionConfig? _lastConfig;
    private bool _unavailable; // 依赖未安装时短路，避免反复尝试启动

    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// 可选的诊断日志回调（由桌面层注入 AppLogger）。形参：(level, message, exception)。
    /// </summary>
    public Action<string, string, Exception?>? Diagnostics { get; set; }

    public async Task DeleteAllAsync(Mem0Scope scope, CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        if (client is null)
        {
            return;
        }

        try
        {
            await client.DeleteAllAsync(scope, cancellationToken);
        }
        catch (Exception ex)
        {
            Diagnostics?.Invoke("warn", $"delete_all 失败（忽略）", ex);
            HandleProcessFault(ex);
        }
    }

    public MemoryOrchestrator(Func<Mem0ConnectionConfig?> configFactory, Func<string?> pythonFactory)
    {
        _configFactory = configFactory;
        _pythonFactory = pythonFactory;
    }

    /// <summary>当前是否可用（依赖已装且未短路）。不会真的拉起进程，只看缓存状态。</summary>
    public bool IsAvailable
    {
        get
        {
            if (_unavailable)
            {
                return false;
            }

            lock (_clientLock)
            {
                return _client is { IsRunning: true };
            }
        }
    }

    /// <summary>惰性获取/复用客户端。配置变化（切 Provider）时重建。</summary>
    private Mem0Client? GetClient()
    {
        if (_unavailable)
        {
            return null;
        }

        var config = _configFactory();
        if (config is null)
        {
            return null;
        }

        var python = _pythonFactory();
        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python))
        {
            return null;
        }

        lock (_clientLock)
        {
            // 配置或解释器变了：丢弃旧 client（ Dispose 会在后台），下次按需重建
            if (_client is not null && (_lastPython != python || !SameConfig(_lastConfig, config)))
            {
                try { _client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { /* 忽略 */ }
                _client = null;
            }

            if (_client is null)
            {
                _client = new Mem0Client(python, Mem0DependencyService.DefaultDataDirectory, config);
                _client.Diagnostics = Diagnostics; // 把编排器的日志回调下传给客户端
                _lastPython = python;
                _lastConfig = config;
            }

            return _client;
        }
    }

    /// <summary>标记依赖缺失，进入短路态（直到设置面板重装后调用 Reset）。</summary>
    public void MarkUnavailable(string reason)
    {
        _unavailable = true;
        StatusChanged?.Invoke(this, $"Mem0 不可用：{reason}");
    }

    /// <summary>重置短路态（依赖装好后调用，下次请求会重新初始化客户端）。</summary>
    public void Reset()
    {
        _unavailable = false;
        lock (_clientLock)
        {
            if (_client is not null)
            {
                try { _client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { /* 忽略 */ }
                _client = null;
            }
        }
    }

    /// <summary>把一轮对话（用户消息 + 小爱回复）写入 Mem0。失败静默（不阻断聊天）。</summary>
    public async Task AddTurnAsync(string sessionId, string userMessage, string assistantMessage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage) && string.IsNullOrWhiteSpace(assistantMessage))
        {
            return;
        }

        var client = GetClient();
        if (client is null)
        {
            return;
        }

        var messages = new[]
        {
            new { role = "user", content = userMessage ?? "" },
            new { role = "assistant", content = assistantMessage ?? "" }
        };
        var json = JsonSerializer.Serialize(messages);

        try
        {
            await client.AddAsync(json, Mem0Scope.ForSession(sessionId), infer: true, cancellationToken);
        }
        catch (Exception ex)
        {
            client.Diagnostics?.Invoke("warn", $"add 失败（忽略）", ex);
            HandleProcessFault(ex);
        }
    }

    /// <summary>按用户最新消息检索相关记忆，拼成提示词块（拼到系统上下文，让小爱「想起」偏好/事实）。</summary>
    public async Task<string> BuildRelevantMemoryBlockAsync(string sessionId, string userMessage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return string.Empty;
        }

        var client = GetClient();
        if (client is null)
        {
            return string.Empty;
        }

        string block = string.Empty;
        try
        {
            // 同时检索「会话记忆」和「全局用户记忆」
            var sessionTask = client.SearchAsync(userMessage, Mem0Scope.ForSession(sessionId), topK: 4, cancellationToken);
            var globalTask = client.SearchAsync(userMessage, Mem0Scope.GlobalUser, topK: 6, cancellationToken);
            await Task.WhenAll(sessionTask, globalTask);

            block = FormatMemoryHits(sessionTask.Result, globalTask.Result);
        }
        catch (Exception ex)
        {
            client.Diagnostics?.Invoke("warn", $"search 失败（忽略，记忆不注入）", ex);
            HandleProcessFault(ex);
        }

        return block;
    }

    private static string FormatMemoryHits(JsonElement sessionHits, JsonElement globalHits)
    {
        var memories = new List<(string text, double score)>();
        foreach (var hit in EnumerateResults(sessionHits))
        {
            memories.Add((GetMemoryText(hit), GetScore(hit)));
        }

        foreach (var hit in EnumerateResults(globalHits))
        {
            memories.Add((GetMemoryText(hit), GetScore(hit)));
        }

        if (memories.Count == 0)
        {
            return string.Empty;
        }

        // 去重 + 按相关度排序
        var sb = new StringBuilder();
        sb.AppendLine("【长期记忆（Mem0）】");
        sb.AppendLine("以下是与本次对话相关的、用户与爱弥斯的过往记忆片段。仅用于保持连续性和个性化，不要主动提及「记忆」二字或内部字段。");
        foreach (var (text, score) in memories.DistinctBy(m => m.text, StringComparer.OrdinalIgnoreCase).Take(8))
        {
            sb.Append("- ").AppendLine(text);
        }

        return sb.ToString();
    }

    private static IEnumerable<JsonElement> EnumerateResults(JsonElement resp)
    {
        if (resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                yield return item;
            }
        }
    }

    private static string GetMemoryText(JsonElement hit)
    {
        if (hit.ValueKind == JsonValueKind.Object && hit.TryGetProperty("memory", out var mem) && mem.ValueKind == JsonValueKind.String)
        {
            return mem.GetString() ?? string.Empty;
        }

        return hit.ToString();
    }

    private static double GetScore(JsonElement hit)
    {
        if (hit.ValueKind == JsonValueKind.Object && hit.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number)
        {
            return s.GetDouble();
        }

        return 0;
    }

    /// <summary>进程级故障（桥接崩溃）时进入短路态，避免每条消息都重试拉起进程。</summary>
    private void HandleProcessFault(Exception ex)
    {
        if (ex is Mem0Exception or TimeoutException)
        {
            // 临时性错误不短路；只有「依赖未装」这类持久错误才短路
            if (ex.Message.Contains("未就绪") || ex.Message.Contains("未安装") || ex.Message.Contains("桥接进程意外退出"))
            {
                MarkUnavailable(ex.Message);
            }
        }
    }

    private static bool SameConfig(Mem0ConnectionConfig? a, Mem0ConnectionConfig? b)
    {
        if (a is null || b is null) return false;
        return a.LlmApiKey == b.LlmApiKey
            && a.LlmBaseUrl == b.LlmBaseUrl
            && a.LlmModel == b.LlmModel
            && a.EmbedModel == b.EmbedModel;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_clientLock)
        {
            if (_client is not null)
            {
                try { _client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { /* 忽略 */ }
                _client = null;
            }
        }

        await ValueTask.CompletedTask;
    }
}
