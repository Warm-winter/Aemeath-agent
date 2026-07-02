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
    private const uint GCS_CURSORPOS = 0x0080;

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
            // 鼠标点击改变 CaretIndex 后，Avalonia 的 TextPresenter._caretBounds 可能不自动刷新，
            // 显式监听 PointerPressed 事件，在点击后强制刷新光标视觉位置。
            textBox.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            textBox.GotFocus += OnGotFocus;
            // composition 期间方向键常被 IME 拦截，KeyUp 收不到；改监听 CaretIndex 变化兜底。
            textBox.PropertyChanged += OnPropertyChanged;
            // composition 字符串变化时 TextChanged 更可靠（IME 可能吞键盘事件）。
            textBox.TextChanged += OnTextChanged;
            return;
        }

        textBox.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        textBox.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
        textBox.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        textBox.GotFocus -= OnGotFocus;
        textBox.PropertyChanged -= OnPropertyChanged;
        textBox.TextChanged -= OnTextChanged;
    }

    /// <summary>CaretIndex 变化时，强制刷新竖线位置。
    /// 不再检查 IsImeComposing()——鼠标点击改变 CaretIndex 时也需要刷新视觉光标。
    /// RefreshCaretVisualSafe 内部会自行判断是否处于 composition 状态。</summary>
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

        // 始终刷新——无论是否处于 composition 状态
        RefreshCaretVisualSafe(tb);
    }

    /// <summary>
    /// TextChanged 在 composition 字符串变化时触发（比 KeyUp 更可靠，IME 可能吞键盘事件）。
    /// 若处于 composition 期间，刷新光标视觉位置。
    /// </summary>
    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBox tb || !tb.IsFocused)
        {
            return;
        }

        if (IsImeComposing())
        {
            RefreshCaretVisualSafe(tb);
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
    /// composition 期间也需强力刷新光标——用户按方向键在候选窗口移动时，
    /// IME 内部光标位置已变但 Avalonia 的 TextPresenter._caretBounds 不会自动更新。
    /// 使用 RefreshCaretVisualSafe：调用 MoveCaretToTextPosition + InvalidateMeasure
    /// 触发布局重算，但**不重设 CaretIndex**，避免破坏 TSF composition 逻辑。
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

        if (IsImeComposing())
        {
            // composition 期间：强力刷新光标位置。
            // 用 GCS_CURSORPOS 获取 IME 内部光标偏移，计算绝对文本位置，
            // 调用 MoveCaretToTextPosition 强制重算 _caretBounds。
            RefreshCaretVisualSafe(tb);
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
    /// 强力刷新光标视觉位置，但不重设 CaretIndex（composition 期间安全）。
    /// 
    /// 核心逻辑：
    /// 1. 用 IMM32 GCS_CURSORPOS 获取 IME 内部光标在 composition 字符串中的偏移
    /// 2. 计算绝对文本位置 = CaretIndex - composition字符串长度 + 光标偏移
    ///    （CaretIndex 通常指向 composition 字符串末尾）
    /// 3. 调用 MoveCaretToTextPosition 强制重算 TextPresenter._caretBounds
    /// 4. InvalidateMeasure/Arrange/Visual 触发布局+渲染
    /// 
    /// 不重设 CaretIndex——composition 期间重设会与 TSF 内部状态冲突。
    /// </summary>
    private static void RefreshCaretVisualSafe(TextBox textBox)
    {
        var presenter = FindTextPresenter(textBox);
        if (presenter is not null)
        {
            try
            {
                // 尝试用 composition 光标位置（比 CaretIndex 更准确）
                var targetPos = ResolveCompositionCursorPos(textBox);
                presenter.MoveCaretToTextPosition(targetPos);
            }
            catch
            {
                // MoveCaretToTextPosition 失败时退到 InvalidateMeasure
            }
            presenter.InvalidateMeasure();
            presenter.InvalidateArrange();
            presenter.InvalidateVisual();
        }

        textBox.InvalidateMeasure();
        textBox.InvalidateArrange();
        textBox.InvalidateVisual();
        // 不重设 CaretIndex——composition 期间重设会与 TSF 冲突
    }

    /// <summary>
    /// 计算 composition 期间光标在文本中的绝对位置。
    /// CaretIndex 通常指向 composition 字符串末尾，
    /// GCS_CURSORPOS 返回光标在 composition 字符串中的偏移，
    /// 绝对位置 = CaretIndex - composition字符串长度 + GCS_CURSORPOS。
    /// 若无法获取 composition 信息，回退到 CaretIndex。
    /// </summary>
    private static int ResolveCompositionCursorPos(TextBox textBox)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return textBox.CaretIndex;
        }

        try
        {
            var hwnd = GetFocus();
            if (hwnd == IntPtr.Zero) return textBox.CaretIndex;

            var hImc = ImmGetContext(hwnd);
            if (hImc == IntPtr.Zero) return textBox.CaretIndex;

            try
            {
                // composition 字符串长度（字节数，ASCII 拼音 = 字符数）
                var compLen = ImmGetCompositionString(hImc, GCS_COMPSTR, IntPtr.Zero, 0);
                if (compLen <= 0) return textBox.CaretIndex;

                // 光标在 composition 字符串中的偏移
                var compCursor = ImmGetCompositionString(hImc, GCS_CURSORPOS, IntPtr.Zero, 0);
                if (compCursor < 0) return textBox.CaretIndex;
                // 过渡态保护：IME 内部状态切换瞬间 compCursor 可能大于 compLen，
                // 此时用陈旧的 composition 数据计算位置会导致光标跳到错误位置。
                if (compCursor > compLen) return textBox.CaretIndex;

                // 绝对位置 = CaretIndex（末尾） - compLen + compCursor
                var absPos = textBox.CaretIndex - compLen + compCursor;
                // 安全 clamp
                var maxLen = textBox.Text?.Length ?? 0;
                if (absPos < 0) absPos = 0;
                if (absPos > maxLen) absPos = maxLen;
                return absPos;
            }
            finally
            {
                ImmReleaseContext(hwnd, hImc);
            }
        }
        catch
        {
            return textBox.CaretIndex;
        }
    }

    /// <summary>
    /// Forces the TextBox to re-render its caret at the current CaretIndex position.
    /// 用了两条路径，互为补充：
    /// 1) 取到 TextBox 内部的 TextPresenter，调用其 MoveCaretToTextPosition——根因修复
    ///    （composition 期间 TextPresenter 的 _caretBounds 不会随 CaretIndex 自动更新）。
    /// 2) InvalidateMeasure/Arrange/Visual 触发布局+渲染重算。
    /// 不再重设 CaretIndex——重设会触发 cascading OnPropertyChanged，导致光标闪烁或跳位。
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
