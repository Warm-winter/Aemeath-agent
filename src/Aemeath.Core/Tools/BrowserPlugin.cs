using Microsoft.SemanticKernel;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;

namespace Aemeath.Core.Tools;

public class BrowserPlugin
{
    private readonly ToolConfirmationService? _confirmationService;

    private static readonly Dictionary<string, string[]> CommonAppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["微信"] = ["WeChat.exe", "Weixin.exe", "WXWork.exe"],
        ["wechat"] = ["WeChat.exe", "Weixin.exe", "WXWork.exe"],
        ["weixin"] = ["WeChat.exe", "Weixin.exe"],
        ["酷狗"] = ["KuGou.exe", "KGMusic.exe"],
        ["kugou"] = ["KuGou.exe", "KGMusic.exe"],
        ["腾讯视频"] = ["QQLive.exe", "TencentVideo.exe", "QQLivePlayer.exe"],
        ["tencent video"] = ["QQLive.exe", "TencentVideo.exe", "QQLivePlayer.exe"],
        ["哔哩哔哩"] = ["bilibili.exe", "Bilibili.exe"],
        ["bilibili"] = ["bilibili.exe", "Bilibili.exe"],
        ["b站"] = ["bilibili.exe", "Bilibili.exe"],
        ["网易云音乐"] = ["cloudmusic.exe", "NeteaseCloudMusic.exe"],
        ["netease music"] = ["cloudmusic.exe", "NeteaseCloudMusic.exe"],
        ["qq音乐"] = ["QQMusic.exe"],
        ["qqmusic"] = ["QQMusic.exe"],
        ["qq"] = ["QQ.exe"],
        ["chrome"] = ["chrome.exe"],
        ["edge"] = ["msedge.exe"]
    };

    private static readonly Dictionary<string, string> CommonWebUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["腾讯视频"] = "https://v.qq.com",
        ["tencent video"] = "https://v.qq.com",
        ["哔哩哔哩"] = "https://www.bilibili.com",
        ["bilibili"] = "https://www.bilibili.com",
        ["b站"] = "https://www.bilibili.com",
        ["网易云音乐"] = "https://music.163.com",
        ["netease music"] = "https://music.163.com",
        ["qq音乐"] = "https://y.qq.com",
        ["qqmusic"] = "https://y.qq.com",
        ["微信"] = "https://weixin.qq.com",
        ["qq"] = "https://im.qq.com",
        ["edge"] = "https://www.microsoft.com/edge",
        ["chrome"] = "https://www.google.com/chrome"
    };

    public BrowserPlugin(ToolConfirmationService? confirmationService = null)
    {
        _confirmationService = confirmationService;
    }

    private static bool IsSafeHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    [KernelFunction("open_browser")]
    [Description("在默认浏览器中打开 URL")]
    public string OpenBrowser(
        [Description("要打开的网址")] string url)
    {
        try
        {
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                url = "https://" + url;
            }

            if (!IsSafeHttpUrl(url))
            {
                return "无效或不安全的 URL";
            }
            
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            
            return $"已在浏览器中打开：{url}";
        }
        catch (Exception ex)
        {
            return $"打开浏览器失败：{ex.Message}";
        }
    }

    [KernelFunction("open_default_browser")]
    [Description("打开系统默认浏览器主页")]
    public string OpenDefaultBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://www.bing.com",
                UseShellExecute = true
            });
            return "已打开默认浏览器";
        }
        catch (Exception ex)
        {
            return $"打开默认浏览器失败：{ex.Message}";
        }
    }

    [KernelFunction("search_web")]
    [Description("在搜索引擎中搜索关键词")]
    public string SearchWeb(
        [Description("搜索关键词")] string query,
        [Description("搜索引擎，默认 Google")] string engine = "Google")
    {
        try
        {
            var baseUrl = engine.ToLower() switch
            {
                "baidu" => "https://www.baidu.com/s?wd=",
                "bing" => "https://www.bing.com/search?q=",
                _ => "https://www.google.com/search?q="
            };
            
            var url = baseUrl + Uri.EscapeDataString(query);
            
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            
            return $"已在 {engine} 中搜索：{query}";
        }
        catch (Exception ex)
        {
            return $"搜索失败：{ex.Message}";
        }
    }

    [KernelFunction("open_application")]
    [Description("打开本机应用程序，可传应用名或可执行文件路径")]
    public string OpenApplication(
        [Description("应用名称或可执行文件路径，例如 微信/WeChat.exe/D:\\Apps\\WeChat.exe")] string appNameOrPath,
        [Description("可选启动参数")] string? arguments = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(appNameOrPath))
            {
                return "打开应用失败：应用名不能为空";
            }

            var target = ResolveAppExecutable(appNameOrPath.Trim());
            if (target is null)
            {
                return $"打开应用失败：未找到 {appNameOrPath}";
            }

            var psi = new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
                Arguments = arguments ?? string.Empty
            };

            Process.Start(psi);
            return $"已启动应用：{Path.GetFileName(target)}";
        }
        catch (Exception ex)
        {
            return $"打开应用失败：{ex.Message}";
        }
    }

    [KernelFunction("open_app_or_web")]
    [Description("优先打开本机应用；找不到应用时再打开对应网页或搜索页")]
    public string OpenAppOrWeb(
        [Description("应用、服务或网站名称，例如 腾讯视频、哔哩哔哩、网易云音乐")] string name,
        [Description("可选启动参数")] string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "打开失败：名称不能为空";
        }

        var normalizedName = name.Trim();
        var appPath = ResolveAppExecutable(normalizedName);
        if (appPath is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = appPath,
                    UseShellExecute = true,
                    Arguments = arguments ?? string.Empty
                });
                return $"已优先启动本机应用：{Path.GetFileName(appPath)}";
            }
            catch (Exception ex)
            {
                return $"找到本机应用但启动失败：{ex.Message}";
            }
        }

        if (CommonWebUrls.TryGetValue(normalizedName, out var url))
        {
            return OpenBrowser(url);
        }

        if (IsSafeHttpUrl(normalizedName))
        {
            return OpenBrowser(normalizedName);
        }

        return SearchWeb(normalizedName, "bing");
    }

    [KernelFunction("run_powershell")]
    [Description("执行 PowerShell 命令并返回输出。仅用于用户明确授权的本机操作")]
    public string RunPowerShell(
        [Description("PowerShell 命令文本，例如 Get-Process | Select-Object -First 3")] string command)
    {
        if (_confirmationService is not null && IsHighRiskPowerShellCommand(command))
        {
            return _confirmationService.RequestConfirmation(
                "执行高风险 PowerShell 命令",
                command,
                () => RunPowerShellCore(command));
        }

        return RunPowerShellCore(command);
    }

    private static string RunPowerShellCore(string command)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return "执行失败：命令不能为空";
            }

            var psi = new ProcessStartInfo
            {
                // 用 -EncodedCommand（UTF-16LE 的 Base64）传命令，彻底避免字符串拼接成
                // -Command 时的引号/反引号/子表达式/管道符注入（SEC-001）。
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {ToEncodedCommand(command)}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return "执行失败：无法启动 PowerShell";
            }

            // 先开始异步读取 stdout/stderr，再 WaitForExit，避免子进程输出缓冲区写满后
            // 阻塞、导致 WaitForExit 死锁（LOGIC-014）。
            var stdoutBuilder = new System.Text.StringBuilder();
            var stderrBuilder = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(15000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                }

                return "执行失败：命令超时（15秒）";
            }

            // 等待异步读取完成，确保缓冲区全部消费
            process.WaitForExit();
            var stdout = stdoutBuilder.ToString().Trim();
            var stderr = stderrBuilder.ToString().Trim();
            if (process.ExitCode == 0)
            {
                return string.IsNullOrWhiteSpace(stdout) ? "命令执行成功（无输出）" : stdout;
            }

            return string.IsNullOrWhiteSpace(stderr)
                ? $"命令执行失败，退出码：{process.ExitCode}"
                : $"命令执行失败：{stderr}";
        }
        catch (Exception ex)
        {
            return $"执行失败：{ex.Message}";
        }
    }

    private static bool IsHighRiskPowerShellCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        // 统一小写后做分词匹配，覆盖 PowerShell 内置别名（SEC-002）：
        // ri=Remove-Item, sc=Set-Content, ni=New-Item, mi=Move-Item, cp/Copy-Item,
        // del/erase/rd/rmdir 等。用带边界的匹配避免误伤普通单词。
        var normalized = " " + command.Trim().ToLowerInvariant() + " ";
        var riskyTokens = new[]
        {
            "remove-item", " ri ", " ri;", " rm ", " rm;", " del ", " erase ",
            " rmdir", " rd ", "clear-content", "clear-item",
            "set-content", " sc ", "out-null", "format-volume", "format ",
            "shutdown", "restart-computer", "stop-computer", "stop-process",
            "taskkill", "remove-aduser", "remove-localuser", "new-item -force",
            "invoke-expression", "iex ", "invoke-webrequest", "iwr ",
            "start-process", "cmd /c", "powershell -"
        };

        return riskyTokens.Any(token => normalized.Contains(token));
    }

    /// <summary>把命令文本编码为 PowerShell -EncodedCommand 需要的 UTF-16LE Base64。</summary>
    private static string ToEncodedCommand(string command)
    {
        var utf16 = System.Text.Encoding.Unicode.GetBytes(command);
        return Convert.ToBase64String(utf16);
    }

    private static string? ResolveAppExecutable(string appNameOrPath)
    {
        if (File.Exists(appNameOrPath))
        {
            if (appNameOrPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveShortcutTarget(appNameOrPath);
            }

            return Path.GetFullPath(appNameOrPath);
        }

        var fileName = appNameOrPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? appNameOrPath
            : appNameOrPath + ".exe";

        if (TryResolveFromWhere(fileName, out var byWhere))
        {
            return byWhere;
        }

        if (TryResolveFromRegistryAppPaths(fileName, out var registryPath))
        {
            return registryPath;
        }

        if (TryResolveFromDesktopShortcuts(appNameOrPath, out var shortcutTarget))
        {
            return shortcutTarget;
        }

        if (CommonAppNames.TryGetValue(appNameOrPath, out var mappedNames))
        {
            foreach (var mapped in mappedNames)
            {
                if (TryResolveFromWhere(mapped, out var mappedByWhere))
                {
                    return mappedByWhere;
                }

                if (TryResolveFromRegistryAppPaths(mapped, out var mappedByRegistry))
                {
                    return mappedByRegistry;
                }

                var known = TryResolveFromKnownLocations(mapped);
                if (known is not null)
                {
                    return known;
                }

                var searched = SearchCommonInstallPaths(mapped);
                if (searched is not null)
                {
                    return searched;
                }
            }
        }

        return TryResolveFromKnownLocations(fileName) ?? SearchCommonInstallPaths(fileName);
    }

    private static bool TryResolveFromDesktopShortcuts(string appName, out string fullPath)
    {
        fullPath = string.Empty;
        foreach (var desktop in GetDesktopDirectories())
        {
            try
            {
                var links = Directory.EnumerateFiles(desktop, "*.lnk", SearchOption.TopDirectoryOnly);
                var link = links.FirstOrDefault(x =>
                    Path.GetFileNameWithoutExtension(x)
                        .Contains(appName, StringComparison.OrdinalIgnoreCase));

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

    private static IEnumerable<string> GetDesktopDirectories()
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

    private static string? ResolveShortcutTarget(string shortcutPath)
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
            @"D:\Software",
            @"D:\Tencent"
        };

        var relativePatterns = new[]
        {
            exeName,
            Path.Combine("Tencent", "WeChat", exeName),
            Path.Combine("Tencent", "Weixin", exeName),
            Path.Combine("KuGou", exeName),
            Path.Combine("Kugou", exeName),
            Path.Combine("WeChat", exeName),
            Path.Combine("Weixin", exeName)
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var pattern in relativePatterns)
            {
                try
                {
                    var candidate = Path.Combine(root, pattern);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }
        }

        return null;
    }

    private static bool TryResolveFromRegistryAppPaths(string exeName, out string fullPath)
    {
        fullPath = string.Empty;
        var keys = new[]
        {
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}",
            $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\{exeName}"
        };

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var keyPath in keys)
            {
                try
                {
                    using var key = root.OpenSubKey(keyPath);
                    var candidate = key?.GetValue(null)?.ToString();
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    {
                        fullPath = candidate;
                        return true;
                    }
                }
                catch
                {
                }
            }
        }

        return false;
    }

    private static bool TryResolveFromWhere(string exeName, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = exeName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            var line = output
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(File.Exists);
            if (line is null)
            {
                return false;
            }

            fullPath = line.Trim();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? SearchCommonInstallPaths(string exeName)
    {
        var roots = new List<string>
        {
            Environment.GetEnvironmentVariable("ProgramFiles") ?? string.Empty,
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            @"D:\",
            @"D:\Program Files",
            @"D:\Program Files (x86)",
            @"D:\Apps"
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var match = Directory
                    .EnumerateFiles(root, exeName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (match is not null)
                {
                    return match;
                }
            }
            catch
            {
            }
        }

        return null;
    }
}
