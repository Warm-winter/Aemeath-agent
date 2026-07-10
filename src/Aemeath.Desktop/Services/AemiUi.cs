using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

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
    public const string PinkDeep = "#E84D8E";
    public const string Icon = "#7A3156";
    public const string Star = "#FF69B4";
    public const string Ghost = "#4A2A3A";
    public const string PrimaryForeground = "#07101E";
    public const string TextSecondary = "#7A5564";
    public const string TextMuted = "#80606E";
    public const string TextFaint = "#80606E";
    public const string Success = "#1F7A4D";
    public const string Warning = "#8A5300";
    public const string Error = "#B52E49";
    public const string SuccessSurface = "#E9FFF2";
    public const string SuccessBorder = "#9BD4B2";
    public const string WarningSurface = "#FFF3E0";
    public const string WarningBorder = "#E5C08D";
    public const string ErrorSurface = "#FFEAF0";
    public const string ErrorBorder = "#E8A8B6";
    public const string InfoSurface = "#EEF3FF";
    public const string InfoForeground = "#3A5A8C";
    public const string CodeSurface = "#FFF4F8";

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
            "star" => (Icon, HaloSoft, Border),
            "pink" => (Icon, PinkSoft, Border),
            "danger" => (Error, ErrorSurface, ErrorBorder),
            "success" => (Success, SuccessSurface, SuccessBorder),
            _ => (TextSecondary, HaloSoft, Border)
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
            Width = 42,
            Height = 42,
            MinWidth = 42,
            MinHeight = 42,
            Padding = new Thickness(6, 4),
            Background = Brush(HaloSoft),
            Foreground = Brush(Ghost),
            BorderBrush = Brush(Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9)
        };
        button.Classes.Add("ghost");
        button.Classes.Add("icon");
        AutomationProperties.SetName(button, tooltip);
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    public static TextBlock Label(string text)
        => Text(text, 12, TextMuted, FontWeight.SemiBold);

    /// <summary>
    /// 根据状态返回状态点颜色（cherry-studio 风格的 4 态配色）。
    /// status 取值：disabled/connecting/ok/error。
    /// </summary>
    public static string StatusColor(string? status)
    {
        return (status ?? string.Empty).ToLowerInvariant() switch
        {
            "ok" or "connected" or "success" => Success,       // 绿
            "connecting" or "running" or "loading" => Warning, // 黄
            "error" or "failed" or "fail" => Error,            // 红
            _ => TextFaint                                        // 灰（disabled/未加载）
        };
    }

    /// <summary>状态点对应的中文标签。</summary>
    public static string StatusLabel(string? status, bool enabled = true)
    {
        if (!enabled)
        {
            return "已停用";
        }
        return (status ?? string.Empty).ToLowerInvariant() switch
        {
            "ok" or "connected" or "success" => "已连接",
            "connecting" or "running" or "loading" => "连接中",
            "error" or "failed" or "fail" => "错误",
            _ => "未加载"
        };
    }

    /// <summary>从 avares URI 加载位图。</summary>
    public static Bitmap LoadBitmap(string uri)
    {
        using var stream = AssetLoader.Open(new Uri(uri));
        return new Bitmap(stream);
    }

    /// <summary>用路径数据构造一个白色矢量图标（坐标已按视图盒缩放）。</summary>
    public static DrawingImage CreateVectorIcon(string pathData, double width, double height, string color = Icon)
    {
        var geometry = StreamGeometry.Parse(pathData);
        var drawing = new GeometryDrawing
        {
            Geometry = geometry,
            Brush = Brush(color)
        };
        var group = new DrawingGroup();
        group.Children.Add(drawing);
        return new DrawingImage(group);
    }

    /// <summary>用 SVG 路径数据（8000x8000 视图盒）构造一个白色矢量图标，自动缩放并翻转 Y 轴。</summary>
    public static DrawingImage CreateSvgTransformedVectorIcon(string pathData, double width, double height)
    {
        var geometry = StreamGeometry.Parse(pathData);
        var drawing = new GeometryDrawing
        {
            Geometry = geometry,
            Brush = Brush(Icon)
        };
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(width / 8000d, -height / 8000d));
        transform.Children.Add(new TranslateTransform(0, height));

        var group = new DrawingGroup { Transform = transform };
        group.Children.Add(drawing);
        return new DrawingImage(group);
    }
}

