using System.Text;
using System.Text.Json;

namespace Aemeath.Desktop.Services;

public sealed class LongTermMemoryStore
{
    private readonly string _filePath;
    private readonly object _sync = new();

    public LongTermMemoryStore()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Aemeath");
        Directory.CreateDirectory(appDataPath);
        _filePath = Path.Combine(appDataPath, "long_term_memory.json");
    }

    public IReadOnlyList<LongTermMemoryEntry> ListEntries()
    {
        lock (_sync)
        {
            return Load().Memories
                .OrderByDescending(m => m.UpdatedAt)
                .Select(Clone)
                .ToList();
        }
    }

    public int GetSummarizedRounds(string sessionId)
    {
        lock (_sync)
        {
            var db = Load();
            return db.SessionSummarizedRounds.TryGetValue(sessionId, out var rounds) ? rounds : 0;
        }
    }

    public void SaveSummary(
        string sessionId,
        int summarizedRounds,
        string summary,
        IEnumerable<string> facts,
        IEnumerable<string> openThreads,
        IEnumerable<string> preferences)
    {
        lock (_sync)
        {
            var db = Load();
            var now = DateTimeOffset.UtcNow;
            db.Memories.RemoveAll(m => string.Equals(m.Scope, "session", StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(m.SessionId, sessionId, StringComparison.Ordinal));

            AddEntry(db, "session", sessionId, "summary", summary, summarizedRounds, now);
            foreach (var fact in facts)
            {
                AddEntry(db, "session", sessionId, "fact", fact, summarizedRounds, now);
            }

            foreach (var thread in openThreads)
            {
                AddEntry(db, "session", sessionId, "task", thread, summarizedRounds, now);
            }

            foreach (var preference in preferences)
            {
                AddGlobalEntryIfNew(db, "preference", preference, now);
            }

            db.SessionSummarizedRounds[sessionId] = summarizedRounds;
            Save(db);
        }
    }

    public void UpsertEntry(LongTermMemoryEntry entry)
    {
        lock (_sync)
        {
            var db = Load();
            var existing = db.Memories.FirstOrDefault(m => string.Equals(m.Id, entry.Id, StringComparison.Ordinal));
            if (existing is null)
            {
                entry.Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id;
                entry.CreatedAt = entry.CreatedAt == default ? DateTimeOffset.UtcNow : entry.CreatedAt;
                entry.UpdatedAt = DateTimeOffset.UtcNow;
                db.Memories.Add(Clone(entry));
            }
            else
            {
                existing.Content = entry.Content.Trim();
                existing.Category = string.IsNullOrWhiteSpace(entry.Category) ? existing.Category : entry.Category.Trim();
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }

            Save(db);
        }
    }

    public bool DeleteEntry(string entryId)
    {
        lock (_sync)
        {
            var db = Load();
            var removed = db.Memories.RemoveAll(m => string.Equals(m.Id, entryId, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                Save(db);
            }

            return removed;
        }
    }

    public void ClearSession(string sessionId)
    {
        lock (_sync)
        {
            var db = Load();
            db.Memories.RemoveAll(m => string.Equals(m.SessionId, sessionId, StringComparison.Ordinal));
            db.SessionSummarizedRounds.Remove(sessionId);
            Save(db);
        }
    }

    public void ClearAll()
    {
        lock (_sync)
        {
            Save(new LongTermMemoryDatabase());
        }
    }

    public string BuildPromptBlock(string sessionId, int maxChars = 1600)
    {
        lock (_sync)
        {
            var db = Load();
            var entries = db.Memories
                .Where(m => string.Equals(m.Scope, "global", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(m.SessionId, sessionId, StringComparison.Ordinal))
                .OrderBy(m => string.Equals(m.Scope, "global", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(m => m.Category)
                .ThenByDescending(m => m.UpdatedAt)
                .ToList();

            if (entries.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Content))
                {
                    continue;
                }

                var scope = string.Equals(entry.Scope, "global", StringComparison.OrdinalIgnoreCase) ? "全局" : "本会话";
                sb.Append(scope).Append('/').Append(entry.Category).Append("：").AppendLine(entry.Content.Trim());
                if (sb.Length >= maxChars)
                {
                    break;
                }
            }

            var text = sb.ToString().Trim();
            return text.Length <= maxChars ? text : text[..maxChars];
        }
    }

    private static void AddEntry(
        LongTermMemoryDatabase db,
        string scope,
        string? sessionId,
        string category,
        string content,
        int sourceRound,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        db.Memories.Add(new LongTermMemoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Scope = scope,
            SessionId = sessionId,
            Category = category,
            Content = content.Trim(),
            SourceRound = sourceRound,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private static void AddGlobalEntryIfNew(LongTermMemoryDatabase db, string category, string content, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var normalized = content.Trim();
        var exists = db.Memories.Any(m => string.Equals(m.Scope, "global", StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(m.Category, category, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(m.Content, normalized, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            AddEntry(db, "global", null, category, normalized, 0, now);
        }
    }

    private LongTermMemoryDatabase Load()
    {
        if (!File.Exists(_filePath))
        {
            return new LongTermMemoryDatabase();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<LongTermMemoryDatabase>(json) ?? new LongTermMemoryDatabase();
        }
        catch
        {
            return new LongTermMemoryDatabase();
        }
    }

    private void Save(LongTermMemoryDatabase db)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(_filePath, JsonSerializer.Serialize(db, options));
    }

    private static LongTermMemoryEntry Clone(LongTermMemoryEntry entry)
        => new()
        {
            Id = entry.Id,
            Scope = entry.Scope,
            SessionId = entry.SessionId,
            Category = entry.Category,
            Content = entry.Content,
            SourceRound = entry.SourceRound,
            CreatedAt = entry.CreatedAt,
            UpdatedAt = entry.UpdatedAt
        };
}

public sealed class LongTermMemoryDatabase
{
    public List<LongTermMemoryEntry> Memories { get; set; } = new();
    public Dictionary<string, int> SessionSummarizedRounds { get; set; } = new(StringComparer.Ordinal);
}

public sealed class LongTermMemoryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Scope { get; set; } = "session";
    public string? SessionId { get; set; }
    public string Category { get; set; } = "summary";
    public string Content { get; set; } = string.Empty;
    public int SourceRound { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
