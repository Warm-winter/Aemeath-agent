using Avalonia;
using Avalonia.Controls;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Aemeath.Desktop.Services;

internal static class WindowsWindowFrame
{
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcHitTest = 0x0084;
    private const int DwmwaBorderColor = 34;
    private const uint DwmwaColorNone = 0xFFFFFFFE;
    private const int SmCxSizeFrame = 32;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private static readonly Dictionary<Window, Win32Properties.CustomWndProcHookCallback> Registrations = new();

    public static void Attach(Window window)
    {
        if (!OperatingSystem.IsWindows() ||
            Registrations.ContainsKey(window) ||
            !TryGetWindowHandle(window, out _))
        {
            return;
        }

        Win32Properties.CustomWndProcHookCallback callback =
            (IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                HandleWindowMessage(window, windowHandle, message, wParam, lParam, ref handled);
        Registrations.Add(window, callback);
        Win32Properties.AddWndProcHookCallback(window, callback);
        Refresh(window);
    }

    public static void Detach(Window window)
    {
        if (!Registrations.Remove(window, out var callback))
        {
            return;
        }

        Win32Properties.RemoveWndProcHookCallback(window, callback);
    }

    public static bool Refresh(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!TryGetWindowHandle(window, out var windowHandle))
        {
            return false;
        }
        SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return true;
        }

        var borderColor = DwmwaColorNone;
        return DwmSetWindowAttribute(
            windowHandle,
            DwmwaBorderColor,
            ref borderColor,
            sizeof(uint)) >= 0;
    }

    internal static Win32Properties.Win32HitTestValue GetResizeHitTest(
        PixelRect windowBounds,
        PixelPoint pointer,
        int horizontalGrip,
        int verticalGrip,
        bool canResize,
        bool isMaximized)
    {
        if (!canResize || isMaximized)
        {
            return Win32Properties.Win32HitTestValue.Client;
        }

        var left = pointer.X >= windowBounds.X && pointer.X < windowBounds.X + horizontalGrip;
        var right = pointer.X < windowBounds.Right && pointer.X >= windowBounds.Right - horizontalGrip;
        var top = pointer.Y >= windowBounds.Y && pointer.Y < windowBounds.Y + verticalGrip;
        var bottom = pointer.Y < windowBounds.Bottom && pointer.Y >= windowBounds.Bottom - verticalGrip;

        if (top && left) return Win32Properties.Win32HitTestValue.TopLeft;
        if (top && right) return Win32Properties.Win32HitTestValue.TopRight;
        if (bottom && left) return Win32Properties.Win32HitTestValue.BottomLeft;
        if (bottom && right) return Win32Properties.Win32HitTestValue.BottomRight;
        if (left) return Win32Properties.Win32HitTestValue.Left;
        if (right) return Win32Properties.Win32HitTestValue.Right;
        if (top) return Win32Properties.Win32HitTestValue.Top;
        if (bottom) return Win32Properties.Win32HitTestValue.Bottom;
        return Win32Properties.Win32HitTestValue.Client;
    }

    private static bool TryGetWindowHandle(Window window, out IntPtr windowHandle)
    {
        var platformHandle = window.TryGetPlatformHandle();
        if (platformHandle is not null &&
            platformHandle.Handle != IntPtr.Zero &&
            string.Equals(platformHandle.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase))
        {
            windowHandle = platformHandle.Handle;
            return true;
        }

        windowHandle = IntPtr.Zero;
        return false;
    }

    private static IntPtr HandleWindowMessage(
        Window window,
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmNcCalcSize && wParam != IntPtr.Zero)
        {
            if (IsZoomed(windowHandle))
            {
                FitMaximizedClientAreaToWorkArea(windowHandle, lParam);
            }

            handled = true;
            return IntPtr.Zero;
        }

        if (message != WmNcHitTest || !GetWindowRect(windowHandle, out var bounds))
        {
            return IntPtr.Zero;
        }

        var packedPoint = lParam.ToInt64();
        var pointer = new PixelPoint(
            unchecked((short)(packedPoint & 0xFFFF)),
            unchecked((short)((packedPoint >> 16) & 0xFFFF)));
        var dpi = Math.Max(GetDpiForWindow(windowHandle), 96u);
        var horizontalGrip = GetSystemMetricsForDpi(SmCxSizeFrame, dpi) +
                             GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
        var verticalGrip = GetSystemMetricsForDpi(SmCySizeFrame, dpi) +
                           GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
        var hitTest = GetResizeHitTest(
            bounds.ToPixelRect(),
            pointer,
            Math.Max(horizontalGrip, 6),
            Math.Max(verticalGrip, 6),
            window.CanResize,
            window.WindowState == WindowState.Maximized);

        if (hitTest != Win32Properties.Win32HitTestValue.Client)
        {
            handled = true;
            return new IntPtr((int)hitTest);
        }

        return IntPtr.Zero;
    }

    private static void FitMaximizedClientAreaToWorkArea(IntPtr windowHandle, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var parameters = Marshal.PtrToStructure<NcCalcSizeParameters>(lParam);
        parameters.ProposedClient = monitorInfo.WorkArea;
        Marshal.StructureToPtr(parameters, lParam, false);
    }

    private static PixelRect ToPixelRect(this NativeRect rect)
        => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NcCalcSizeParameters
    {
        public NativeRect ProposedClient;
        public NativeRect CurrentWindow;
        public NativeRect CurrentClient;
        public IntPtr WindowPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref uint attributeValue,
        int attributeSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);
}
