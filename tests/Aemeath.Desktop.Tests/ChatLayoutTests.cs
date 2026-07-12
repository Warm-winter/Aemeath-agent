using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    [AvaloniaFact]
    public void SessionActions_UseExactRenameLabelAndAccessibleName()
    {
        using var temp = new TemporaryDirectory();
        var settings = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        var window = new ChatWindow(
            new NoOpChatService(),
            settings,
            new ChatSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new AttachmentThumbnailCache());

        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var rename = window.FindControl<Button>("RenameSessionButton")!;

            Assert.Equal("\u91cd\u547d\u540d", rename.Content);
            Assert.Equal("\u91cd\u547d\u540d\u5f53\u524d\u5bf9\u8bdd", Avalonia.Automation.AutomationProperties.GetName(rename));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task InitialOpen_WithSidebarAndLateLayoutGrowth_StaysAtLatestMessage()
    {
        using var temp = new TemporaryDirectory();
        var settings = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        settings.Current.IsChatSidebarOpen = true;
        settings.Save();
        var sessions = new ChatSessionStore(Path.Combine(temp.Path, "sessions.json"));
        var session = sessions.CreateSession("initial scroll regression");
        for (var index = 0; index < 24; index++)
        {
            sessions.AppendMessage(
                session.Id,
                index % 2 == 0 ? "user" : "assistant",
                $"message {index}: " + string.Join(" ", Enumerable.Repeat("layout content", 36)));
        }

        var window = new ChatWindow(
            new NoOpChatService(),
            settings,
            sessions,
            new AttachmentThumbnailCache())
        {
            Width = 940,
            Height = 720
        };

        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var messages = window.FindControl<StackPanel>("MessagesPanel")!;
            messages.Children.Add(new Border { Height = 900 });
            Dispatcher.UIThread.RunJobs();

            await Task.Delay(260);
            Dispatcher.UIThread.RunJobs();

            var viewer = window.FindControl<ScrollViewer>("ChatScrollViewer")!;
            var maxOffset = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            Assert.True(maxOffset > 0);
            Assert.InRange(Math.Abs(viewer.Offset.Y - maxOffset), 0, 1.5);

            var verticalBar = viewer.GetVisualDescendants()
                .OfType<ScrollBar>()
                .Single(bar => bar.Orientation == Orientation.Vertical);
            Assert.True(verticalBar.IsVisible);
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
