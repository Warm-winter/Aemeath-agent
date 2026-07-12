using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
}
