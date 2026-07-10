using System.Text.Json;
using Aemeath.Core.AI;

namespace Aemeath.Desktop.Services;

public sealed class ChatSessionStore
{
    private readonly string _filePath;
    private readonly object _sync = new();

    public ChatSessionStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Aemeath",
            "chat_sessions.json"))
    {
    }

    internal ChatSessionStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public ChatSessionRecord CreateSession(string? title = null)
    {
        lock (_sync)
        {
            var db = Load();
            var now = DateTimeOffset.UtcNow;
            var session = new ChatSessionRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = string.IsNullOrWhiteSpace(title) ? $"新对话 {now:yyyy-MM-dd HH:mm:ss}" : title.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
                Messages = new List<ChatMessageRecord>()
            };

            db.Sessions.Add(session);
            Save(db);
            return session;
        }
    }

    public IReadOnlyList<ChatSessionRecord> ListSessions()
    {
        lock (_sync)
        {
            var db = Load();
            return db.Sessions
                .OrderByDescending(s => s.UpdatedAt)
                .ToList();
        }
    }

    public ChatSessionRecord? GetSession(string sessionId)
    {
        lock (_sync)
        {
            var db = Load();
            return db.Sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
        }
    }

    public void DeleteSession(string sessionId)
    {
        lock (_sync)
        {
            var db = Load();
            var target = db.Sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
            if (target is null)
            {
                return;
            }

            db.Sessions.Remove(target);
            Save(db);
        }
    }

    public bool RenameSession(string sessionId, string title)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        lock (_sync)
        {
            var db = Load();
            var session = db.Sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
            if (session is null)
            {
                return false;
            }

            session.Title = title.Trim();
            session.UpdatedAt = DateTimeOffset.UtcNow;
            Save(db);
            return true;
        }
    }

    public void AppendMessage(
        string sessionId,
        string role,
        string content,
        IReadOnlyList<ChatAttachment>? attachments = null)
    {
        var attachmentList = attachments ?? Array.Empty<ChatAttachment>();
        if (string.IsNullOrWhiteSpace(content) && attachmentList.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            var db = Load();
            var session = db.Sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
            if (session is null)
            {
                return;
            }

            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) &&
                session.Messages.Count == 0 && IsGeneratedTitle(session.Title))
            {
                var titleSource = string.IsNullOrWhiteSpace(content)
                    ? attachmentList.FirstOrDefault()?.Name ?? string.Empty
                    : content;
                session.Title = BuildTitle(titleSource);
            }
            session.Messages.Add(new ChatMessageRecord
            {
                Role = role,
                Content = content,
                Timestamp = DateTimeOffset.UtcNow,
                Attachments = CloneAttachments(attachmentList)
            });
            session.UpdatedAt = DateTimeOffset.UtcNow;
            Save(db);
        }
    }

    public IReadOnlyList<ChatMessageRecord> GetRecentMessages(string sessionId, int maxMessages)
    {
        lock (_sync)
        {
            var db = Load();
            var session = db.Sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
            if (session is null)
            {
                return Array.Empty<ChatMessageRecord>();
            }

            return session.Messages
                .TakeLast(Math.Max(0, maxMessages))
                .ToList();
        }
    }

    public void ReplaceMessages(string sessionId, IReadOnlyList<ChatMessageRecord> messages)
    {
        lock (_sync)
        {
            var db = Load();
            var session = db.Sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
            if (session is null)
            {
                return;
            }

            session.Messages = messages.Select(m => new ChatMessageRecord
            {
                Role = m.Role,
                Content = m.Content,
                Timestamp = m.Timestamp,
                Attachments = CloneAttachments(m.Attachments)
            }).ToList();
            session.UpdatedAt = DateTimeOffset.UtcNow;
            Save(db);
        }
    }

    private ChatSessionDatabase Load()
    {
        if (!File.Exists(_filePath))
        {
            return new ChatSessionDatabase();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var database = JsonSerializer.Deserialize<ChatSessionDatabase>(json) ?? new ChatSessionDatabase();
            database.Sessions ??= new List<ChatSessionRecord>();
            foreach (var session in database.Sessions)
            {
                session.Messages ??= new List<ChatMessageRecord>();
                foreach (var message in session.Messages)
                {
                    message.Role ??= string.Empty;
                    message.Content ??= string.Empty;
                    message.Attachments ??= new List<ChatAttachment>();
                }
            }
            return database;
        }
        catch
        {
            return new ChatSessionDatabase();
        }
    }


    private static List<ChatAttachment> CloneAttachments(IEnumerable<ChatAttachment>? attachments)
        => attachments?.Select(attachment => attachment with { }).ToList() ?? new List<ChatAttachment>();

    private static bool IsGeneratedTitle(string title)
        => title.StartsWith("\u65b0\u5bf9\u8bdd ", StringComparison.Ordinal);

    private static string BuildTitle(string content)
    {
        var normalized = string.Join(
            " ",
            content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "\u65b0\u5bf9\u8bdd";
        }

        const int maxLength = 30;
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength].TrimEnd() + "\u2026";
    }
    private void Save(ChatSessionDatabase db)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(db, options);
        File.WriteAllText(_filePath, json);
    }
}

public sealed class ChatSessionDatabase
{
    public List<ChatSessionRecord> Sessions { get; set; } = new();
}

public sealed class ChatSessionRecord
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ChatMessageRecord> Messages { get; set; } = new();
}

public sealed class ChatMessageRecord
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public List<ChatAttachment> Attachments { get; set; } = new();
}
