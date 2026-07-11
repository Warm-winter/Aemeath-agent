using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Aemeath.Desktop.Views;

namespace Aemeath.Desktop.Tests;

public sealed class AemeathToastHostTests
{
    [AvaloniaFact]
    public void ShowToast_UsesThemeStateDurationsAndLiveAnnouncements()
    {
        var host = new AemeathToastHost();
        var window = new Window { Content = host };
        window.Show();
        try
        {
            host.ShowToast(AemeathToastKind.Success, "\u914d\u7f6e\u5df2\u4fdd\u5b58", reduceMotion: false);
            Dispatcher.UIThread.RunJobs();

            var card = host.FindControl<Border>("ToastCard")!;
            var message = host.FindControl<TextBlock>("ToastMessage")!;
            Assert.True(host.IsVisible);
            Assert.Equal(AemeathToastKind.Success, host.CurrentKind);
            Assert.Equal(AemeathToastHost.SuccessDuration, host.CurrentDuration);
            Assert.Contains("success", card.Classes);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(message));

            host.ShowToast(AemeathToastKind.Error, "\u4fdd\u5b58\u5931\u8d25", reduceMotion: true);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(AemeathToastKind.Error, host.CurrentKind);
            Assert.Equal(AemeathToastHost.ErrorDuration, host.CurrentDuration);
            Assert.True(host.UsesReducedMotion);
            Assert.Contains("error", card.Classes);
            Assert.DoesNotContain("success", card.Classes);
            Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(message));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ShowToast_NewMessageReplacesTimerAndEventuallyCollapses()
    {
        var host = new AemeathToastHost();
        var window = new Window { Content = host };
        window.Show();
        try
        {
            host.ShowToast(
                AemeathToastKind.Success,
                "first",
                reduceMotion: true,
                durationOverride: TimeSpan.FromMilliseconds(20));
            host.ShowToast(
                AemeathToastKind.Error,
                "second",
                reduceMotion: true,
                durationOverride: TimeSpan.FromMilliseconds(40));

            Assert.Equal("second", host.FindControl<TextBlock>("ToastMessage")!.Text);
            await Task.Delay(180);
            Dispatcher.UIThread.RunJobs();

            Assert.False(host.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
