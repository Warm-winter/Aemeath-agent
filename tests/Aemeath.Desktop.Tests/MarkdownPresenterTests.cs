using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Aemeath.Desktop.Views;

namespace Aemeath.Desktop.Tests;

public sealed class MarkdownPresenterTests
{
    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/path", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/secret.txt", false)]
    [InlineData("/relative/path", false)]
    public void IsSafeLink_Scheme_ReturnsExpectedResult(string value, bool expected)
    {
        Assert.Equal(expected, MarkdownPresenter.IsSafeLink(value));
    }

    [AvaloniaFact]
    public void Constructor_RemoteImage_DoesNotCreateImageControl()
    {
        var presenter = new MarkdownPresenter("![远程图片](https://example.com/image.png)");

        var images = presenter.GetLogicalDescendants().OfType<Image>().ToList();

        Assert.Empty(images);
    }
}
