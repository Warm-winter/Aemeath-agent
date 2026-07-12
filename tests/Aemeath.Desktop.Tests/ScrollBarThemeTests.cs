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

public sealed class ScrollBarThemeTests
{
    [AvaloniaFact]
    public void GlobalScrollBar_UsesPersistentCompactThemeWithoutLineButtons()
    {
        var viewer = new ScrollViewer
        {
            Width = 320,
            Height = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Width = 280, Height = 1200 }
        };
        var window = new Window
        {
            Width = 360,
            Height = 300,
            Content = viewer
        };

        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var scrollBar = viewer.GetVisualDescendants()
                .OfType<ScrollBar>()
                .Single(bar => bar.Orientation == Orientation.Vertical);
            var thumb = scrollBar.GetVisualDescendants().OfType<Thumb>().Single();
            var track = scrollBar.GetVisualDescendants()
                .OfType<Avalonia.Controls.Shapes.Rectangle>()
                .Single(rectangle => rectangle.Name == "TrackRect");
            var lineButtons = scrollBar.GetVisualDescendants()
                .OfType<RepeatButton>()
                .Where(button => button.Name is "PART_LineUpButton" or "PART_LineDownButton")
                .ToArray();

            Assert.False(scrollBar.AllowAutoHide);
            Assert.Equal(12, scrollBar.Width, precision: 1);
            Assert.Equal(6, thumb.Width, precision: 1);
            Assert.Equal(32, thumb.MinHeight, precision: 1);
            Assert.Equal(new CornerRadius(999), thumb.CornerRadius);
            Assert.Equal(0.38, track.Opacity, precision: 2);
            Assert.Equal(2, lineButtons.Length);
            Assert.All(lineButtons, button =>
            {
                Assert.False(button.IsHitTestVisible);
                Assert.Equal(0, button.Width, precision: 1);
                Assert.Equal(0, button.Height, precision: 1);
            });
            Assert.NotNull(thumb.Transitions);
            Assert.True(thumb.Transitions!.Count >= 3);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ChatScrollBar_KeepsHitTargetButUsesNarrowVisualTrack()
    {
        using var temp = new TemporaryDirectory();
        var settings = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        settings.Current.IsChatSidebarOpen = true;
        settings.Save();
        var sessions = new ChatSessionStore(Path.Combine(temp.Path, "sessions.json"));
        var session = sessions.CreateSession("scrollbar regression");
        for (var index = 0; index < 18; index++)
        {
            sessions.AppendMessage(
                session.Id,
                index % 2 == 0 ? "user" : "assistant",
                string.Join(" ", Enumerable.Repeat($"message {index}", 40)));
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
            var scrollBar = viewer.GetVisualDescendants()
                .OfType<ScrollBar>()
                .Single(bar => bar.Orientation == Orientation.Vertical);
            var thumb = scrollBar.GetVisualDescendants().OfType<Thumb>().Single();
            var track = scrollBar.GetVisualDescendants()
                .OfType<Avalonia.Controls.Shapes.Rectangle>()
                .Single(rectangle => rectangle.Name == "TrackRect");

            Assert.Contains("chat-messages", viewer.Classes);
            Assert.Equal(12, scrollBar.Width, precision: 1);
            Assert.Equal(3, track.Width, precision: 1);
            Assert.Equal(5, thumb.Width, precision: 1);
        }
        finally
        {
            window.Close();
        }
    }
}
