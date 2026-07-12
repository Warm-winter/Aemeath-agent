using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aemeath.Pet;

namespace Aemeath.Desktop.Tests;

public sealed class PetMenuTests
{
    [AvaloniaFact]
    public void BuildContextMenu_UsesFlatInteractionsAndExpectedBottomGroup()
    {
        var window = new PetWindow();
        window.Show();
        try
        {
            var menu = window.BuildContextMenu();
            var items = menu.Items.Cast<object>().ToArray();

            Assert.Collection(
                items,
                item => AssertHeader(item, "\u6253\u5f00\u5bf9\u8bdd"),
                item => AssertHeader(item, "\u6478\u6478\u5c0f\u7231"),
                item => AssertHeader(item, "\u968f\u673a\u95ee\u5019"),
                item => AssertHeader(item, "\u6253\u5f00\u8bbe\u7f6e"),
                item => AssertHeader(item, "\u8ddf\u968f\u9f20\u6807"),
                item => AssertHeader(item, "\u7a97\u53e3\u884c\u4e3a"),
                item => AssertHeader(item, "\u5916\u89c2"),
                item => Assert.IsType<Separator>(item),
                item => AssertHeader(item, "\u6536\u7eb3\u5230\u7cfb\u7edf\u6258\u76d8"),
                item => AssertHeader(item, "\u9000\u51fa\u7231\u5f25\u65af\u52a9\u624b"));

            var topLevelHeaders = items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray();
            Assert.DoesNotContain("\u4e92\u52a8", topLevelHeaders);

            var windowBehavior = items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "\u7a97\u53e3\u884c\u4e3a");
            Assert.DoesNotContain(
                windowBehavior.Items.OfType<MenuItem>(),
                item => item.Header?.ToString() is "\u6536\u7eb3\u5230\u7cfb\u7edf\u6258\u76d8" or "\u8ddf\u968f\u9f20\u6807");

            var followItem = items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "\u8ddf\u968f\u9f20\u6807");
            Assert.Equal(MenuItemToggleType.CheckBox, followItem.ToggleType);

            Assert.Contains("pet-menu", menu.Classes);
            var allMenuItems = EnumerateMenuItems(items).ToArray();
            Assert.All(allMenuItems, item => Assert.Contains("pet-menu-item", item.Classes));

            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            foreach (var item in items.OfType<MenuItem>())
            {
                AssertRenderedMenuItemHeadersAreCentered(item);
            }
            menu.Close();
        }
        finally
        {
            window.Close();
        }
    }



    private static void AssertRenderedMenuItemHeadersAreCentered(MenuItem item)
    {
        var headerPresenter = item
            .GetVisualDescendants()
            .OfType<ContentPresenter>()
            .FirstOrDefault(presenter => presenter.Name == "PART_HeaderPresenter");
        Assert.NotNull(headerPresenter);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, headerPresenter.HorizontalAlignment);

        if (item.Items.Count == 0)
        {
            return;
        }

        item.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        foreach (var child in item.Items.OfType<MenuItem>())
        {
            AssertRenderedMenuItemHeadersAreCentered(child);
        }
        item.IsSubMenuOpen = false;
        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<MenuItem> EnumerateMenuItems(IEnumerable<object> items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in EnumerateMenuItems(item.Items.Cast<object>()))
            {
                yield return child;
            }
        }
    }

    private static void AssertHeader(object item, string expected)
    {
        var menuItem = Assert.IsType<MenuItem>(item);
        Assert.Equal(expected, menuItem.Header?.ToString());
    }
}
