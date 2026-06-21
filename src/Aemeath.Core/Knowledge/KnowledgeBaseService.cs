using System.Reflection;
using System.Text.Json;

namespace Aemeath.Core.Knowledge;

public sealed class KnowledgeBaseService
{
    private readonly Lazy<IReadOnlyList<KnowledgeBaseEntry>> _entries;
    private readonly List<KnowledgeBaseEntry> _extraEntries = new();
    private IReadOnlyList<KnowledgeBaseEntry> _allEntriesCache = Array.Empty<KnowledgeBaseEntry>();
    private bool _cacheDirty = true;

    public KnowledgeBaseService()
    {
        _entries = new Lazy<IReadOnlyList<KnowledgeBaseEntry>>(LoadEntries);
    }

    /// <summary>
    /// 追加额外的知识库条目（例如来自 Skill 的背景资料）。
    /// 这些条目与内置条目一起参与检索，形成互补。
    /// </summary>
    public void AddEntries(IEnumerable<KnowledgeBaseEntry> entries)
    {
        if (entries is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (entry is not null && !_extraEntries.Any(x => string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase)))
            {
                _extraEntries.Add(entry);
            }
        }
        _cacheDirty = true;
    }

    /// <summary>清空外部注入的条目（重新加载 skill 时调用，避免禁用的 skill 条目残留）。</summary>
    public void ClearExtraEntries()
    {
        if (_extraEntries.Count > 0)
        {
            _extraEntries.Clear();
            _cacheDirty = true;
        }
    }

    /// <summary>合并内置条目 + 外部注入条目（带缓存）。</summary>
    private IReadOnlyList<KnowledgeBaseEntry> GetAllEntries()
    {
        if (_cacheDirty)
        {
            var combined = new List<KnowledgeBaseEntry>(_entries.Value.Count + _extraEntries.Count);
            combined.AddRange(_entries.Value);
            combined.AddRange(_extraEntries);
            _allEntriesCache = combined;
            _cacheDirty = false;
        }
        return _allEntriesCache;
    }

    public IReadOnlyList<KnowledgeBaseEntry> Search(string query, int maxResults = 4)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<KnowledgeBaseEntry>();
        }

        var normalizedQuery = Normalize(query);
        var scored = GetAllEntries()
            .Select(entry => new
            {
                Entry = entry,
                Score = Score(entry, normalizedQuery)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxResults))
            .Select(x => x.Entry)
            .ToList();

        return scored;
    }

    public string FormatForPrompt(IReadOnlyList<KnowledgeBaseEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "本地资料库未覆盖该问题；请明确说明资料不足，不要编造。";
        }

        var lines = new List<string>
        {
            "本地权威知识库命中资料："
        };

        foreach (var entry in entries)
        {
            lines.Add($"- {entry.Title}（{entry.Category}）：{entry.Content} 来源：{entry.SourceUrl}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public string SearchText(string query, int maxResults = 4)
    {
        return FormatForPrompt(Search(query, maxResults));
    }

    private static int Score(KnowledgeBaseEntry entry, string normalizedQuery)
    {
        var score = 0;
        score += ContainsAny(normalizedQuery, entry.Title) ? 5 : 0;
        score += ContainsAny(normalizedQuery, entry.Category) ? 2 : 0;

        foreach (var alias in entry.Aliases)
        {
            if (ContainsAny(normalizedQuery, alias))
            {
                score += alias.Length >= 3 ? 4 : 2;
            }
        }

        var contentTokens = new[] { "鸣潮", "爱弥斯", "星炬", "拉贝尔", "隧者", "电子幽灵", "热熔", "长剑", "星海" };
        foreach (var token in contentTokens)
        {
            if (normalizedQuery.Contains(Normalize(token), StringComparison.Ordinal))
            {
                score += entry.Content.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                         entry.Title.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                         entry.Aliases.Any(x => x.Contains(token, StringComparison.OrdinalIgnoreCase))
                    ? 2
                    : 0;
            }
        }

        return score;
    }

    private static bool ContainsAny(string normalizedQuery, string value)
    {
        var normalizedValue = Normalize(value);
        return !string.IsNullOrWhiteSpace(normalizedValue) &&
               (normalizedQuery.Contains(normalizedValue, StringComparison.Ordinal) ||
                normalizedValue.Contains(normalizedQuery, StringComparison.Ordinal));
    }

    private static string Normalize(string text)
    {
        return text.Trim().ToLowerInvariant()
            .Replace(" ", string.Empty)
            .Replace("　", string.Empty)
            .Replace("，", string.Empty)
            .Replace(",", string.Empty)
            .Replace("。", string.Empty)
            .Replace("？", string.Empty)
            .Replace("?", string.Empty);
    }

    private static IReadOnlyList<KnowledgeBaseEntry> LoadEntries()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith("knowledge_base.zh-CN.json", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return Array.Empty<KnowledgeBaseEntry>();
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return Array.Empty<KnowledgeBaseEntry>();
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<KnowledgeBaseEntry>>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return entries ?? new List<KnowledgeBaseEntry>();
        }
        catch
        {
            return Array.Empty<KnowledgeBaseEntry>();
        }
    }
}
