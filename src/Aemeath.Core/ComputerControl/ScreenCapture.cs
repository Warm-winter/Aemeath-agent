using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Aemeath.Core.ComputerControl;

/// <summary>
/// 屏幕截图（前台窗口或全屏）。比走 PowerShell 快得多，避免每步都起子进程。
/// </summary>
public static class ScreenCapture
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    /// <summary>截取前台窗口的客户区。返回保存路径。</summary>
    public static string CaptureForeground(string outPath)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || IsIconic(hwnd))
        {
            return CaptureFullScreen(outPath);
        }

        GetWindowRect(hwnd, out var rect);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return CaptureFullScreen(outPath);
        }

        try
        {
            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            bmp.Save(outPath, ImageFormat.Png);
            return outPath;
        }
        catch
        {
            return CaptureFullScreen(outPath);
        }
    }

    public static string CaptureFullScreen(string outPath)
    {
        var width = GetSystemMetrics(SM_CXSCREEN);
        var height = GetSystemMetrics(SM_CYSCREEN);
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        bmp.Save(outPath, ImageFormat.Png);
        return outPath;
    }
}
