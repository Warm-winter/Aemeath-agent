using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Runtime.InteropServices;

namespace Aemeath.Desktop.Behaviors;

/// <summary>
/// Refreshes the TextBox visual after IME text commits without taking over
/// caret navigation. Arrow keys, Home/End and selection remain native Avalonia
/// behavior so the drawn caret stays aligned with the real edit position.
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
            textBox.GotFocus += OnGotFocus;
            return;
        }

        textBox.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        textBox.GotFocus -= OnGotFocus;
    }

    private static void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is not TextBox textBox || !textBox.IsFocused || IsImeComposing())
        {
            return;
        }

        ScheduleVisualRefresh(textBox);
    }

    private static void OnGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            ScheduleVisualRefresh(textBox);
        }
    }

    private static void ScheduleVisualRefresh(TextBox textBox)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (textBox.IsFocused)
            {
                textBox.InvalidateVisual();
            }
        }, DispatcherPriority.Render);
    }

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
                return ImmGetCompositionString(hImc, GCS_COMPSTR, IntPtr.Zero, 0) > 0;
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
