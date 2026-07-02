using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using UIA = System.Windows.Automation;

namespace Aemeath.Core.ComputerControl;

/// <summary>
/// 基于 Windows UIAutomation（UIA）的控件树枚举 + 截图标注。
///
/// 这是「电脑控制 Agent（轨 A）」的感知层：从当前前台窗口抓取可交互控件，
/// 给每个控件分配数字标签，并在截图上画出编号，供视觉 LLM 决策下一步操作。
///
/// 这一层移植自 UFO（ufo/automator/ui_control/）的控件标注思路：
/// - 用 UIA 遍历前台窗口的可交互子控件
/// - 过滤掉不可见/无名称/无边界/重复的控件
/// - 每个控件标一个递增编号，同时记录其在窗口里的坐标，供执行层点击
/// </summary>
public static class UiaControlTree
{
    /// <summary>最多枚举的控件数（避免控件树过大撑爆 LLM 上下文）。</summary>
    private const int MaxControls = 80;

    /// <summary>抓取当前前台窗口的可交互控件列表（带坐标，用于后续点击）。</summary>
    public static List<AnnotatedControl> CaptureForeground(IntPtr? hwndHint = null)
    {
        var result = new List<AnnotatedControl>();
        try
        {
            var hwnd = hwndHint ?? GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return result;
            }

            var element = UIA.AutomationElement.FromHandle(hwnd);
            if (element is null)
            {
                return result;
            }

            // 只取「可交互」的控件类型：按钮/编辑/超链接/列表项/复选/单选/菜单项/组合框/标签/工具栏/图片/窗格等
            var conditions = BuildClickableCondition();
            var walker = new UIA.TreeWalker(conditions);
            CollectControls(element, walker, result, hwnd);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[uia] CaptureForeground 失败：{ex.Message}");
        }

        // 去重（同名 + 同边界算一个）后重新编号
        return DedupAndNumber(result);
    }

    private static void CollectControls(UIA.AutomationElement root, UIA.TreeWalker walker, List<AnnotatedControl> sink, IntPtr hwnd)
    {
        var stack = new Stack<UIA.AutomationElement>();
        stack.Push(root);

        while (stack.Count > 0 && sink.Count < MaxControls)
        {
            var current = stack.Pop();
            try
            {
                // 先尝试把 current 自身作为候选控件
                if (!ReferenceEquals(current, root))
                {
                    TryAdd(current, sink);
                }

                // 遍历符合条件的子控件
                var child = walker.GetFirstChild(current);
                while (child is not null && sink.Count < MaxControls)
                {
                    stack.Push(child);
                    child = walker.GetNextSibling(child);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[uia] CollectControls 子节点失败：{ex.Message}");
            }
        }
    }

    private static void TryAdd(UIA.AutomationElement el, List<AnnotatedControl> sink)
    {
        try
        {
            if (!el.Current.IsEnabled)
            {
                return;
            }

            var rect = el.Current.BoundingRectangle;
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            // 跨屏或异常的巨大边界跳过（UFO 同样的处理）
            if (rect.Width > 4000 || rect.Height > 4000)
            {
                return;
            }

            var name = el.Current.Name?.Trim();
            var type = el.Current.ControlType;
            var typeName = type == null ? "Control" : type.ProgrammaticName?.Replace("ControlType.", "") ?? "Control";

            sink.Add(new AnnotatedControl(
                Id: 0, // 稍后编号
                Name: name ?? string.Empty,
                TypeName: typeName,
                Left: (int)rect.X,
                Top: (int)rect.Y,
                Width: (int)rect.Width,
                Height: (int)rect.Height,
                Element: el));
        }
        catch
        {
            // 单个控件失败不影响整体
        }
    }

    private static List<AnnotatedControl> DedupAndNumber(List<AnnotatedControl> raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AnnotatedControl>();
        var idx = 1;
        foreach (var c in raw)
        {
            var key = $"{c.Name}|{c.Left},{c.Top},{c.Width},{c.Height}";
            if (string.IsNullOrEmpty(c.Name) && c.Width < 6 && c.Height < 6)
            {
                continue; // 无名又极小的控件无用
            }

            if (seen.Add(key))
            {
                result.Add(c with { Id = idx++ });
            }
        }

        return result;
    }

    private static UIA.Condition BuildClickableCondition()
    {
        // Or 条件：匹配常见的可交互控件类型（UFO 的 control_filter 思路）
        var controlTypes = new[]
        {
            UIA.ControlType.Button,
            UIA.ControlType.CheckBox,
            UIA.ControlType.RadioButton,
            UIA.ControlType.Edit,
            UIA.ControlType.Hyperlink,
            UIA.ControlType.ListItem,
            UIA.ControlType.ComboBox,
            UIA.ControlType.MenuItem,
            UIA.ControlType.Menu,
            UIA.ControlType.TabItem,
            UIA.ControlType.TreeItem,
            UIA.ControlType.DataItem,
            UIA.ControlType.Slider,
            UIA.ControlType.Spinner,
            UIA.ControlType.Document,
            UIA.ControlType.Text,
            UIA.ControlType.Image,
            UIA.ControlType.Pane,
            UIA.ControlType.ToolBar,
            UIA.ControlType.Custom
        };

        var typeConditions = controlTypes
            .Select(t => new UIA.PropertyCondition(UIA.AutomationElement.ControlTypeProperty, t))
            .Cast<UIA.Condition>()
            .ToArray();

        return new UIA.OrCondition(typeConditions);
    }

    /// <summary>
    /// 在截图上为每个控件画编号（移植自 UFO 的 annotation）。
    /// 控件边框和编号背景按控件类型颜色编码，数字文字保持白色。
    /// 如果 lastActionControlId 不为 null，在该控件位置画粗红色边框标记上一步操作。
    /// </summary>
    public static void Annotate(string screenshotPath, IReadOnlyList<AnnotatedControl> controls, string outputPath, int? lastActionControlId = null)
    {
        using var bmp = new Bitmap(screenshotPath);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var font = new Font("Arial", 11, FontStyle.Bold);
        // 数字编号文字保持白色
        using var textFg = new SolidBrush(Color.White);

        foreach (var c in controls)
        {
            var color = GetControlColor(c.TypeName);
            using var pen = new Pen(Color.FromArgb(220, color.R, color.G, color.B), 2);
            using var labelBg = new SolidBrush(Color.FromArgb(230, color.R, color.G, color.B));

            // 画边框
            g.DrawRectangle(pen, c.Left, c.Top, Math.Max(1, c.Width - 1), Math.Max(1, c.Height - 1));
            // 编号标签画在控件左上角
            var label = c.Id.ToString();
            var size = g.MeasureString(label, font);
            var lx = c.Left;
            var ly = c.Top - size.Height;
            if (ly < 0) ly = c.Top;
            g.FillRectangle(labelBg, lx, ly, size.Width + 4, size.Height);
            g.DrawString(label, font, textFg, lx + 2, ly);
        }

        // 上一步操作控件：粗红色边框标记
        if (lastActionControlId.HasValue)
        {
            var last = controls.FirstOrDefault(x => x.Id == lastActionControlId.Value);
            if (last is not null)
            {
                using var thickPen = new Pen(Color.FromArgb(255, 244, 67, 54), 4);
                var pad = 3;
                g.DrawRectangle(thickPen,
                    last.Left - pad, last.Top - pad,
                    Math.Max(1, last.Width + pad * 2 - 1),
                    Math.Max(1, last.Height + pad * 2 - 1));
            }
        }

        bmp.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>按控件类型返回标注颜色。</summary>
    private static Color GetControlColor(string typeName)
    {
        return typeName switch
        {
            "Button" => ColorTranslator.FromHtml("#2196F3"),       // 蓝色
            "Edit" => ColorTranslator.FromHtml("#4CAF50"),          // 绿色
            "ListItem" or "TreeItem" => ColorTranslator.FromHtml("#FF9800"), // 橙色
            "Hyperlink" => ColorTranslator.FromHtml("#9C27B0"),     // 紫色
            "ComboBox" => ColorTranslator.FromHtml("#00BCD4"),      // 青色
            "Menu" or "MenuItem" => ColorTranslator.FromHtml("#795548"), // 棕色
            _ => ColorTranslator.FromHtml("#F44336")                // 红色（默认）
        };
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

/// <summary>一个带编号和坐标的可交互控件。</summary>
public sealed record AnnotatedControl(int Id, string Name, string TypeName, int Left, int Top, int Width, int Height, UIA.AutomationElement? Element = null)
{
    /// <summary>控件中心点（用于点击）。</summary>
    public (int X, int Y) Center => (Left + Width / 2, Top + Height / 2);

    /// <summary>
    /// 尝试用 UIA 读取控件的文本值。依次尝试 ValuePattern、TextPattern，
    /// 最后回退到 AutomationElement.Current.Name。
    /// </summary>
    public string? GetText()
    {
        if (Element is null) return null;

        // ValuePattern（输入框、文档等可编辑控件的文本）
        try
        {
            if (Element.GetCurrentPattern(UIA.ValuePattern.Pattern) is UIA.ValuePattern vp)
            {
                var val = vp.Current.Value;
                if (!string.IsNullOrEmpty(val)) return val;
            }
        }
        catch { /* 控件不支持 ValuePattern，继续尝试下一个 */ }

        // TextPattern（文档、富文本控件）
        try
        {
            if (Element.GetCurrentPattern(UIA.TextPattern.Pattern) is UIA.TextPattern tp)
            {
                var val = tp.DocumentRange.GetText(-1);
                if (!string.IsNullOrEmpty(val)) return val;
            }
        }
        catch { /* 控件不支持 TextPattern */ }

        // 回退到 Name 属性
        try { return Element.Current.Name; } catch { return null; }
    }
}
