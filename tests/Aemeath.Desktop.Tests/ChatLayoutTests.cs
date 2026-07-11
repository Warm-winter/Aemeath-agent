using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Aemeath.Core.Configuration;
using Aemeath.Desktop.Services;
using Aemeath.Desktop.Views;

namespace Aemeath.Desktop.Tests;

public sealed class ChatLayoutTests
{
    [AvaloniaTheory]
    [InlineData(720, SplitViewDisplayMode.Overlay)]
    [InlineData(940, SplitViewDisplayMode.Inline)]
    [InlineData(1200, SplitViewDisplayMode.Inline)]
    public void SessionSidebar_UsesSharedOuterBoundsAndStableWidth(
        double width,
        SplitViewDisplayMode expectedDisplayMode)
    {
        using var temp = new TemporaryDirectory();
        var settings = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        settings.Current.IsChatSidebarOpen = true;
        settings.Save();
        var sessions = new ChatSessionStore(Path.Combine(temp.Path, "sessions.json"));
        var window = new ChatWindow(
            new NoOpChatService(),
            settings,
            sessions,
            new AttachmentThumbnailCache())
        {
            Width = width,
            Height = 800
        };

        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var splitView = window.FindControl<SplitView>("ChatSplitView")!;
            var sidebar = window.FindControl<Border>("SessionSidebarCard")!;
            var main = window.FindControl<Grid>("ChatMainLayout")!;
            var sidebarTopLeft = sidebar.TranslatePoint(default, window)!.Value;
            var mainTopLeft = main.TranslatePoint(default, window)!.Value;

            Assert.Equal(expectedDisplayMode, splitView.DisplayMode);
            Assert.Equal(240, sidebar.Bounds.Width, precision: 1);
            Assert.Equal(mainTopLeft.Y, sidebarTopLeft.Y, precision: 1);
            Assert.Equal(main.Bounds.Height, sidebar.Bounds.Height, precision: 1);
            Assert.Equal(16, sidebarTopLeft.X, precision: 1);

            if (expectedDisplayMode == SplitViewDisplayMode.Inline)
            {
                var gap = mainTopLeft.X - (sidebarTopLeft.X + sidebar.Bounds.Width);
                Assert.Equal(12, gap, precision: 1);
            }
            else
            {
                Assert.Equal(16, mainTopLeft.X, precision: 1);
            }
        }
        finally
        {
            window.Close();
        }
    }
}
