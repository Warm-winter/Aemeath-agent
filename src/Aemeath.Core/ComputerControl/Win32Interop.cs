using System.Runtime.InteropServices;

namespace Aemeath.Core.ComputerControl;

/// <summary>
/// Win32 互操作：DPI 感知、窗口枚举/最小化、微信窗口查找。
/// 集中托管所有底层 P/Invoke，供 ComputerControlAgent / 微信直控 / 任务前最小化窗口复用。
/// </summary>
internal static class Win32Interop
{
    // ===== DPI 感知 =====
    // Per-Monitor DPI v2：让进程以物理像素为基准，截图/UIA BoundingRectangle/SetCursorPos 全部统一到物理像素坐标系。
    // 不设置时，进程可能被系统 DPI 虚拟化，导致「截图坐标=物理像素/2」错位、点击落空。
    private const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(int value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private static int _dpiAwareSet; // 0=未设, 1=已设

    /// <summary>确保进程是 Per-Monitor DPI v2 感知（幂等）。电脑控制前调用。</summary>
    public static void EnsurePerMonitorDpiAware()
    {
        if (Interlocked.CompareExchange(ref _dpiAwareSet, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
            {
                SetProcessDPIAware(); // 回退到系统级 DPI 感知
            }
        }
        catch
        {
            // 旧系统可能没有该 API，忽略
        }
    }

    // ===== 窗口枚举 / 最小化 =====
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const uint GW_OWNER = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    /// <summary>记录被最小化的窗口句柄，供任务结束后恢复。</summary>
    public sealed class MinimizeScope : IDisposable
    {
        private readonly List<IntPtr> _minimized = new();
        private bool _disposed;

        internal void Add(IntPtr hwnd) => _minimized.Add(hwnd);

        public void Restore()
        {
            foreach (var hwnd in _minimized)
            {
                try { ShowWindowAsync(hwnd, SW_RESTORE); } catch { /* 忽略 */ }
            }
            _minimized.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Restore();
        }
    }

    /// <summary>
    /// 最小化所有可见的顶层窗口（排除桌面/Shell、任务栏等），返回一个 scope，
    /// 调用方 Dispose 时恢复原状。用于电脑控制任务前清理桌面遮挡。
    /// excludeHwnd：目标窗口句柄，不会被最小化（保护刚打开的目标应用窗口）。
    /// </summary>
    public static MinimizeScope MinimizeAllWindows(IntPtr? excludeHwnd = null)
    {
        var scope = new MinimizeScope();
        var shell = GetShellWindow();
        var handles = new List<IntPtr>();

        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (hwnd == shell) return true;
                // 排除目标窗口（不最小化目标应用）
                if (excludeHwnd.HasValue && hwnd == excludeHwnd.Value) return true;
                // 跳过有 owner 的窗口（如对话框/子窗口），避免误伤
                if (GetWindowOwner(hwnd) != IntPtr.Zero) return true;
                // 跳过无标题的工具窗口（任务栏等）
                var title = new System.Text.StringBuilder(256);
                GetWindowText(hwnd, title, title.Capacity);
                var cls = new System.Text.StringBuilder(256);
                GetClassName(hwnd, cls, cls.Capacity);
                var className = cls.ToString();
                if (className == "Shell_TrayWnd" /* 任务栏 */
                    || className == "Shell_SecondaryTrayWnd" /* 副屏任务栏 */)
                {
                    return true;
                }

                handles.Add(hwnd);
            }
            catch { /* 忽略单个窗口 */ }
            return true;
        }, IntPtr.Zero);

        foreach (var hwnd in handles)
        {
            try
            {
                if (ShowWindowAsync(hwnd, SW_MINIMIZE))
                {
                    scope.Add(hwnd);
                }
            }
            catch { /* 忽略 */ }
        }

        return scope;
    }

    private static IntPtr GetWindowOwner(IntPtr hwnd)
    {
        try { return GetWindow(hwnd, GW_OWNER); }
        catch { return IntPtr.Zero; }
    }

    // ===== 微信窗口查找（WeChatMainWndForPC） =====
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_RETURN = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>微信桌面版的窗口类名（自定义渲染 UI，UIA 几乎拿不到子控件）。</summary>
    public const string WeChatMainClass = "WeChatMainWndForPC";

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>
    /// 查找微信主窗口句柄。不依赖固定类名（微信 4.x 类名已变），
    /// 改为枚举所有可见顶层窗口，按标题（微信/WeChat）或进程名（WeChat.exe/Weixin.exe）匹配。
    /// 返回 IntPtr.Zero 表示未找到。
    /// </summary>
    public static IntPtr FindWeChatWindow()
    {
        try
        {
            // 优先：已知类名（最稳，命中即返回）
            var byClass = FindWindow(WeChatMainClass, null);
            if (byClass != IntPtr.Zero && IsWindowVisible(byClass)) return byClass;

            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd)) return true;

                    GetWindowThreadProcessId(hwnd, out var pid);
                    var procName = GetProcessNameSafe(pid);

                    var title = new System.Text.StringBuilder(256);
                    GetWindowText(hwnd, title, title.Capacity);
                    var titleStr = title.ToString();

                    var cls = new System.Text.StringBuilder(256);
                    GetClassName(hwnd, cls, cls.Capacity);
                    var clsStr = cls.ToString();

                    // 匹配条件：微信进程名（微信 4.x 的进程是 WeChatAppEx，不是 WeChat.exe！）
                    // 或标题含微信/WeChat，或类名含 WeChat/Weixin/Androws
                    bool isWeChatProc = !string.IsNullOrEmpty(procName)
                        && (procName.StartsWith("WeChat", StringComparison.OrdinalIgnoreCase)
                            || procName.StartsWith("Weixin", StringComparison.OrdinalIgnoreCase)
                            || procName.Equals("WeChatAppEx", StringComparison.OrdinalIgnoreCase)
                            || procName.StartsWith("Androws", StringComparison.OrdinalIgnoreCase));
                    bool isWeChatTitle = titleStr.Contains("微信", StringComparison.Ordinal)
                                         || titleStr.Contains("WeChat", StringComparison.OrdinalIgnoreCase);
                    bool isWeChatClass = clsStr.IndexOf("WeChat", StringComparison.OrdinalIgnoreCase) >= 0
                                         || clsStr.IndexOf("Weixin", StringComparison.OrdinalIgnoreCase) >= 0
                                         || clsStr.IndexOf("Androws", StringComparison.OrdinalIgnoreCase) >= 0;

                    // 微信相关窗口不跳过 owner 检查（WeChat 4.x 主窗口可能有 owner）；
                    // 非微信窗口仍跳过有 owner 的（对话框/子窗口）。
                    if (!isWeChatProc && !isWeChatTitle && !isWeChatClass)
                    {
                        if (GetWindowOwner(hwnd) != IntPtr.Zero) return true; // 跳过子窗口/对话框
                    }

                    // 进程名匹配即可（微信 4.x 进程是 WeChatAppEx，窗口可能无标题）；
                    // 或类名/标题明显是微信。优先返回可见的主窗口。
                    if (isWeChatProc || isWeChatTitle || isWeChatClass)
                    {
                        found = hwnd;
                        return false; // 找到就停（取第一个）
                    }
                }
                catch { /* 忽略单个窗口 */ }
                return true;
            }, IntPtr.Zero);

            return found;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>安全获取进程名（不抛异常，找不到返回空）。</summary>
    private static string GetProcessNameSafe(uint pid)
    {
        try
        {
            if (pid == 0) return string.Empty;
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            return proc.ProcessName ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>获取窗口的物理像素矩形。</summary>
    public static RECT GetRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var rect);
        return rect;
    }

    /// <summary>把窗口带到前台（若最小化则先恢复）。</summary>
    public static void FocusWindow(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
        Thread.Sleep(150); // 等待窗口真正前台化
    }

    /// <summary>在物理像素坐标点击（左键单击）。</summary>
    public static void ClickPhysical(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(40);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
        Thread.Sleep(20);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
    }

    /// <summary>按回车键（微信发消息常用）。</summary>
    public static void PressEnter()
    {
        keybd_event(VK_RETURN, 0, 0, 0);
        Thread.Sleep(20);
        keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, 0);
    }
}
