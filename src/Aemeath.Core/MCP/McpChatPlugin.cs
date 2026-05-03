using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text.Json;

namespace Aemeath.Core.MCP;

public class McpChatPlugin
{
    private readonly string _appDataPath;
    private readonly string _memoryPath;

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

            var config = new
            {
                mcpServers = new Dictionary<string, object?>
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
                    },
                    ["windows_odr"] = new
                    {
                        command = ResolveOdrExecutablePath(),
                        args = new[] { "list" }
                    }
                }
            };

            File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            var uvNote = string.IsNullOrWhiteSpace(uvPath) ? "（未找到 uv.exe，仅启用 bun 方案）" : string.Empty;
            return $"内置 MCP servers 配置完成：{configPath}{uvNote}";
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

        var db = LoadMemory();
        db[key.Trim()] = value;
        SaveMemory(db);
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

        var db = LoadMemory();
        var removed = db.Remove(key.Trim());
        SaveMemory(db);
        return removed ? $"记忆已删除：{key}" : $"未找到记忆：{key}";
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
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveMemory(Dictionary<string, string> db)
    {
        File.WriteAllText(_memoryPath, JsonSerializer.Serialize(db, new JsonSerializerOptions { WriteIndented = true }));
    }
}
