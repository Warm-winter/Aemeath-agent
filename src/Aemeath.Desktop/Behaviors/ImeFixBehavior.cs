using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Aemeath.Desktop.Behaviors;

/// <summary>
/// 在非 IME composition 场景下兜底刷新 TextBox 视觉光标位置。
///
/// 策略：
/// 1. Composition 期间零干预——Avalonia 12.0.4 原生 TSF 负责光标渲染。
///    之前的版本在 composition 期间调用 MoveCaretToTextPosition + 三重 Invalidate*
///    与 TSF 布局竞态，导致光标乱跳与文本视觉缺失。
/// 2. 仅在 TextInput（composition 提交后）、GotFocus、PointerPressed 三个时机
///    通过 Dispatcher.UIThread.Post 延迟一帧调用 RefreshCaretVisual，用当前
///    CaretIndex 刷新视觉。
/// 3. 不再使用 IMM32 P/Invoke 检测 composition 状态——这是不可靠的，且会与
///    Avalonia 内部状态产生竞态。
/// </summary>
public static class ImeFixBehavior
{
    public static readonly AttachedProperty<bool> EnableImeCursorFixProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("EnableImeCursorFix", typeof(ImeFixBehavior));

    static ImeFixBehavior()
    {
        EnableImeCursorFixProperty.Changed.AddClassHandler<TextBox>(OnEnableImeCursorFixChanged);
    }

    public static void SetEnableImeCursorFix(TextBox element, bool value) => element.SetValue(EnableImeCursorFixProperty, value);

    public static bool GetEnableImeCursorFix(TextBox element) => element.GetValue(EnableImeCursorFixProperty);

    private static void OnEnableImeCursorFixChanged(TextBox textBox, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            textBox.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
            // 鼠标点击改变 CaretIndex 后，Avalonia 的 TextPresenter._caretBounds 可能不自动刷新，
            // 显式监听 PointerPressed 事件，在点击后强制刷新光标视觉位置。
            textBox.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            textBox.GotFocus += OnGotFocus;
            return;
        }

        textBox.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        textBox.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        textBox.GotFocus -= OnGotFocus;
    }

    /// <summary>
    /// TextInput fires when text is actually committed (direct typing or IME commit).
    /// Safe moment to refresh the visual caret position.
    /// </summary>
    private static void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is not TextBox tb || !tb.IsFocused)
        {
            return;
        }

        ScheduleCaretRefresh(tb);
    }

    /// <summary>
    /// When the TextBox gains focus, do a one-time caret visual refresh.
    /// </summary>
    private static void OnGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb)
        {
            return;
        }

        ScheduleCaretRefresh(tb);
    }

    /// <summary>
    /// 鼠标点击（左键/右键）在 TextBox 内改变 CaretIndex 后，
    /// Avalonia 的 TextPresenter._caretBounds 可能不自动更新。
    /// 延迟一帧刷新，确保 Avalonia 内部 PointerPressed 处理完毕后再校正光标视觉。
    /// </summary>
    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBox tb || !tb.IsFocused)
        {
            return;
        }

        ScheduleCaretRefresh(tb);
    }

    /// <summary>
    /// Schedule a caret visual refresh with a small delay to ensure
    /// the TextBox has finished its internal layout pass.
    /// </summary>
    private static void ScheduleCaretRefresh(TextBox tb)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!tb.IsFocused)
            {
                return;
            }

            RefreshCaretVisual(tb);
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// Forces the TextBox to re-render its caret at the current CaretIndex position.
    /// 用了两条路径，互为补充：
    /// 1) 取到 TextBox 内部的 TextPresenter，调用其 MoveCaretToTextPosition——根因修复
    ///    （composition 期间 TextPresenter 的 _caretBounds 不会随 CaretIndex 自动更新）。
    /// 2) InvalidateMeasure/Arrange/Visual 触发布局+渲染重算。
    /// 不再重设 CaretIndex——重设会触发级联属性变更通知，导致光标闪烁或跳位。
    /// </summary>
    private static void RefreshCaretVisual(TextBox textBox)
    {
        var idx = textBox.CaretIndex;

        var presenter = FindTextPresenter(textBox);
        if (presenter is not null)
        {
            try
            {
                // 让 presenter 按 CaretIndex 重新计算 caret 位置并重绘竖线。
                presenter.MoveCaretToTextPosition(idx);
            }
            catch
            {
                // MoveCaretToTextPosition 不可用时，退到重绘
            }
            presenter.InvalidateMeasure();
            presenter.InvalidateArrange();
            presenter.InvalidateVisual();
        }

        textBox.InvalidateMeasure();
        textBox.InvalidateArrange();
        textBox.InvalidateVisual();
    }

    /// <summary>从 TextBox 的可视化子树里找到内部的 TextPresenter。</summary>
    private static TextPresenter? FindTextPresenter(TextBox textBox)
    {
        return textBox.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
    }
}
