using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Aemeath.Desktop.Views;

namespace Aemeath.Desktop.Tests;

public sealed class AemeathTitleBarTests
{
    [AvaloniaFact]
    public void WindowButtons_ExposeAccessibleNamesAndTrackWindowState()
    {
        var titleBar = new AemeathTitleBar { Title = "Test" };
        var window = new Window
        {
            Width = 640,
            Height = 480,
            WindowDecorations = WindowDecorations.BorderOnly,
            Content = titleBar
        };
        window.Show();
        try
        {
            var minimize = Assert.IsType<Button>(titleBar.FindControl<Button>("MinimizeButton"));
            var maximize = Assert.IsType<Button>(titleBar.FindControl<Button>("MaximizeButton"));
            var close = Assert.IsType<Button>(titleBar.FindControl<Button>("CloseButton"));
            var minimizeGlyph = Assert.IsType<Border>(titleBar.FindControl<Border>("MinimizeGlyph"));

            Assert.True(minimizeGlyph.Width > 0);
            Assert.True(minimizeGlyph.Height > 0);
            Assert.Equal("\u6700\u5c0f\u5316\u7a97\u53e3", AutomationProperties.GetName(minimize));
            Assert.Equal("\u6700\u5927\u5316\u7a97\u53e3", AutomationProperties.GetName(maximize));
            Assert.Equal("\u5173\u95ed\u7a97\u53e3", AutomationProperties.GetName(close));

            maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.Equal("\u8fd8\u539f\u7a97\u53e3", AutomationProperties.GetName(maximize));

            maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(WindowState.Normal, window.WindowState);

            minimize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(WindowState.Minimized, window.WindowState);
        }
        finally
        {
            window.WindowState = WindowState.Normal;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CloseButton_ClosesHostWindow()
    {
        var titleBar = new AemeathTitleBar();
        var window = new Window { Content = titleBar };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();

        titleBar.FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(closed);
    }

    [AvaloniaFact]
    public void UnsavedDialogTitleBar_HidesRedundantCloseButtonOnlyForThatFlow()
    {
        var unsavedTitleBar = DialogService.BuildDialogTitleBar("\u672a\u4fdd\u5b58\u66f4\u6539", showCloseButton: false);
        var standardTitleBar = DialogService.BuildDialogTitleBar("\u786e\u8ba4\u64cd\u4f5c");

        Assert.False(unsavedTitleBar.ShowMinimizeButton);
        Assert.False(unsavedTitleBar.ShowMaximizeButton);
        Assert.False(unsavedTitleBar.ShowCloseButton);
        Assert.True(standardTitleBar.ShowCloseButton);
    }

    [AvaloniaFact]
    public void DialogMode_HidesMinimizeAndMaximizeButKeepsClose()
    {
        var titleBar = new AemeathTitleBar
        {
            ShowMinimizeButton = false,
            ShowMaximizeButton = false
        };
        var window = new Window { Content = titleBar };
        window.Show();
        try
        {
            Assert.False(titleBar.FindControl<Button>("MinimizeButton")!.IsVisible);
            Assert.False(titleBar.FindControl<Button>("MaximizeButton")!.IsVisible);
            Assert.True(titleBar.FindControl<Button>("CloseButton")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
