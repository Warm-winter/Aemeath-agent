using Avalonia.Media;
using Aemeath.Desktop.Services;

namespace Aemeath.Desktop.Tests;

public sealed class ThemeTokenTests
{
    [Theory]
    [InlineData(AemiUi.PrimaryForeground, AemiUi.PinkDeep)]
    [InlineData(AemiUi.TextMuted, AemiUi.HaloSoft)]
    [InlineData(AemiUi.Success, AemiUi.SuccessSurface)]
    [InlineData(AemiUi.Warning, AemiUi.WarningSurface)]
    [InlineData(AemiUi.Error, AemiUi.ErrorSurface)]
    [InlineData(AemiUi.InfoForeground, AemiUi.InfoSurface)]
    public void SemanticForeground_BackgroundPair_MeetsNormalTextContrast(string foreground, string background)
    {
        var ratio = ContrastRatio(Color.Parse(foreground), Color.Parse(background));

        Assert.True(ratio >= 4.5, $"Contrast {ratio:F2}:1 is below 4.5:1 for {foreground} on {background}.");
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.04045
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) +
               0.7152 * Channel(color.G) +
               0.0722 * Channel(color.B);
    }
}
