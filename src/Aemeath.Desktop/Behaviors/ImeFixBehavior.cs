using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
            return;
        }

        textBox.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        textBox.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
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
    /// KeyUp fires after a key is released. We check:
    /// 1. Is it an arrow key (Left/Right/Home/End)?
    /// If true, refresh the caret visual regardless of IME state.
    /// This fixes the case where arrow keys don't update the visual caret position,
    /// even during IME composition.
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

        // Always refresh caret visual for arrow keys, even during IME composition
        ScheduleCaretRefresh(tb);
    }

    /// <summary>
    /// When the TextBox gains focus, do a one-time caret visual refresh.
    /// </summary>
    private static void OnGotFocus(object? sender, GotFocusEventArgs e)
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
    /// Uses a two-phase approach: first invalidate layout, then re-set CaretIndex
    /// after the layout pass completes.
    /// </summary>
    private static void RefreshCaretVisual(TextBox textBox)
    {
        var idx = textBox.CaretIndex;

        // Force layout recalculation which triggers caret position update
        textBox.InvalidateMeasure();
        textBox.InvalidateArrange();
        textBox.InvalidateVisual();

        // Re-apply caret index after layout to force the caret adorner to update
        Dispatcher.UIThread.Post(() =>
        {
            if (textBox.IsFocused)
            {
                textBox.CaretIndex = idx;
            }
        }, DispatcherPriority.Render);
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
