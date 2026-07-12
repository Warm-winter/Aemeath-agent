using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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
            var paneBackground = Assert.IsAssignableFrom<ISolidColorBrush>(splitView.PaneBackground);
            Assert.Equal(Colors.Transparent, paneBackground.Color);
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

    [AvaloniaFact]
    public void AssistantMessage_OpenSidebar_StaysInsideMessageViewport()
    {
        using var temp = new TemporaryDirectory();
        var settings = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        settings.Current.IsChatSidebarOpen = true;
        settings.Save();
        var sessions = new ChatSessionStore(Path.Combine(temp.Path, "sessions.json"));
        var session = sessions.CreateSession("layout regression");
        sessions.AppendMessage(
            session.Id,
            "assistant",
            string.Join(
                " ",
                Enumerable.Repeat(
                    "This assistant message is intentionally long enough to use the full bubble width.",
                    24)));
        var window = new ChatWindow(
            new NoOpChatService(),
            settings,
            sessions,
            new AttachmentThumbnailCache())
        {
            Width = 1052,
            Height = 820
        };

        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var messages = window.FindControl<StackPanel>("MessagesPanel")!;
            var root = Assert.IsType<StackPanel>(Assert.Single(messages.Children));
            var row = Assert.IsType<Grid>(Assert.Single(root.Children));
            var bubble = row.Children
                .OfType<Border>()
                .Single(border => Math.Abs(border.MaxWidth - 720) < 0.01);

            Assert.Contains("message-row", row.Classes);
            Assert.True(messages.Bounds.Width < 774, "The test must reproduce the constrained inline layout.");
            Assert.Equal(messages.Bounds.Width, row.Bounds.Width, precision: 1);
            AssertControlRightEdgeIsInside(bubble, messages, window);

            var initialWidth = row.Bounds.Width;
            window.Width = 1200;
            Dispatcher.UIThread.RunJobs();

            Assert.True(row.Bounds.Width > initialWidth);
            Assert.Equal(messages.Bounds.Width, row.Bounds.Width, precision: 1);
            AssertControlRightEdgeIsInside(bubble, messages, window);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertControlRightEdgeIsInside(Control control, Control container, Window window)
    {
        var controlOrigin = control.TranslatePoint(default, window)!.Value;
        var containerOrigin = container.TranslatePoint(default, window)!.Value;
        var controlRight = controlOrigin.X + control.Bounds.Width;
        var containerRight = containerOrigin.X + container.Bounds.Width;

        Assert.True(
            controlRight <= containerRight + 0.5,
            $"Control right edge {controlRight:F1} exceeded container right edge {containerRight:F1}.");
    }

}
