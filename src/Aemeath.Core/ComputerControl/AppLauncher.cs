using System.Diagnostics;

namespace Aemeath.Core.ComputerControl;

/// <summary>
/// 通用应用启动器：通过桌面快捷方式、开始菜单、where 命令、常见安装路径
/// 搜索应用的可执行文件路径并启动。
///
/// 从 BrowserPlugin 提取为共享类，供 ComputerControlAgent（launch_application 动作）、
/// WeChatDirectController、BrowserPlugin 共用，消除代码重复。
/// </summary>
public static class AppLauncher
{
    /// <summary>
    /// 通过应用名搜索桌面快捷方式和开始菜单，返回可执行文件路径。
    /// 搜索顺序：桌面 .lnk → 开始菜单 .lnk → where 命令 → 常见安装路径。
    /// 排除卸载快捷方式。
    /// </summary>
    public static string? ResolveAppExecutable(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return null;
        }

        // 1. 搜索桌面快捷方式
        if (TryResolveFromDesktopShortcuts(appName, out var desktopPath))
        {
            return desktopPath;
        }

        // 2. 搜索开始菜单快捷方式
        if (TryResolveFromStartMenuShortcuts(appName, out var startMenuPath))
        {
            return startMenuPath;
        }

        // 3. where 命令
        var whereResult = TryResolveFromWhere(appName, out var wherePath);
        if (whereResult && wherePath is not null)
        {
            return wherePath;
        }

        // 4. 常见安装路径
        return TryResolveFromKnownLocations(appName + ".exe");
    }

    /// <summary>获取桌面目录列表（用户桌面 + 公共桌面 + OneDrive 桌面）。</summary>
    public static IEnumerable<string> GetDesktopDirectories()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrWhiteSpace(oneDrive))
        {
            set.Add(Path.Combine(oneDrive, "Desktop"));
            set.Add(Path.Combine(oneDrive, "桌面"));
        }

        return set.Where(Directory.Exists);
    }

    /// <summary>获取开始菜单目录列表（用户开始菜单 + 公共开始菜单）。</summary>
    public static IEnumerable<string> GetStartMenuDirectories()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        };

        return set.Where(Directory.Exists);
    }

    /// <summary>解析 .lnk 快捷方式的目标路径，使用 WScript.Shell COM 对象。</summary>
    public static string? ResolveShortcutTarget(string shortcutPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            var target = shortcut.TargetPath as string;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>搜索桌面 .lnk 快捷方式，按应用名模糊匹配。</summary>
    private static bool TryResolveFromDesktopShortcuts(string appName, out string fullPath)
    {
        fullPath = string.Empty;
        foreach (var desktop in GetDesktopDirectories())
        {
            try
            {
                var links = Directory.EnumerateFiles(desktop, "*.lnk", SearchOption.TopDirectoryOnly);
                var link = links.FirstOrDefault(x =>
                {
                    var fn = Path.GetFileNameWithoutExtension(x);
                    return fn.Contains(appName, StringComparison.OrdinalIgnoreCase)
                           && !fn.Contains("卸载", StringComparison.Ordinal)
                           && !fn.Contains("uninstall", StringComparison.OrdinalIgnoreCase);
                });

                if (link is null)
                {
                    continue;
                }

                var target = ResolveShortcutTarget(link);
                if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
                {
                    fullPath = target;
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    /// <summary>搜索开始菜单 .lnk 快捷方式，按应用名模糊匹配。</summary>
    private static bool TryResolveFromStartMenuShortcuts(string appName, out string fullPath)
    {
        fullPath = string.Empty;
        foreach (var startMenu in GetStartMenuDirectories())
        {
            try
            {
                var links = Directory.EnumerateFiles(startMenu, "*.lnk", SearchOption.AllDirectories);
                var link = links.FirstOrDefault(x =>
                {
                    var fn = Path.GetFileNameWithoutExtension(x);
                    return fn.Contains(appName, StringComparison.OrdinalIgnoreCase)
                           && !fn.Contains("卸载", StringComparison.Ordinal)
                           && !fn.Contains("uninstall", StringComparison.OrdinalIgnoreCase);
                });

                if (link is null)
                {
                    continue;
                }

                var target = ResolveShortcutTarget(link);
                if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
                {
                    fullPath = target;
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    /// <summary>通过 where 命令搜索可执行文件。</summary>
    private static bool TryResolveFromWhere(string appName, out string? fullPath)
    {
        fullPath = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = appName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            if (proc.ExitCode != 0)
            {
                return false;
            }

            var firstLine = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                                  .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (firstLine is not null && File.Exists(firstLine))
            {
                fullPath = firstLine.Trim();
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    /// <summary>在常见安装路径下搜索可执行文件。</summary>
    private static string? TryResolveFromKnownLocations(string exeName)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles") ?? string.Empty,
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty,
            localAppData,
            Path.Combine(localAppData, "Programs"),
            roaming,
            @"D:\Program Files",
            @"D:\Program Files (x86)",
            @"D:\Apps",
            @"D:\Software"
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var candidate = Path.Combine(root, exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                // 搜索一级子目录
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    candidate = Path.Combine(dir, exeName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
            }
        }

        return null;
    }
}
