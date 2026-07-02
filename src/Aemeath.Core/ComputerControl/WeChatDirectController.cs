using System.Text.RegularExpressions;

namespace Aemeath.Core.ComputerControl;

/// <summary>
/// 微信桌面版直控器（PowerShell+Win32 API 思路的 C# 实现）。
///
/// 背景：微信是自定义渲染 UI，UIA 树几乎只暴露 3 个顶层元素，拿不到聊天列表/输入框/按钮，
/// 导致通用 UIA agent 无法可靠操作。本类绕过 UIA，用 Win32 FindWindow 找到微信主窗口后，
/// 按窗口矩形的相对坐标精确定位「搜索框/输入框」等关键区域，再用 SetCursorPos+mouse_event+keybd_event 操作。
///
/// 适配窗口类：WeChatMainWndForPC（当前主流桌面版）。坐标比例基于实测，微信改版可能需微调。
///
/// 这是「应用直控」范式，可复用于其它自定义渲染 UI（电报、QQ 等）：子类化并提供相对坐标常量即可。
/// </summary>
public sealed class WeChatDirectController
{
    private readonly Action<string> _log;

    public WeChatDirectController(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// 尝试启动微信桌面版。微信 4.x（代号 Androws）的可执行文件结构和老版完全不同：
    /// 没有 WeChat.exe/Weixin.exe，启动器是 Androws\Application\AndrowsLauncher.exe，
    /// 运行期进程是 WeChatAppEx.exe（WMPF runtime）。这里覆盖新旧两套。
    /// 登录仍需用户手动完成。
    /// </summary>
    public static bool TryStartWeChat()
    {
        try
        {
            var exe = ResolveWeChatExecutable();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                return false;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析微信可执行文件路径（兼容微信 4.x/5.x「Androws」与老版 WeChat.exe）。</summary>
    private static string? ResolveWeChatExecutable()
    {
        // 1. where 命令查老版
        foreach (var name in new[] { "WeChat.exe", "Weixin.exe", "AndrowsLauncher.exe" })
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "where",
                Arguments = name,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is not null)
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                var line = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(File.Exists);
                if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
            }
        }

        // 2. 桌面快捷方式（用户通常从桌面启动微信）
        foreach (var desktop in AppLauncher.GetDesktopDirectories())
        {
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(desktop, "*.lnk", SearchOption.TopDirectoryOnly))
                {
                    var fn = Path.GetFileNameWithoutExtension(lnk);
                    if (!fn.Contains("微信", StringComparison.Ordinal)
                        && !fn.Contains("WeChat", StringComparison.OrdinalIgnoreCase)
                        && !fn.Contains("Weixin", StringComparison.OrdinalIgnoreCase)) continue;
                    // 排除卸载快捷方式
                    if (fn.Contains("卸载", StringComparison.Ordinal)
                        || fn.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                    var target = AppLauncher.ResolveShortcutTarget(lnk);
                    if (!string.IsNullOrWhiteSpace(target) && File.Exists(target)) return target;
                }
            }
            catch { }
        }

        // 3. 开始菜单快捷方式
        foreach (var progDir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        })
        {
            if (string.IsNullOrEmpty(progDir) || !Directory.Exists(progDir)) continue;
            foreach (var lnk in Directory.EnumerateFiles(progDir, "*.lnk", SearchOption.AllDirectories))
            {
                var fn = Path.GetFileNameWithoutExtension(lnk);
                if (!fn.Contains("微信", StringComparison.Ordinal)
                    && !fn.Contains("WeChat", StringComparison.OrdinalIgnoreCase)
                    && !fn.Contains("Weixin", StringComparison.OrdinalIgnoreCase)) continue;
                // 排除卸载快捷方式，避免误启动卸载器
                if (fn.Contains("卸载", StringComparison.Ordinal)
                    || fn.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                var target = ResolveShortcutTarget(lnk);
                if (!string.IsNullOrWhiteSpace(target) && File.Exists(target)) return target;
            }
        }

        // 3. 微信 4.x/5.x（Androws）固定路径
        var androwsApp = @"C:\Program Files\Tencent\Androws\Application";
        if (Directory.Exists(androwsApp))
        {
            // Application\AndrowsLauncher.exe 直接命中
            var launcher = Path.Combine(androwsApp, "AndrowsLauncher.exe");
            if (File.Exists(launcher)) return launcher;
            // 或 Application\<version>\AndrowsLauncher.exe
            foreach (var d in Directory.EnumerateDirectories(androwsApp))
            {
                var v = Path.Combine(d, "AndrowsLauncher.exe");
                if (File.Exists(v)) return v;
            }
        }

        // 4. 老版常见路径
        var candidates = new[]
        {
            @"C:\Program Files\Tencent\WeChat\WeChat.exe",
            @"C:\Program Files (x86)\Tencent\WeChat\WeChat.exe",
            @"C:\Program Files\Tencent\Weixin\Weixin.exe",
            @"C:\Program Files (x86)\Tencent\Weixin\Weixin.exe",
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveShortcutTarget(string shortcutPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            var target = shortcut.TargetPath as string;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }


    /// <summary>
    /// 尝试执行一个微信相关任务。当前支持模式：
    /// - 给指定对象发消息：「给/向 {name} 发 {msg}」「send {msg} to {name}」
    /// 返回 true 表示已处理（无论成功失败都给出反馈），false 表示任务不是微信相关、交给通用 agent。
    /// </summary>
    public async Task<(bool Handled, string Result)> TryHandleAsync(string request, CancellationToken cancellationToken = default)
    {
        var match = MatchSendTask(request);
        if (match is null)
        {
            return (false, string.Empty);
        }

        var (contactName, message) = match.Value;
        return (true, await SendMessageAsync(contactName, message, cancellationToken));
    }

    /// <summary>识别「给 X 发 Y」任务。返回 (联系人, 消息)，不匹配返回 null。</summary>
    private static (string Contact, string Message)? MatchSendTask(string request)
    {
        // 中文：给/向/跟 {联系人} 发/发送 {消息}：/ ：消息
        var cn = Regex.Match(request, @"(给|向|跟|对)\s*(?<c>[^\s,，。：:]+?)\s*(?:发(送)?|说|回复)\s*[个条]?\s*(?:消息|信息|内容)?\s*[:：]?\s*(?<m>.+)$");
        if (cn.Success)
        {
            var msg = cn.Groups["m"].Value.Trim().Trim('"', '“', '”', ' ', '　');
            return (cn.Groups["c"].Value.Trim(), msg);
        }

        // 英文：send "msg" to contact / message contact: msg
        var en = Regex.Match(request, @"send\s+(?<m>.+?)\s+to\s+(?<c>.+)$", RegexOptions.IgnoreCase);
        if (en.Success)
        {
            return (en.Groups["c"].Value.Trim(), en.Groups["m"].Value.Trim().Trim('"'));
        }

        return null;
    }

    /// <summary>给指定联系人发消息。</summary>
    public async Task<string> SendMessageAsync(string contactName, string message, CancellationToken cancellationToken = default)
    {
        // 1. 找微信窗口（若未打开则提示）
        var hwnd = Win32Interop.FindWeChatWindow();
        if (hwnd == IntPtr.Zero)
        {
            return "未找到微信窗口。请先打开并登录微信桌面版，再重试。";
        }

        _log($"找到微信窗口 hwnd=0x{hwnd.ToInt64():X}");
        Win32Interop.FocusWindow(hwnd);
        var rect = Win32Interop.GetRect(hwnd);
        _log($"微信窗口矩形：({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})，尺寸 {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");

        // 2. 用 Ctrl+F 打开搜索（微信全局快捷键，比点搜索框坐标更可靠）。
        InputExecutor.KeyboardInput("{VK_CONTROL}f");
        await Task.Delay(450, cancellationToken);

        // 3. 输入联系人名称，等待搜索结果
        InputExecutor.TypeText(contactName);
        await Task.Delay(800, cancellationToken);

        // 4. 回车选中第一个搜索结果，进入与该联系人的会话
        Win32Interop.PressEnter();
        await Task.Delay(700, cancellationToken);

        // 5. 进入会话后输入框默认聚焦，直接输入消息（不依赖输入框坐标）
        InputExecutor.TypeText(message);
        await Task.Delay(350, cancellationToken);

        // 6. 回车发送
        Win32Interop.PressEnter();
        await Task.Delay(350, cancellationToken);

        return $"已尝试通过微信给「{contactName}」发送消息：「{message}」。请在微信窗口确认是否发送成功。";
    }
}
