using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aemeath.Core.MCP;

public sealed class McpServerStore
{
    private readonly string _serversDirectory;
    private readonly string _legacyConfigPath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public McpServerStore()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aemeath");
        _serversDirectory = Path.Combine(appData, "mcp", "servers");
        _legacyConfigPath = Path.Combine(appData, "mcp_servers.json");
        Directory.CreateDirectory(_serversDirectory);
        TryMigrateLegacyConfig();
    }

    public string ServersDirectory => _serversDirectory;

    public IReadOnlyList<McpServerConfig> ListServers()
    {
        Directory.CreateDirectory(_serversDirectory);
        return Directory.GetFiles(_serversDirectory, "*.json")
            .Select(LoadFile)
            .Where(s => s is not null)
            .Select(s => s!)
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public McpServerConfig? GetServer(string id)
    {
        var safeId = NormalizeId(id);
        var path = GetServerPath(safeId);
        return File.Exists(path) ? LoadFile(path) : null;
    }

    public void SaveServer(McpServerConfig server)
    {
        if (string.IsNullOrWhiteSpace(server.Id))
        {
            server.Id = NormalizeId(server.Name);
        }

        server.Id = NormalizeId(server.Id);
        server.Name = string.IsNullOrWhiteSpace(server.Name) ? server.Id : server.Name.Trim();
        server.UpdatedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(_serversDirectory);

        _fileLock.Wait();
        try
        {
            var path = GetServerPath(server.Id);
            var json = JsonSerializer.Serialize(server, McpConfigJson.Options);
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public bool DeleteServer(string id)
    {
        var path = GetServerPath(NormalizeId(id));
        // 加锁，避免与并发的 SaveServer/LoadFile 竞态（CON-007）。
        _fileLock.Wait();
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public void SetEnabled(string id, bool enabled)
    {
        var server = GetServer(id);
        if (server is null)
        {
            return;
        }

        server.Enabled = enabled;
        SaveServer(server);
    }

    public IReadOnlyList<McpServerConfig> ImportJson(string json)
    {
        var imported = ParseServers(json).ToList();
        foreach (var server in imported)
        {
            SaveServer(server);
        }

        return imported;
    }

    private IEnumerable<McpServerConfig> ParseServers(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) ?? throw new InvalidOperationException("JSON 内容为空。");

        if (root is JsonObject obj && obj["mcpServers"] is JsonObject serversObj)
        {
            foreach (var kvp in serversObj)
            {
                if (kvp.Value is JsonObject serverObj)
                {
                    yield return ParseServer(kvp.Key, serverObj);
                }
            }

            yield break;
        }

        if (root is JsonObject single)
        {
            var id = single["id"]?.GetValue<string>()
                     ?? single["name"]?.GetValue<string>()
                     ?? "mcp-server";
            yield return ParseServer(id, single);
        }
    }

    private static McpServerConfig ParseServer(string id, JsonObject obj)
    {
        var transport = ParseTransport(obj);
        var server = new McpServerConfig
        {
            Id = NormalizeId(id),
            Name = obj["name"]?.GetValue<string>() ?? id,
            Enabled = obj["enabled"]?.GetValue<bool>() ?? true,
            Transport = transport,
            Command = obj["command"]?.GetValue<string>(),
            Args = ReadStringArray(obj["args"]),
            Env = ReadStringMap(obj["env"]),
            WorkingDirectory = obj["workingDirectory"]?.GetValue<string>() ?? obj["cwd"]?.GetValue<string>(),
            Url = obj["url"]?.GetValue<string>() ?? obj["endpoint"]?.GetValue<string>(),
            Headers = ReadStringMap(obj["headers"])
        };

        if (server.Transport == McpTransportType.Stdio && string.IsNullOrWhiteSpace(server.Command))
        {
            throw new InvalidOperationException($"MCP 服务 {id} 缺少 command。");
        }

        if (server.Transport != McpTransportType.Stdio && string.IsNullOrWhiteSpace(server.Url))
        {
            throw new InvalidOperationException($"MCP 服务 {id} 缺少 url/endpoint。");
        }

        return server;
    }

    private static McpTransportType ParseTransport(JsonObject obj)
    {
        var raw = obj["transport"]?.GetValue<string>()
                  ?? obj["type"]?.GetValue<string>()
                  ?? (obj["url"] is not null || obj["endpoint"] is not null ? "http" : "stdio");
        return raw.Trim().ToLowerInvariant() switch
        {
            "sse" => McpTransportType.Sse,
            "http" or "streamable-http" or "streamable_http" => McpTransportType.Http,
            _ => McpTransportType.Stdio
        };
    }

    private static List<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
    }

    private static Dictionary<string, string> ReadStringMap(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is not JsonObject obj)
        {
            return result;
        }

        foreach (var kvp in obj)
        {
            try
            {
                if (kvp.Value is JsonValue jsonValue)
                {
                    result[kvp.Key] = jsonValue.GetValue<string>();
                }
                else if (kvp.Value is not null)
                {
                    result[kvp.Key] = kvp.Value.ToJsonString().Trim('"');
                }
            }
            catch
            {
                result[kvp.Key] = kvp.Value?.ToString() ?? string.Empty;
            }
        }

        return result;
    }

    private void TryMigrateLegacyConfig()
    {
        if (!File.Exists(_legacyConfigPath) || Directory.GetFiles(_serversDirectory, "*.json").Length > 0)
        {
            return;
        }

        try
        {
            ImportJson(File.ReadAllText(_legacyConfigPath));
        }
        catch
        {
        }
    }

    private McpServerConfig? LoadFile(string path)
    {
        _fileLock.Wait();
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var server = JsonSerializer.Deserialize<McpServerConfig>(json, McpConfigJson.Options);
            if (server is null || string.IsNullOrWhiteSpace(server.Id))
            {
                return null;
            }

            server.Id = NormalizeId(server.Id);
            server.Name = string.IsNullOrWhiteSpace(server.Name) ? server.Id : server.Name;
            server.Env ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            server.Headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            server.Args ??= [];

            // 历史遗留：env/headers 里的 Windows 路径可能被二次序列化成双反斜杠
            // （例如 MEMORY_FILE_PATH = C:\\Users\\ASUS\\...）。这里把盘符路径里
            // 连续的反斜杠折叠回单个，让损坏的旧配置在加载时自动恢复正常路径。
            // 只对 Windows 盘符绝对路径（如 C:\、D:\）生效，避免误伤 UNC 路径（\\server）。
            if (server.Env.Count > 0)
            {
                server.Env = server.Env.ToDictionary(
                    kvp => kvp.Key,
                    kvp => NormalizeWindowsPath(kvp.Value),
                    StringComparer.OrdinalIgnoreCase);
            }

            if (server.Headers.Count > 0)
            {
                server.Headers = server.Headers.ToDictionary(
                    kvp => kvp.Key,
                    kvp => NormalizeWindowsPath(kvp.Value),
                    StringComparer.OrdinalIgnoreCase);
            }

            return server;
        }
        catch
        {
            return null;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private string GetServerPath(string id) => Path.Combine(_serversDirectory, NormalizeId(id) + ".json");

    /// <summary>
    /// 修复历史二次序列化损坏的 Windows 路径：把盘符绝对路径（如 C:\...）里
    /// 连续的反斜杠折叠为单个。仅对以「盘符:\」开头的值生效，UNC 路径和其它值原样返回。
    /// </summary>
    private static string NormalizeWindowsPath(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        // 只处理 Windows 盘符绝对路径：形如 C:\ 或 D:\
        if (value.Length < 3 ||
            !char.IsLetter(value[0]) ||
            value[1] != ':' ||
            value[2] != '\\')
        {
            return value;
        }

        // 把 2 个及以上连续反斜杠折叠为单个
        return BackslashRunRegex.Replace(value, "\\");
    }

    private static readonly Regex BackslashRunRegex = new("\\\\{2,}", RegexOptions.Compiled);

    public static string NormalizeId(string value)
    {
        var normalized = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "mcp-server" : normalized;
    }
}
