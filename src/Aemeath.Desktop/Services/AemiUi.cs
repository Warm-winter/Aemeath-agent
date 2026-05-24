using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Aemeath.Desktop.Services;

internal static class AemiUi
{
    public const string Void = "#FFF1F6";
    public const string VoidDeep = "#FFE1EE";
    public const string Panel = "#FFFFFF";
    public const string PanelSoft = "#FFF8FB";
    public const string PanelRaised = "#FFFFFF";
    public const string Glass = "#FFFFFF";
    public const string GlassSoft = "#FFF8FB";
    public const string Border = "#F3C2D4";
    public const string BorderSoft = "#F8D8E4";
    public const string Halo = "#FF9BCF";
    public const string HaloSoft = "#FFE1EE";
    public const string Pink = "#FF69B4";
    public const string PinkSoft = "#FFD1E5";
    public const string Star = "#FF69B4";
    public const string Ghost = "#4A2A3A";
    public const string TextSecondary = "#7A5564";
    public const string TextMuted = "#9A7482";
    public const string Success = "#3CA66B";
    public const string Error = "#D94A62";

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
            "star" => (Pink, "#FFE1EE", "#F3C2D4"),
            "pink" => (Pink, "#FFD1E5", "#F3C2D4"),
            "danger" => (Error, "#FFEAF0", "#F0A9B8"),
            "success" => (Success, "#E9FFF2", "#A7E5BE"),
            _ => (TextSecondary, "#FFE1EE", "#F3C2D4")
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
            Background = Brush("#FFE1EE"),
            Foreground = Brush(Ghost),
            BorderBrush = Brush("#F3C2D4"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9)
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    public static TextBlock Label(string text)
        => Text(text, 12, TextMuted, FontWeight.SemiBold);
}

