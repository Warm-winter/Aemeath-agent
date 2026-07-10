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
            AppLogger.Info("app", "settings loaded");
            _chatService = new AemiChatService(_settingsService);
            if (_chatService is AemiChatService aemiService)
            {
                aemiService.SetUiThreadInvoker(action => Dispatcher.UIThread.Post(action, DispatcherPriority.Normal));
                aemiService.ReminderTriggered += OnReminderTriggered;
            }
            AppLogger.Info("app", "chat service created");

            _petWindow = CreatePetWindow();
            AppLogger.Info("app", "pet window created");
            desktop.MainWindow = _petWindow;
            AppLogger.Info("app", "main window assigned");
            AppLogger.Info("app", "framework initialized");
            BridgeAssetDeployer.DeployUfoRunner();
            StartMcpBackgroundReload();
        }

        base.OnFrameworkInitializationCompleted();
    }


    private PetWindow CreatePetWindow()
    {
        if (_settingsService is null)
        {
            throw new InvalidOperationException("Settings service is not ready.");
        }

        var window = new PetWindow(
            _settingsService.Current,
            OpenChatWindow,
            OpenConfigWindow,
            _settingsService,
            ExitApplication);
        window.Closing += OnPetWindowClosing;
        return window;
    }

    private void StartMcpBackgroundReload()
    {
        if (_chatService is not AemiChatService aemiChatService)
        {
            return;
        }

        aemiChatService.McpStatusChanged -= OnMcpStatusChanged;
        aemiChatService.McpStatusChanged += OnMcpStatusChanged;
        AppLogger.Info("mcp", "mcp background reload started");
        _ = Task.Run(async () =>
        {
            try
            {
                await aemiChatService.ReloadMcpToolsAsync();
                AppLogger.Info("mcp", "mcp background reload completed: " + aemiChatService.McpStatus);
            }
            catch (Exception ex)
            {
                AppLogger.Error("mcp", "mcp background reload failed", ex);
            }
        });
    }

    private void OnMcpStatusChanged(object? sender, string status)
    {
        AppLogger.Info("mcp", status);
    }

    private void OnReminderTriggered(object? sender, string message)
    {
        // ReminderPlugin 的 Timer.Elapsed 在 ThreadPool 线程触发，需切到 UI 线程操作桌宠
        AppLogger.Info("reminder", $"reminder triggered: {message}");
        Dispatcher.UIThread.Post(() =>
        {
            if (_petWindow is null)
            {
                return;
            }

            _ = _petWindow.PlayTemporaryStateAsync(
                PetState.Review,
                TimeSpan.FromSeconds(4),
                $"⏰ 提醒：{message}");
        }, DispatcherPriority.Normal);
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
                case ChatActivityKind.Canceled:
                    _petWindow.SetActivityState(null);
                    _ = _petWindow.PlayTemporaryStateAsync(PetState.Waiting, TimeSpan.FromSeconds(1), "已经停下来了。");
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

    /// <summary>供聊天栏「打开 MCP 配置」跳转：打开/激活设置窗口并切到 MCP 配置 Tab。</summary>
    public void OpenConfigFromUi()
    {
        OpenConfigWindow();
    }

    public void OpenConfigAtMcpTab()
    {
        OpenConfigWindow();
        _configWindow?.SelectMcpTab();
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

    private void OnTrayOpenChatClick(object? sender, EventArgs e)
    {
        try
        {
            OpenChatWindow();
        }
        catch (Exception ex)
        {
            AppLogger.Error("tray", "failed to open chat from tray", ex);
        }
    }

    private void OnTrayOpenConfigClick(object? sender, EventArgs e)
    {
        try
        {
            OpenConfigWindow();
        }
        catch (Exception ex)
        {
            AppLogger.Error("tray", "failed to open config from tray", ex);
        }
    }

    private void OnTrayShowPetClick(object? sender, EventArgs e)
    {
        try
        {
            if (_petWindow is null)
            {
                _petWindow = CreatePetWindow();
                if (_desktop is not null)
                {
                    _desktop.MainWindow = _petWindow;
                }
                AppLogger.Info("pet", "pet window recreated from tray");
            }

            _petWindow.Show();
            _petWindow.Activate();
            AppLogger.Info("pet", "pet window shown from tray");
        }
        catch (Exception ex)
        {
            AppLogger.Error("tray", "failed to show pet from tray", ex);
        }
    }

    private void OnTrayHidePetClick(object? sender, EventArgs e)
    {
        try
        {
            _petWindow?.Hide();
            AppLogger.Info("pet", "pet window hidden from tray");
        }
        catch (Exception ex)
        {
            AppLogger.Error("tray", "failed to hide pet from tray", ex);
        }
    }

    private void OnTrayExitClick(object? sender, EventArgs e)
    {
        try
        {
            ExitApplication();
        }
        catch (Exception ex)
        {
            AppLogger.Error("tray", "failed to exit from tray", ex);
        }
    }
    private void ExitApplication()
    {
        AppLogger.Info("app", "exit requested");
        _isExiting = true;

        CloseWindow(_chatWindow, "chat");
        _chatWindow = null;

        CloseWindow(_configWindow, "config");
        _configWindow = null;

        CloseWindow(_petWindow, "pet");
        _petWindow = null;

        // 释放 AI/MCP 运行时资源（HttpClient、MCP 子进程等）后再关闭应用（RES-002）。
        if (_chatService is AemiChatService aemiChatService)
        {
            try
            {
                aemiChatService.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                AppLogger.Error("app", "chat service dispose failed", ex);
            }
        }

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


