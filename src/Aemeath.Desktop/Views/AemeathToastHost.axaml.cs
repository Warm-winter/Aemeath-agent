using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Aemeath.Desktop.Views;

internal enum AemeathToastKind
{
    Success,
    Error
}

internal partial class AemeathToastHost : UserControl
{
    internal static readonly TimeSpan SuccessDuration = TimeSpan.FromSeconds(2.5);
    internal static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(4.5);

    private readonly DispatcherTimer _dismissTimer;
    private readonly DispatcherTimer _collapseTimer;
    private bool _reduceMotion;

    public AemeathToastHost()
    {
        InitializeComponent();
        _dismissTimer = new DispatcherTimer();
        _dismissTimer.Tick += (_, _) => BeginDismiss();
        _collapseTimer = new DispatcherTimer();
        _collapseTimer.Tick += (_, _) => CompleteDismiss();
    }

    internal AemeathToastKind CurrentKind { get; private set; }
    internal TimeSpan CurrentDuration => _dismissTimer.Interval;
    internal bool UsesReducedMotion => _reduceMotion;

    internal void ShowToast(
        AemeathToastKind kind,
        string message,
        bool reduceMotion,
        TimeSpan? durationOverride = null)
    {
        _dismissTimer.Stop();
        _collapseTimer.Stop();
        _reduceMotion = reduceMotion;
        CurrentKind = kind;

        var isError = kind == AemeathToastKind.Error;
        ToastCard.Classes.Set("success", !isError);
        ToastCard.Classes.Set("error", isError);
        ToastIconSurface.Classes.Set("success", !isError);
        ToastIconSurface.Classes.Set("error", isError);
        SuccessGlyph.IsVisible = !isError;
        ErrorGlyph.IsVisible = isError;
        ToastTitle.Text = isError ? "\u64cd\u4f5c\u5931\u8d25" : "\u64cd\u4f5c\u5b8c\u6210";
        ToastMessage.Text = message;
        AutomationProperties.SetLiveSetting(
            ToastMessage,
            isError ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);

        IsVisible = true;
        ToastCard.Opacity = 0;
        ToastCard.RenderTransform = reduceMotion
            ? TransformOperations.Identity
            : TransformOperations.Parse("translate(0px, 6px)");

        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible)
            {
                return;
            }

            ToastCard.Opacity = 1;
            ToastCard.RenderTransform = TransformOperations.Identity;
        }, DispatcherPriority.Render);

        _dismissTimer.Interval = durationOverride ?? (isError ? ErrorDuration : SuccessDuration);
        _dismissTimer.Start();
    }

    private void BeginDismiss()
    {
        _dismissTimer.Stop();
        ToastCard.Opacity = 0;
        if (!_reduceMotion)
        {
            ToastCard.RenderTransform = TransformOperations.Parse("translate(0px, -4px)");
        }

        _collapseTimer.Interval = _reduceMotion
            ? TimeSpan.FromMilliseconds(100)
            : TimeSpan.FromMilliseconds(160);
        _collapseTimer.Start();
    }

    private void CompleteDismiss()
    {
        _collapseTimer.Stop();
        IsVisible = false;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _dismissTimer.Stop();
        _collapseTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
