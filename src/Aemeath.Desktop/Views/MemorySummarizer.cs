using System.Text;
using System.Text.Json;
using Aemeath.Core.AI;
using Aemeath.Desktop.Services;

namespace Aemeath.Desktop.Views;

/// <summary>
/// 长期记忆总结逻辑（从 ChatWindow 抽出，降低主窗口体积）。
/// 每完成若干轮对话后，调用 AI 把会话压缩成 summary/fact/task/preference 写入长期记忆。
/// 内部用信号量保证同一会话不会并发总结。
/// </summary>
internal sealed class MemorySummarizer
{
    private const int SummaryRoundThreshold = 5;

    private readonly IChatService _chatService;
    private readonly ChatSessionStore _sessionStore;
    private readonly LongTermMemoryStore _memoryStore;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MemorySummarizer(IChatService chatService, ChatSessionStore sessionStore, LongTermMemoryStore memoryStore)
    {
        _chatService = chatService;
        _sessionStore = sessionStore;
        _memoryStore = memoryStore;
    }

    /// <summary>若该会话距上次总结已积累足够轮次，则触发一次 AI 记忆压缩。</summary>
    public async Task UpdateIfNeededAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        if (!await _lock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var session = _sessionStore.GetSession(sessionId);
            if (session is null)
            {
                return;
            }

            var completedRounds = CountCompletedRounds(session.Messages);
            var summarizedRounds = _memoryStore.GetSummarizedRounds(sessionId);
            if (completedRounds - summarizedRounds < SummaryRoundThreshold)
            {
                return;
            }

            var prompt = BuildMemorySummaryPrompt(sessionId, session.Messages, summarizedRounds, completedRounds);
            string rawSummary;
            try
            {
                rawSummary = await _chatService.SummarizeAsync(prompt);
            }
            catch (Exception ex)
            {
                AppLogger.Error("memory", "AI memory summary failed, using fallback", ex);
                rawSummary = BuildFallbackMemorySummary(session.Messages);
            }

            var parsed = ParseMemorySummary(rawSummary);
            if (string.IsNullOrWhiteSpace(parsed.Summary))
            {
                parsed = parsed with { Summary = BuildFallbackMemorySummary(session.Messages) };
            }

            _memoryStore.SaveSummary(
                sessionId,
                completedRounds,
                parsed.Summary,
                parsed.Facts,
                parsed.OpenThreads,
                parsed.Preferences);
            AppLogger.Info("memory", $"long-term memory updated: session={sessionId}, rounds={completedRounds}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("memory", "long-term memory update failed", ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    private string BuildMemorySummaryPrompt(string sessionId, IReadOnlyList<ChatMessageRecord> messages, int summarizedRounds, int completedRounds)
    {
        var recent = messages.TakeLast(20).ToList();
        var existingMemory = _memoryStore.BuildPromptBlock(sessionId, 1200);
        var sb = new StringBuilder();
        sb.AppendLine("请更新 Aemeath 的本地长期记忆。只输出 JSON，不要 Markdown。");
        sb.AppendLine("JSON 格式：{\"summary\":\"...\",\"preferences\":[\"...\"],\"facts\":[\"...\"],\"openThreads\":[\"...\"]}");
        sb.AppendLine("要求：只保留用户偏好、未完成事项、重要事实和本会话摘要；不要编造；不要写寒暄。");
        sb.AppendLine($"已总结到第 {summarizedRounds} 轮；当前完成第 {completedRounds} 轮。");
        if (!string.IsNullOrWhiteSpace(existingMemory))
        {
            sb.AppendLine("已有长期记忆：");
            sb.AppendLine(existingMemory);
        }

        sb.AppendLine("最近对话：");
        foreach (var message in recent)
        {
            var role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "小爱" : "用户";
            sb.Append(role).Append("：").AppendLine(message.Content);
        }

        return sb.ToString();
    }

    private static int CountCompletedRounds(IReadOnlyList<ChatMessageRecord> messages)
    {
        var users = messages.Count(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        var assistants = messages.Count(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        return Math.Min(users, assistants);
    }

    private static string BuildFallbackMemorySummary(IReadOnlyList<ChatMessageRecord> messages)
    {
        var recent = messages.TakeLast(10)
            .Select(m => $"{(string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "小爱" : "用户")}：{m.Content}");
        var text = string.Join("\n", recent).Trim();
        return text.Length <= 900 ? text : text[..900];
    }

    private static MemorySummaryResult ParseMemorySummary(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new MemorySummaryResult(string.Empty, [], [], []);
        }

        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            return new MemorySummaryResult(raw.Trim(), [], [], []);
        }

        try
        {
            var result = JsonSerializer.Deserialize<MemorySummaryDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return new MemorySummaryResult(
                result?.Summary?.Trim() ?? string.Empty,
                CleanList(result?.Preferences),
                CleanList(result?.Facts),
                CleanList(result?.OpenThreads));
        }
        catch
        {
            return new MemorySummaryResult(raw.Trim(), [], [], []);
        }
    }

    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    private static IReadOnlyList<string> CleanList(IEnumerable<string>? values)
        => values?
               .Where(v => !string.IsNullOrWhiteSpace(v))
               .Select(v => v.Trim())
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .Take(12)
               .ToList()
           ?? [];

    private sealed record MemorySummaryResult(
        string Summary,
        IReadOnlyList<string> Preferences,
        IReadOnlyList<string> Facts,
        IReadOnlyList<string> OpenThreads);

    private sealed class MemorySummaryDto
    {
        public string? Summary { get; set; }
        public List<string>? Preferences { get; set; }
        public List<string>? Facts { get; set; }
        public List<string>? OpenThreads { get; set; }
    }
}
