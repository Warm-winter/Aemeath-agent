using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Aemeath.Core.AI;
using Aemeath.Core.Configuration;
using Aemeath.Desktop.Services;
using Aemeath.Desktop.Views;
using Aemeath.Pet;
using Avalonia.Threading;

namespace Aemeath.Desktop;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private SettingsService? _settingsService;
    private IChatService? _chatService;
    private ChatWindow? _chatWindow;
    private ConfigWindow? _configWindow;
    private PetWindow? _petWindow;
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
            _chatWindow.ActivityChanged += OnChatActivityChanged;
            _chatWindow.Closed += (closedSender, _) =>
            {
                if (closedSender is ChatWindow window)
                {
                    window.ActivityChanged -= OnChatActivityChanged;
                }
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
            _chatWindow.ActivityChanged += OnChatActivityChanged;
            _chatWindow.Closed += (closedSender, _) =>
            {
                if (closedSender is ChatWindow window)
                {
                    window.ActivityChanged -= OnChatActivityChanged;
                }
                _chatWindow = null;
            };
            _chatWindow.Show();
        }
    }

    private void OnChatActivityChanged(object? sender, ChatActivityChangedEventArgs e)
    {
        if (_petWindow is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_petWindow is null)
            {
                return;
            }

            switch (e.Kind)
            {
                case ChatActivityKind.Sending:
                    _petWindow.SetActivityState(PetState.Running, "小爱正在执行任务。");
                    break;
                case ChatActivityKind.VoiceListening:
                    _petWindow.SetActivityState(PetState.Waiting, "小爱正在聆听。");
                    break;
                case ChatActivityKind.ToolWaiting:
                    _petWindow.SetActivityState(PetState.Waiting, "有高风险操作需要确认。");
                    break;
                case ChatActivityKind.Completed:
                    _petWindow.SetActivityState(null);
                    _ = _petWindow.PlayTemporaryStateAsync(PetState.Review, TimeSpan.FromSeconds(1.4), "任务反馈完成。");
                    break;
                case ChatActivityKind.Failed:
                    _petWindow.SetActivityState(null);
                    _ = _petWindow.PlayTemporaryStateAsync(PetState.Failed, TimeSpan.FromSeconds(1.6), "信号异常，小爱需要再试一次。");
                    break;
                default:
                    _petWindow.SetActivityState(null);
                    break;
            }
        }, DispatcherPriority.Background);
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

}
