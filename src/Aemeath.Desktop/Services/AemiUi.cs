using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Aemeath.Desktop.Services;

internal static class AemiUi
{
    public const string Void = "#050712";
    public const string VoidDeep = "#02040A";
    public const string Panel = "#111A2E";
    public const string PanelSoft = "#16213A";
    public const string PanelRaised = "#1C2A48";
    public const string Glass = "#D90E1627";
    public const string GlassSoft = "#B8121C31";
    public const string Border = "#375070";
    public const string BorderSoft = "#2A3A59";
    public const string Halo = "#73C7FF";
    public const string HaloSoft = "#B9E5FF";
    public const string Pink = "#FF78BE";
    public const string PinkSoft = "#FFB6DA";
    public const string Star = "#FFE07A";
    public const string Ghost = "#F7FBFF";
    public const string TextSecondary = "#C5D4F2";
    public const string TextMuted = "#8FA2C8";
    public const string Success = "#65E8B0";
    public const string Error = "#FF6B7A";

    public static SolidColorBrush Brush(string color) => new(Color.Parse(color));

    public static Border Surface(
        Control? child = null,
        double radius = 14,
        double padding = 14,
        string background = Glass,
        string border = Border,
        double opacity = 1)
        => new()
        {
            Child = child,
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(padding),
            Background = Brush(background),
            BorderBrush = Brush(border),
            BorderThickness = new Thickness(1),
            Opacity = opacity
        };

    public static TextBlock Text(
        string text,
        double fontSize = 14,
        string color = Ghost,
        FontWeight? weight = null,
        TextWrapping wrapping = TextWrapping.NoWrap)
        => new()
        {
            Text = text,
            FontSize = fontSize,
            Foreground = Brush(color),
            FontWeight = weight ?? FontWeight.Normal,
            TextWrapping = wrapping
        };

    public static Border Badge(string text, string tone = "halo")
    {
        var (foreground, background, border) = tone switch
        {
            "star" => (Star, "#26FFE07A", "#66FFE07A"),
            "pink" => (PinkSoft, "#24FF78BE", "#66FF78BE"),
            "danger" => ("#FFD9E3", "#32FF6B7A", "#88FF6B7A"),
            "success" => (Success, "#2665E8B0", "#7765E8B0"),
            _ => (HaloSoft, "#2473C7FF", "#6673C7FF")
        };

        return new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 3),
            Background = Brush(background),
            BorderBrush = Brush(border),
            BorderThickness = new Thickness(1),
            Child = Text(text, 12, foreground, FontWeight.SemiBold)
        };
    }

    public static Button Button(string text, string? style = null, double minWidth = 86)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = minWidth,
            MinHeight = 36,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        if (!string.IsNullOrWhiteSpace(style))
        {
            button.Classes.Add(style);
        }

        return button;
    }

    public static Button IconButton(IImage icon, string tooltip)
    {
        var button = new Button
        {
            Content = new Image { Source = icon, Width = 16, Height = 16, Stretch = Stretch.Uniform },
            Width = 38,
            Height = 34,
            MinWidth = 38,
            MinHeight = 34,
            Padding = new Thickness(6, 4),
            Background = Brush("#3A1C2A48"),
            Foreground = Brush(Ghost),
            BorderBrush = Brush("#6673C7FF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9)
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    public static TextBlock Label(string text)
        => Text(text, 12, TextMuted, FontWeight.SemiBold);
}
