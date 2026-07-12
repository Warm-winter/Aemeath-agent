using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
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

            var viewer = window.FindControl<ScrollViewer>("ChatScrollViewer")!;
            var messages = window.FindControl<StackPanel>("MessagesPanel")!;
            var root = Assert.IsType<StackPanel>(Assert.Single(messages.Children));
            var row = Assert.IsType<Grid>(Assert.Single(root.Children));
            var bubble = row.Children
                .OfType<Border>()
                .Single(border => Math.Abs(border.MaxWidth - 720) < 0.01);

            Assert.Contains("message-row", row.Classes);
            Assert.Equal(HorizontalAlignment.Stretch, viewer.HorizontalContentAlignment);
            Assert.Equal(HorizontalAlignment.Stretch, messages.HorizontalAlignment);
            Assert.True(double.IsNaN(row.Width), "Message rows must stretch instead of binding Width to live bounds.");
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
            Assert.Contains("compact", rename.Classes);
            Assert.Equal(new Thickness(0), rename.Margin);
            Assert.Equal(8, rename.Padding.Left, precision: 1);
            Assert.Equal(8, rename.Padding.Right, precision: 1);

            var textProbe = new TextBlock
            {
                Text = Assert.IsType<string>(rename.Content),
                FontFamily = rename.FontFamily,
                FontSize = rename.FontSize,
                FontStyle = rename.FontStyle,
                FontWeight = rename.FontWeight
            };
            textProbe.Measure(Size.Infinity);
            var availableContentWidth = rename.Bounds.Width
                - rename.Padding.Left
                - rename.Padding.Right
                - rename.BorderThickness.Left
                - rename.BorderThickness.Right;
            Assert.True(
                availableContentWidth >= textProbe.DesiredSize.Width,
                $"Rename text needs {textProbe.DesiredSize.Width:F1}px but only {availableContentWidth:F1}px is available.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task InitialOpen_WithSidebarAndLayoutGrowthAfterInitialPin_StaysAtLatestMessage()
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

            // The previous fixed-frame stabilizer completed after at most ~192 ms.
            // Reproduce a real late layout change after that window has already elapsed.
            await Task.Delay(260);
            Dispatcher.UIThread.RunJobs();
            messages.Children.Add(new Border { Height = 900 });
            Dispatcher.UIThread.RunJobs();

            await Task.Delay(120);
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

    [AvaloniaFact]
    public async Task UserScrollsUp_LateLayoutGrowth_DoesNotForceLatest()
    {
        using var temp = new TemporaryDirectory();
        var settings = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        settings.Current.IsChatSidebarOpen = true;
        settings.Save();
        var sessions = new ChatSessionStore(Path.Combine(temp.Path, "sessions.json"));
        var session = sessions.CreateSession("manual scroll regression");
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
            await Task.Delay(260);
            Dispatcher.UIThread.RunJobs();

            var viewer = window.FindControl<ScrollViewer>("ChatScrollViewer")!;
            var messages = window.FindControl<StackPanel>("MessagesPanel")!;
            var jump = window.FindControl<Button>("JumpToLatestButton")!;
            var originalMaximum = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            viewer.Offset = new Vector(viewer.Offset.X, Math.Max(0, originalMaximum - 320));
            Dispatcher.UIThread.RunJobs();
            var userOffset = viewer.Offset.Y;

            messages.Children.Add(new Border { Height = 900 });
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(120);
            Dispatcher.UIThread.RunJobs();

            var newMaximum = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            Assert.True(newMaximum > originalMaximum);
            Assert.InRange(Math.Abs(viewer.Offset.Y - userOffset), 0, 1.5);
            Assert.True(viewer.Offset.Y < newMaximum - 100);
            Assert.True(jump.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void UserScrollsUp_WhileLatestPinIsQueued_CancelsPendingFollow()
    {
        using var temp = new TemporaryDirectory();
        var settings = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        settings.Current.IsChatSidebarOpen = true;
        settings.Save();
        var sessions = new ChatSessionStore(Path.Combine(temp.Path, "sessions.json"));
        var session = sessions.CreateSession("queued pin regression");
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
            var viewer = window.FindControl<ScrollViewer>("ChatScrollViewer")!;
            var jump = window.FindControl<Button>("JumpToLatestButton")!;
            var maximum = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            Assert.True(maximum > 320);

            jump.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var userOffset = maximum - 320;
            viewer.Offset = new Vector(viewer.Offset.X, userOffset);
            Dispatcher.UIThread.RunJobs();

            Assert.InRange(Math.Abs(viewer.Offset.Y - userOffset), 0, 1.5);
            Assert.True(jump.IsVisible);
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
