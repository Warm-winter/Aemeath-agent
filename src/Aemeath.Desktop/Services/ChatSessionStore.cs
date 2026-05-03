using System.Text.Json;

namespace Aemeath.Desktop.Services;

public sealed class ChatSessionStore
{
    private readonly string _filePath;
    private readonly object _sync = new();

    public ChatSessionStore()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Aemeath");
        Directory.CreateDirectory(appDataPath);
        _filePath = Path.Combine(appDataPath, "chat_sessions.json");
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

    public void AppendMessage(string sessionId, string role, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
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

            session.Messages.Add(new ChatMessageRecord
            {
                Role = role,
                Content = content,
                Timestamp = DateTimeOffset.UtcNow
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
                Timestamp = m.Timestamp
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
            return JsonSerializer.Deserialize<ChatSessionDatabase>(json) ?? new ChatSessionDatabase();
        }
        catch
        {
            return new ChatSessionDatabase();
        }
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
}
