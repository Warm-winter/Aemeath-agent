using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace Aemeath.Core.MCP;

public class McpChatPlugin
{
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

            // 使用 McpServerStore 统一 API 写入 mcp/servers/ 单文件，
            // 与 McpRuntimeService 的读取位置一致，配置才会被实际加载。
            var store = new McpServerStore();

            var savedServers = new List<string>();

            // filesystem：内置受保护服务，使用 bun 运行 @modelcontextprotocol/server-filesystem
            // 注意：内置「记忆」MCP（@modelcontextprotocol/server-memory）已移除，
            // 长期记忆改由 Mem0 提供（见 Aemeath.Core.Memory）。这里只保留 filesystem。
            var filesystemConfig = new McpServerConfig
            {
                Id = "filesystem",
                Name = "filesystem",
                Enabled = true,
                Transport = McpTransportType.Stdio,
                Command = bunPath,
                Args = BuildFilesystemArgs(roots).ToList()
            };
            store.SaveServer(filesystemConfig);
            savedServers.Add("filesystem");

            // 仅当本机确有 odr.exe 时才写入 windows_odr（DATA-004 / 防止写入会崩溃的配置）。
            var odrPath = ResolveOdrExecutablePath();
            if (!string.IsNullOrWhiteSpace(odrPath) && File.Exists(odrPath))
            {
                var odrConfig = new McpServerConfig
                {
                    Id = "windows-odr",
                    Name = "windows_odr",
                    Enabled = true,
                    Transport = McpTransportType.Stdio,
                    Command = odrPath,
                    Args = new List<string> { "list" }
                };
                store.SaveServer(odrConfig);
                savedServers.Add("windows_odr");
            }

            var uvNote = string.IsNullOrWhiteSpace(uvPath) ? "（未找到 uv.exe，仅启用 bun 方案）" : string.Empty;
            var odrNote = !savedServers.Contains("windows_odr") ? "（未找到 odr.exe，已跳过 windows_odr）" : string.Empty;
            return $"内置 MCP servers 配置完成：已写入 {store.ServersDirectory}（{string.Join("、", savedServers)}）{uvNote}{odrNote}";
        }
        catch (Exception ex)
        {
            return $"MCP 配置失败：{ex.Message}";
        }
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
}
