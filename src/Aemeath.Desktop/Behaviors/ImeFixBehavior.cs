using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;

namespace Aemeath.Desktop.Behaviors;

/// <summary>
/// Fixes the visual caret position not updating correctly during and after
/// IME composition in Avalonia TextBox on Windows.
///
/// Strategy:
/// 1. On TextInput (text commit) — refresh caret visual position.
/// 2. On KeyUp for arrow keys — if NOT in IME composition, refresh caret.
///    This handles the case where the user presses arrow keys after committing
///    IME text but the caret visual doesn't update.
/// 3. Uses Win32 IMM32 APIs to detect whether IME composition is active,
///    so we never interfere while the user is still composing pinyin.
/// </summary>
public static class ImeFixBehavior
{
    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hImc);

    [DllImport("imm32.dll")]
    private static extern int ImmGetCompositionString(IntPtr hImc, uint dwIndex, IntPtr lpBuf, uint dwBufLen);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    private const uint GCS_COMPSTR = 0x0008;

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
            textBox.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
            textBox.GotFocus += OnGotFocus;
            // composition 期间方向键常被 IME 拦截，KeyUp 收不到；改监听 CaretIndex 变化兜底。
            textBox.PropertyChanged += OnPropertyChanged;
            return;
        }

        textBox.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        textBox.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
        textBox.GotFocus -= OnGotFocus;
        textBox.PropertyChanged -= OnPropertyChanged;
    }

    /// <summary>CaretIndex 变化时，若处于 IME composition 期间，强制刷新竖线位置。</summary>
    private static void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not TextBox tb || !tb.IsFocused)
        {
            return;
        }

        if (e.Property != TextBox.CaretIndexProperty)
        {
            return;
        }

        // 仅在 composition 期间介入；纯文本输入时 Avalonia 原生 caret 正常，不干预。
        if (IsImeComposing())
        {
            RefreshCaretVisual(tb);
        }
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
    /// KeyUp fires after a key is released. We check:
    /// 1. Is it an arrow key (Left/Right/Home/End)?
    /// 2. Is IME currently NOT composing?
    /// If both true, refresh the caret visual — this fixes the case where
    /// arrow keys after IME commit don't update the visual caret position.
    ///
    /// 注意：输入法组合（候选框打开）期间必须「不干预」。
    /// 早期版本（提交 7b25014）曾尝试在组合期间也强制刷新光标，结果反而破坏了 Avalonia
    /// 原生的光标跟随行为——表现为候选框打开时按方向键，显示光标卡住不动。
    /// 这里恢复首版行为：组合期间直接 return，把光标渲染交还给 Avalonia 自己处理。
    /// </summary>
    private static void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || !tb.IsFocused)
        {
            return;
        }

        // Only handle navigation keys that move the caret
        if (e.Key != Key.Left && e.Key != Key.Right &&
            e.Key != Key.Home && e.Key != Key.End &&
            e.Key != Key.Up && e.Key != Key.Down)
        {
            return;
        }

        // If IME is currently composing, don't interfere
        if (IsImeComposing())
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
    /// 1) 取到 TextBox 内部的 TextPresenter，调用其 caret 定位/重绘方法——这是根因修复
    ///    （composition 期间 TextPresenter 的 _caretBounds 不会随 CaretIndex 自动更新）。
    /// 2) 退路：InvalidateMeasure/Arrange/Visual + 重设 CaretIndex（旧逻辑，部分场景仍有效）。
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
            presenter.InvalidateVisual();
        }

        // 退路：强制布局 + 重设 CaretIndex，兼容旧路径
        textBox.InvalidateMeasure();
        textBox.InvalidateArrange();
        textBox.InvalidateVisual();

        Dispatcher.UIThread.Post(() =>
        {
            if (textBox.IsFocused)
            {
                textBox.CaretIndex = idx;
            }
        }, DispatcherPriority.Render);
    }

    /// <summary>从 TextBox 的可视化子树里找到内部的 TextPresenter。</summary>
    private static TextPresenter? FindTextPresenter(TextBox textBox)
    {
        return textBox.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
    }

    /// <summary>
    /// Checks whether the IME is currently in composition mode using Win32 IMM32 APIs.
    /// Returns true if there is an active composition string (user is typing pinyin).
    /// </summary>
    private static bool IsImeComposing()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            var hwnd = GetFocus();
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var hImc = ImmGetContext(hwnd);
            if (hImc == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                // Get the length of the composition string
                // If > 0, IME is currently composing
                var len = ImmGetCompositionString(hImc, GCS_COMPSTR, IntPtr.Zero, 0);
                return len > 0;
            }
            finally
            {
                ImmReleaseContext(hwnd, hImc);
            }
        }
        catch
        {
            return false;
        }
    }
}
