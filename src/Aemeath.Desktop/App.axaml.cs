using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Aemeath.Core.AI;
using Aemeath.Core.Configuration;
using Aemeath.Desktop.Services;
using Aemeath.Desktop.Views;
using Aemeath.Pet;
using Aemeath.Speech;
using Avalonia.Threading;
using System.Threading;
using System.Threading.Tasks;

namespace Aemeath.Desktop;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private SettingsService? _settingsService;
    private IChatService? _chatService;
    private ChatWindow? _chatWindow;
    private ConfigWindow? _configWindow;
    private PetWindow? _petWindow;
    private WakeWordService? _wakeWordService;
    private readonly SemaphoreSlim _wakeWordSemaphore = new(1, 1);
    private bool _isExiting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _settingsService = new SettingsService();
            _chatService = new AemiChatService(_settingsService);
            AppLogger.Info("app", "framework initialized");

            _petWindow = new PetWindow(
                _settingsService.Current,
                OpenChatWindow,
                OpenConfigWindow,
                _settingsService);

            _wakeWordService = new WakeWordService();
            _wakeWordService.WakeWordDetected += OnWakeWordDetected;
            _ = RestartWakeWordServiceAsync();

            _petWindow.Closing += OnPetWindowClosing;
            desktop.MainWindow = _petWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OpenChatWindow()
    {
        if (_chatService is null || _settingsService is null)
        {
            return;
        }

        if (_chatWindow is null)
        {
            _chatWindow = new ChatWindow(_chatService, _settingsService);
            _chatWindow.Closed += (_, _) =>
            {
                AppLogger.Info("chat", "chat window closed");
                _chatWindow = null;
            };
            _chatWindow.Show();
            AppLogger.Info("chat", "chat window created and shown");
            return;
        }

        try
        {
            if (!_chatWindow.IsVisible)
            {
                _chatWindow.Show();
                AppLogger.Info("chat", "chat window shown from hidden");
                return;
            }

            if (_chatWindow.WindowState == WindowState.Minimized)
            {
                _chatWindow.WindowState = WindowState.Normal;
                _chatWindow.Show();
            }

            _chatWindow.Activate();
            AppLogger.Info("chat", "chat window activated");
        }
        catch (Exception ex)
        {
            AppLogger.Error("chat", "failed to show existing chat window, recreating", ex);
            _chatWindow = new ChatWindow(_chatService, _settingsService);
            _chatWindow.Closed += (_, _) => _chatWindow = null;
            _chatWindow.Show();
        }
    }

    private void OpenConfigWindow()
    {
        if (_settingsService is null || _chatService is null)
        {
            return;
        }

        if (_configWindow is null || !_configWindow.IsVisible)
        {
            _configWindow = new ConfigWindow(
                _settingsService,
                _chatService,
                () => _ = RestartWakeWordServiceAsync(),
                () => _chatWindow?.CurrentSessionId);
            _configWindow.Closed += (_, _) =>
            {
                AppLogger.Info("config", "config window closed");
                _configWindow = null;
            };
            _configWindow.Show();
            return;
        }

        _configWindow.Activate();
    }

    private void OnPetWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        if (_settingsService?.Current.MinimizeToTray != true)
        {
            return;
        }

        if (sender is Window window)
        {
            e.Cancel = true;
            window.Hide();
        }
    }

    private void OnTrayShowPetClick(object? sender, EventArgs e)
    {
        if (_petWindow is null)
        {
            return;
        }

        _petWindow.Show();
        _petWindow.Activate();
    }

    private void OnTrayHidePetClick(object? sender, EventArgs e)
    {
        _petWindow?.Hide();
    }

    private void OnTrayExitClick(object? sender, EventArgs e)
    {
        AppLogger.Info("app", "tray exit requested");
        _isExiting = true;
        DisposeWakeWordService();

        CloseWindow(_chatWindow, "chat");
        _chatWindow = null;

        CloseWindow(_configWindow, "config");
        _configWindow = null;

        CloseWindow(_petWindow, "pet");
        _petWindow = null;

        _desktop?.Shutdown();
    }

    private static void CloseWindow(Window? window, string source)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            AppLogger.Error(source, "failed to close window during tray exit", ex);
        }
    }

    private async Task RestartWakeWordServiceAsync()
    {
        if (_settingsService is null || _wakeWordService is null)
        {
            return;
        }

        _wakeWordService.Stop();

        if (!_settingsService.Current.EnableWakeWord)
        {
            AppLogger.Info("wakeword", "wake word disabled");
            return;
        }

        var accessKey = _settingsService.Current.PicovoiceAccessKey;
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            AppLogger.Info("wakeword", "wake word not started because access key is missing");
            return;
        }

        await Task.Run(() =>
        {
            if (_wakeWordService.Start(accessKey))
            {
                AppLogger.Info("wakeword", "wake word listener started");
                return;
            }

            AppLogger.Error("wakeword", $"wake word listener failed: {_wakeWordService.LastError}");
        });
    }

    private void OnWakeWordDetected(object? sender, WakeWordDetectedEventArgs e)
    {
        AppLogger.Info("wakeword", $"wake word detected: {e.KeywordLabel}");
        Dispatcher.UIThread.Post(async () => await HandleWakeWordDetectedAsync(), DispatcherPriority.Background);
    }

    private async Task HandleWakeWordDetectedAsync()
    {
        if (!await _wakeWordSemaphore.WaitAsync(0))
        {
            return;
        }

        try
        {
            _wakeWordService?.Stop();
            OpenChatWindow();

            if (_chatWindow is not null)
            {
                await _chatWindow.HandleWakeWordAsync();
            }
        }
        finally
        {
            await RestartWakeWordServiceAsync();
            _wakeWordSemaphore.Release();
        }
    }

    private void DisposeWakeWordService()
    {
        if (_wakeWordService is null)
        {
            return;
        }

        _wakeWordService.WakeWordDetected -= OnWakeWordDetected;
        _wakeWordService.Dispose();
        _wakeWordService = null;
    }
}
