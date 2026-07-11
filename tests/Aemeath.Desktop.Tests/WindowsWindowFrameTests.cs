using Avalonia;
using Avalonia.Controls;
using Aemeath.Desktop.Services;

namespace Aemeath.Desktop.Tests;

public sealed class WindowsWindowFrameTests
{
    private static readonly PixelRect Bounds = new(100, 100, 800, 600);

    [Theory]
    [InlineData(101, 101, Win32Properties.Win32HitTestValue.TopLeft)]
    [InlineData(899, 101, Win32Properties.Win32HitTestValue.TopRight)]
    [InlineData(101, 699, Win32Properties.Win32HitTestValue.BottomLeft)]
    [InlineData(899, 699, Win32Properties.Win32HitTestValue.BottomRight)]
    [InlineData(101, 400, Win32Properties.Win32HitTestValue.Left)]
    [InlineData(899, 400, Win32Properties.Win32HitTestValue.Right)]
    [InlineData(500, 101, Win32Properties.Win32HitTestValue.Top)]
    [InlineData(500, 699, Win32Properties.Win32HitTestValue.Bottom)]
    [InlineData(500, 400, Win32Properties.Win32HitTestValue.Client)]
    public void GetResizeHitTest_ReturnsExpectedEdge(
        int x,
        int y,
        Win32Properties.Win32HitTestValue expected)
    {
        var actual = WindowsWindowFrame.GetResizeHitTest(
            Bounds,
            new PixelPoint(x, y),
            horizontalGrip: 12,
            verticalGrip: 12,
            canResize: true,
            isMaximized: false);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void GetResizeHitTest_NonResizableOrMaximized_ReturnsClient(
        bool canResize,
        bool isMaximized)
    {
        var actual = WindowsWindowFrame.GetResizeHitTest(
            Bounds,
            new PixelPoint(101, 101),
            horizontalGrip: 12,
            verticalGrip: 12,
            canResize,
            isMaximized);

        Assert.Equal(Win32Properties.Win32HitTestValue.Client, actual);
    }
}
