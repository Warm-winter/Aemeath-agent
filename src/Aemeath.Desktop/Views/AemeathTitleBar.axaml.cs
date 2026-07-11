using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Aemeath.Desktop.Services;

namespace Aemeath.Desktop.Views;

internal partial class AemeathTitleBar : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<AemeathTitleBar, string>(nameof(Title), "\u7231\u5f25\u65af\u52a9\u624b");

    public static readonly StyledProperty<bool> ShowMinimizeButtonProperty =
        AvaloniaProperty.Register<AemeathTitleBar, bool>(nameof(ShowMinimizeButton), true);

    public static readonly StyledProperty<bool> ShowMaximizeButtonProperty =
        AvaloniaProperty.Register<AemeathTitleBar, bool>(nameof(ShowMaximizeButton), true);

    public static readonly StyledProperty<bool> ShowCloseButtonProperty =
        AvaloniaProperty.Register<AemeathTitleBar, bool>(nameof(ShowCloseButton), true);

    private Window? _hostWindow;

    public AemeathTitleBar()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool ShowMinimizeButton
    {
        get => GetValue(ShowMinimizeButtonProperty);
        set => SetValue(ShowMinimizeButtonProperty, value);
    }

    public bool ShowMaximizeButton
    {
        get => GetValue(ShowMaximizeButtonProperty);
        set => SetValue(ShowMaximizeButtonProperty, value);
    }

    public bool ShowCloseButton
    {
        get => GetValue(ShowCloseButtonProperty);
        set => SetValue(ShowCloseButtonProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachWindow(TopLevel.GetTopLevel(this) as Window);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        AttachWindow(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachWindow(Window? window)
    {
        if (ReferenceEquals(_hostWindow, window))
        {
            return;
        }

        if (_hostWindow is not null)
        {
            _hostWindow.PropertyChanged -= HostWindow_OnPropertyChanged;
            _hostWindow.Opened -= HostWindow_OnOpened;
            WindowsWindowFrame.Detach(_hostWindow);
        }

        _hostWindow = window;
        if (_hostWindow is not null)
        {
            _hostWindow.PropertyChanged += HostWindow_OnPropertyChanged;
            _hostWindow.Opened += HostWindow_OnOpened;
            WindowsWindowFrame.Attach(_hostWindow);
        }

        UpdateWindowStateVisuals();
    }

    private void HostWindow_OnOpened(object? sender, EventArgs e)
    {
        if (_hostWindow is not null)
        {
            WindowsWindowFrame.Attach(_hostWindow);
            WindowsWindowFrame.Refresh(_hostWindow);
        }
    }

    private void HostWindow_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            if (_hostWindow is not null)
            {
                WindowsWindowFrame.Refresh(_hostWindow);
            }
            UpdateWindowStateVisuals();
        }
    }

    private void DragRegion_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_hostWindow is null || e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2 && ShowMaximizeButton && _hostWindow.CanResize)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        _hostWindow.BeginMoveDrag(e);
        e.Handled = true;
    }

    private void MinimizeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null)
        {
            _hostWindow.WindowState = WindowState.Minimized;
        }
    }

    private void MaximizeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        if (_hostWindow is null || !_hostWindow.CanResize)
        {
            return;
        }

        _hostWindow.WindowState = _hostWindow.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _hostWindow?.Close();
    }

    private void UpdateWindowStateVisuals()
    {
        if (MaximizeButton is null || MaximizeGlyph is null || RestoreGlyph is null)
        {
            return;
        }

        var isMaximized = _hostWindow?.WindowState == WindowState.Maximized;
        MaximizeGlyph.IsVisible = !isMaximized;
        RestoreGlyph.IsVisible = isMaximized;
        AutomationProperties.SetName(MaximizeButton, isMaximized ? "\u8fd8\u539f\u7a97\u53e3" : "\u6700\u5927\u5316\u7a97\u53e3");
        ToolTip.SetTip(MaximizeButton, isMaximized ? "\u8fd8\u539f" : "\u6700\u5927\u5316");
    }
}
