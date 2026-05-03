using Avalonia;
using System;
using Aemeath.Desktop.Services;

namespace Aemeath.Desktop;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLogger.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLogger.Error("unhandled", "AppDomain unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLogger.Error("unobserved", "Task unobserved exception", e.Exception);
            e.SetObserved();
        };

        try
        {
            AppLogger.Info("program", "desktop lifetime start");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLogger.Error("program", "fatal startup exception", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
