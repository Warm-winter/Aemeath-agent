using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aemeath.Core.MCP;

public class McpChatPlugin
{
    private readonly string _appDataPath;
    private readonly string _memoryPath;
    private readonly object _memoryLock = new();

    public McpChatPlugin()
    {
        _appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Aemeath");
        Directory.CreateDirectory(_appDataPath);
        _memoryPath = Path.Combine(_appDataPath, "mcp_memory.json");
    }

    [KernelFunction("setup_builtin_mcp_servers")]
    [Description("配置内置 MCP servers，优先使用应用目录下的 uv.exe 和 bun.exe")]
    public string SetupBuiltinMcpServers(
        [Description("uv.exe 绝对路径，可空") ] string? uvExePath = null,
        [Description("bun.exe 绝对路径，可空") ] string? bunExePath = null,
        [Description("filesystem 允许访问目录（多个目录用;分隔）") ] string? filesystemRoots = null)
    {
        try
        {
            var uvPath = ResolveExecutablePath(uvExePath, "uv.exe");
            var bunPath = ResolveExecutablePath(bunExePath, "bun.exe");
            if (string.IsNullOrWhiteSpace(bunPath))
            {
                return "MCP 配置失败：未找到 bun.exe";
            }

            var roots = string.IsNullOrWhiteSpace(filesystemRoots)
                ? new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }
                : filesystemRoots.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var configPath = Path.Combine(_appDataPath, "mcp_servers.json");
            var memoryFilePath = Path.Combine(_appDataPath, "mcp_memory_store.jsonl");

            var builtin = new Dictionary<string, object?>
            {
                ["memory"] = new
                {
                    command = bunPath,
                    args = new[] { "x", "@modelcontextprotocol/server-memory" },
                    env = new Dictionary<string, string>
                    {
                        ["MEMORY_FILE_PATH"] = memoryFilePath
                    }
                },
                ["filesystem"] = new
                {
                    command = bunPath,
                    args = BuildFilesystemArgs(roots)
                }
            };

            // 仅当本机确有 odr.exe 时才写入 windows_odr（DATA-004 / 防止写入会崩溃的配置）。
            var odrPath = ResolveOdrExecutablePath();
            if (!string.IsNullOrWhiteSpace(odrPath) && File.Exists(odrPath))
            {
                builtin["windows_odr"] = new
                {
                    command = odrPath,
                    args = new[] { "list" }
                };
            }

            // 合并而非覆盖：保留用户已有的非内置服务（DATA-004）。
            var existing = LoadExistingLegacyServers(configPath);
            foreach (var kvp in existing)
            {
                if (!builtin.ContainsKey(kvp.Key))
                {
                    builtin[kvp.Key] = kvp.Value;
                }
            }

            var config = new { mcpServers = builtin };
            File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

            var odrNote = !builtin.ContainsKey("windows_odr") ? "（未找到 odr.exe，已跳过 windows_odr）" : string.Empty;
            var uvNote = string.IsNullOrWhiteSpace(uvPath) ? "（未找到 uv.exe，仅启用 bun 方案）" : string.Empty;
            return $"内置 MCP servers 配置完成：{configPath}{uvNote}{odrNote}";
        }
        catch (Exception ex)
        {
            return $"MCP 配置失败：{ex.Message}";
        }
    }

    [KernelFunction("mcp_memory_set")]
    [Description("写入全局持久记忆键值")]
    public string MemorySet(
        [Description("记忆键") ] string key,
        [Description("记忆值") ] string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "记忆写入失败：key 不能为空";
        }

        // 读-改-写整体加锁，避免并发写入丢失（CON-005）。
        lock (_memoryLock)
        {
            var db = LoadMemory();
            db[key.Trim()] = value;
            SaveMemory(db);
        }
        return $"记忆已写入：{key}";
    }

    [KernelFunction("mcp_memory_get")]
    [Description("读取全局持久记忆键值")]
    public string MemoryGet([Description("记忆键") ] string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "记忆读取失败：key 不能为空";
        }

        var db = LoadMemory();
        return db.TryGetValue(key.Trim(), out var value)
            ? value
            : $"未找到记忆：{key}";
    }

    [KernelFunction("mcp_memory_delete")]
    [Description("删除全局持久记忆键值")]
    public string MemoryDelete([Description("记忆键") ] string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "记忆删除失败：key 不能为空";
        }

        lock (_memoryLock)
        {
            var db = LoadMemory();
            var removed = db.Remove(key.Trim());
            SaveMemory(db);
            return removed ? $"记忆已删除：{key}" : $"未找到记忆：{key}";
        }
    }

    [KernelFunction("mcp_memory_list")]
    [Description("列出所有持久记忆键")]
    public string MemoryList()
    {
        var db = LoadMemory();
        if (db.Count == 0)
        {
            return "当前没有持久记忆";
        }

        return "持久记忆键：\n" + string.Join("\n", db.Keys.OrderBy(x => x));
    }

    private static string[] BuildFilesystemArgs(IEnumerable<string> roots)
    {
        var args = new List<string> { "x", "@modelcontextprotocol/server-filesystem" };
        args.AddRange(roots);
        return args.ToArray();
    }

    /// <summary>读取已存在的 legacy mcp_servers.json 里的服务定义，供合并保留（DATA-004）。</summary>
    private static Dictionary<string, object?> LoadExistingLegacyServers(string configPath)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(configPath))
        {
            return result;
        }

        try
        {
            var json = JsonNode.Parse(File.ReadAllText(configPath));
            if (json is JsonObject obj && obj["mcpServers"] is JsonObject servers)
            {
                foreach (var kvp in servers)
                {
                    if (kvp.Key is not null && kvp.Value is not null)
                    {
                        result[kvp.Key] = kvp.Value.Deserialize<object?>();
                    }
                }
            }
        }
        catch
        {
            // 读不到就当空，不阻塞内置配置
        }

        return result;
    }

    private static string ResolveOdrExecutablePath()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var odr = Path.Combine(system32, "odr.exe");
        return File.Exists(odr) ? odr : "odr.exe";
    }

    private static string? ResolveExecutablePath(string? preferredPath, string exeName)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
        {
            return Path.GetFullPath(preferredPath);
        }

        var candidates = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(McpDependencyService.DefaultBinDirectory, exeName));
        candidates.Add(Path.Combine(baseDir, exeName));
        candidates.Add(Path.Combine(baseDir, "bin", exeName));
        candidates.Add(Path.Combine(baseDir, "..", "..", "..", "bin", exeName));
        candidates.Add(Path.Combine(baseDir, "..", "..", "..", "..", "bin", exeName));
        candidates.Add(Path.Combine(baseDir, "..", "..", "..", "..", "..", "bin", exeName));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "bin", exeName));

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    private Dictionary<string, string> LoadMemory()
    {
        if (!File.Exists(_memoryPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(_memoryPath);
            // 反序列化得到的默认是 Ordinal 比较；重建为 OrdinalIgnoreCase，与新建时一致（DATA-003），
            // 否则重启后用不同大小写读不到已存记忆。
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                      ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in raw)
            {
                result[kvp.Key] = kvp.Value;
            }
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MCP 记忆读取失败：{ex.Message}");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveMemory(Dictionary<string, string> db)
    {
        // 原子写，避免崩溃导致 JSON 截断（DATA-005）。
        var json = JsonSerializer.Serialize(db, new JsonSerializerOptions { WriteIndented = true });
        var tempPath = _memoryPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _memoryPath, overwrite: true);
    }
}
