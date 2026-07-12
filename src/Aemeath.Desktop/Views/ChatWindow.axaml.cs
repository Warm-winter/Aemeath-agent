using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Aemeath.Core.AI;
using Aemeath.Core.Configuration;
using Aemeath.Core.MCP;
using Aemeath.Core.Memory;
using Aemeath.Core.Tools;
using Aemeath.Desktop.Services;
using Aemeath.Pet.Effects;
using Aemeath.Speech;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AvaloniaImage = Avalonia.Controls.Image;

namespace Aemeath.Desktop.Views;

public partial class ChatWindow : Window
{
    private readonly IChatService _chatService;
    private readonly SettingsService _settingsService;
    private readonly ChatSessionStore _sessionStore;
    private readonly MemoryOrchestrator _memoryOrchestrator;
    private readonly ParticleEffect _particleEffect;
    private readonly ToolConfirmationService? _toolConfirmationService;
    private readonly AttachmentService _attachments = new();
    private readonly AttachmentThumbnailCache _attachmentThumbnailCache;
    private CancellationTokenSource _attachmentRenderCts = new();
    private readonly DispatcherTimer _pendingTimer;
    private readonly DispatcherTimer _flickerTimer;
    private readonly DispatcherTimer _statusHideTimer;
    private readonly string[] _pendingFrames = ["星点同步中", "星点同步中.", "星点同步中..", "星点同步中..."];

    private int _pendingFrameIndex;
    private TextBlock? _pendingTextBlock;
    private bool _isSending;
    private bool _scrollPending;
    private bool _isUpdatingSessionList;
    private bool _userNearBottom = true;
    private CancellationTokenSource? _sendCancellationTokenSource;
    private readonly ChatInteractionStateMachine _interactionState = new();
    private const double AutoScrollThreshold = 96;
    private double _flickerPhase;

    private readonly Bitmap _assistantAvatar;
    private readonly Bitmap _maleAvatar;
    private readonly Bitmap _femaleAvatar;
    private Bitmap? _customUserAvatar;
    private string? _customUserAvatarPath;
    private Bitmap? _chatBackgroundBitmap;
    private string? _appliedChatBackgroundPath;
    private readonly DrawingImage _copyIcon;
    private readonly DrawingImage _deleteIcon;
    private readonly DrawingImage _retryIcon;
    private readonly DrawingImage _micIcon;
    private readonly DrawingImage _keyboardIcon;
    private readonly DrawingImage _uploadIcon;
    private readonly DrawingImage _wrenchIcon;
    private readonly DrawingImage _imageIcon;
    private readonly DrawingImage _fileIcon;
    private readonly McpServerStore _mcpServerStore = new();
    private readonly List<ChatMessageRecord> _displayMessages = [];
    private readonly Dictionary<string, PendingToolAction> _pendingToolActions = new(StringComparer.OrdinalIgnoreCase);
    // 确认卡片控件：actionId → 渲染出的 Border。确认/取消时从面板移除该卡片。
    private readonly Dictionary<string, Border> _pendingActionCards = new(StringComparer.OrdinalIgnoreCase);
    // 长任务（电脑控制）确认后，在结果到达前显示的占位气泡：actionId → _displayMessages 索引
    private readonly Dictionary<string, int> _runningActionPlaceholders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan MinimumVoiceCaptureDuration = TimeSpan.FromMilliseconds(500);
    private readonly SemaphoreSlim _voiceCaptureLock = new(1, 1);
    private readonly SemaphoreSlim _providerSwitchLock = new(1, 1);
    private volatile bool _confirmationCreatedDuringGeneration;
    private bool _isLoadingProviderSwitch;
    private SpeechService? _holdSpeechService;
    private Task? _voiceCaptureStartTask;
    private DateTimeOffset? _voiceCaptureStartedAt;
    private bool _voiceHolding;
    private bool _isVoiceMode;

    private string _currentSessionId = string.Empty;

    public string CurrentSessionId => _currentSessionId;

    public event EventHandler<ChatActivityChangedEventArgs>? ActivityChanged;

    public ChatWindow() : this(new NoOpChatService(), new SettingsService(), null, null)
    {
    }

    public ChatWindow(IChatService chatService, SettingsService settingsService)
        : this(chatService, settingsService, null, null)
    {
    }

    internal ChatWindow(
        IChatService chatService,
        SettingsService settingsService,
        ChatSessionStore? sessionStore,
        AttachmentThumbnailCache? attachmentThumbnailCache)
    {
        InitializeComponent();
        AppLogger.Info("chat", "chat window constructor start");
        _chatService = chatService;
        _settingsService = settingsService;
        _settingsService.ProvidersChanged += OnProvidersChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
        Closed += (_, _) =>
        {
            _settingsService.ProvidersChanged -= OnProvidersChanged;
            _settingsService.SettingsChanged -= OnSettingsChanged;
        };
        _sessionStore = sessionStore ?? new ChatSessionStore();
        _attachmentThumbnailCache = attachmentThumbnailCache ?? new AttachmentThumbnailCache();
        // Mem0 记忆编排器：每轮 add + 发送前 search 注入。config/python 由 AemiChatService 提供。
        if (chatService is AemiChatService aemiChatSvc)
        {
            _memoryOrchestrator = new MemoryOrchestrator(
                () => aemiChatSvc.BuildMem0Config(),
                () => _settingsService.Current.Mem0PythonPath);
            _memoryOrchestrator.Diagnostics = (level, msg, ex) =>
            {
                if (ex is null) AppLogger.Info("memory", $"[{level}] {msg}");
                else AppLogger.Error("memory", msg, ex);
            };
        }
        else
        {
            _memoryOrchestrator = new MemoryOrchestrator(() => null, () => null);
        }
        _particleEffect = new ParticleEffect(BackgroundParticleCanvas);
        if (_chatService is AemiChatService aemiChatService)
        {
            _toolConfirmationService = aemiChatService.ToolConfirmationService;
            _toolConfirmationService.PendingActionCreated += OnPendingToolActionCreated;
            _toolConfirmationService.PendingActionCompleted += OnPendingToolActionCompleted;
        }

        _assistantAvatar = AemiUi.LoadBitmap("avares://Aemeath-agent/Assets/static/xiaoai-avatar.png");
        _maleAvatar = AemiUi.LoadBitmap("avares://Aemeath-agent/Assets/static/user-male.png");
        _femaleAvatar = AemiUi.LoadBitmap("avares://Aemeath-agent/Assets/static/user-female.png");
        // Copy icon (two overlapping rectangles)
        _copyIcon = AemiUi.CreateVectorIcon(
            "M4 2 C2.895 2 2 2.895 2 4 L2 14 L4 14 L4 4 L14 4 L14 2 Z " +
            "M8 6 C6.895 6 6 6.895 6 8 L6 18 C6 19.105 6.895 20 8 20 L18 20 C19.105 20 20 19.105 20 18 L20 8 C20 6.895 19.105 6 18 6 Z " +
            "M8 8 L18 8 L18 18 L8 18 Z",
            22, 22);
        // Delete / trash icon
        _deleteIcon = AemiUi.CreateVectorIcon(
            "M9 3 L9 4 L4 4 L4 6 L5 6 L5 20 C5 21.105 5.895 22 7 22 L17 22 C18.105 22 19 21.105 19 20 L19 6 L20 6 L20 4 L15 4 L15 3 Z " +
            "M7 6 L17 6 L17 20 L7 20 Z " +
            "M9 8 L9 18 L11 18 L11 8 Z " +
            "M13 8 L13 18 L15 18 L15 8 Z",
            24, 24, AemiUi.Error);
        // Retry / refresh icon (circular arrow)
        _retryIcon = AemiUi.CreateVectorIcon(
            "M17.65 6.35 C16.2 4.9 14.21 4 12 4 C7.58 4 4.01 7.58 4.01 12 C4.01 16.42 7.58 20 12 20 C15.73 20 18.84 17.45 19.73 14 L17.65 14 " +
            "C16.83 16.33 14.61 18 12 18 C8.69 18 6 15.31 6 12 C6 8.69 8.69 6 12 6 C13.66 6 15.14 6.69 16.22 7.78 L13 11 L20 11 L20 4 Z",
            24, 24);
        // Microphone icon
        _micIcon = AemiUi.CreateVectorIcon(
            "M12 14 C13.66 14 14.99 12.66 14.99 11 L15 5 C15 3.34 13.66 2 12 2 C10.34 2 9 3.34 9 5 L9 11 C9 12.66 10.34 14 12 14 Z " +
            "M17.3 11 C17.3 14 14.76 16.1 12 16.1 C9.24 16.1 6.7 14 6.7 11 L5 11 C5 14.41 7.72 17.23 11 17.72 L11 21 L13 21 L13 17.72 " +
            "C16.28 17.23 19 14.41 19 11 Z",
            24, 24);
        // Keyboard icon
        _keyboardIcon = AemiUi.CreateVectorIcon(
            "M2 6 C2 4.9 2.9 4 4 4 L20 4 C21.1 4 22 4.9 22 6 L22 18 C22 19.1 21.1 20 20 20 L4 20 C2.9 20 2 19.1 2 18 Z " +
            "M4 6 L4 18 L20 18 L20 6 Z " +
            "M5 8 L7 8 L7 10 L5 10 Z M8 8 L10 8 L10 10 L8 10 Z M11 8 L13 8 L13 10 L11 10 Z M14 8 L16 8 L16 10 L14 10 Z M17 8 L19 8 L19 10 L17 10 Z " +
            "M5 11 L7 11 L7 13 L5 13 Z M8 11 L10 11 L10 13 L8 13 Z M11 11 L13 11 L13 13 L11 13 Z M14 11 L16 11 L16 13 L14 13 Z M17 11 L19 11 L19 13 L17 13 Z " +
            "M7 14 L17 14 L17 16 L7 16 Z",
            24, 24);
        _uploadIcon = AemiUi.CreateVectorIcon(
            "M3830 5115 l0 -955 -965 0 -965 0 0 -85 0 -85 965 0 965 0 0 -965 0 -965 85 0 85 0 2 963 3 962 958 3 957 2 0 85 0 85 -960 0 -960 0 0 955 0 955 -85 0 -85 0 0 -955z",
            800, 800);
        _wrenchIcon = AemiUi.CreateSvgTransformedVectorIcon(
            "M4495 6174 c-530 -89 -947 -486 -1061 -1010 -12 -55 -17 -126 -17 -234 -1 -174 15 -278 63 -413 l29 -82 -222 -225 c-122 -124 -427 -432 -677 -685 -660 -665 -684 -692 -725 -775 -48 -102 -61 -187 -42 -288 20 -104 65 -192 139 -268 166 -173 426 -204 628 -74 44 28 570 551 1333 1326 l277 281 93 -27 c129 -39 264 -54 416 -47 333 14 620 139 853 372 192 191 304 399 353 657 21 108 21 326 1 440 -17 90 -58 234 -82 280 -17 34 -62 58 -106 58 -29 0 -65 -32 -375 -342 l-342 -341 -103 22 c-57 12 -152 32 -212 44 l-109 22 -38 160 c-21 88 -41 178 -44 200 -7 46 -47 1 442 496 230 233 243 248 243 283 0 54 -15 75 -73 100 -170 74 -442 104 -642 70z",
            800, 800);
        _imageIcon = AemiUi.CreateVectorIcon(
            "M1953 5440 c-12 -5 -26 -18 -32 -29 -8 -14 -11 -424 -11 -1339 l0 -1319 23 -21 23 -22 1960 0 1961 0 21 23 22 23 0 1321 c0 1286 -1 1321 -19 1344 l-19 24 -1954 2 c-1113 1 -1962 -2 -1975 -7z m3795 -1362 l-3 -1193 -1830 0 -1830 0 -3 161 -2 161 87 79 c91 82 365 327 395 351 17 15 37 0 473 -355 82 -67 152 -121 157 -120 6 3 584 506 1051 917 l138 122 117 -108 c158 -145 962 -857 970 -860 4 -2 36 24 71 55 l63 58 -73 66 c-41 36 -265 236 -499 444 -234 208 -474 422 -533 476 -60 54 -112 98 -116 98 -4 0 -34 -24 -67 -53 -101 -92 -1106 -971 -1120 -982 -10 -7 -22 -1 -46 21 -26 24 -285 238 -549 452 l-27 22 -119 -108 c-65 -60 -174 -158 -243 -219 l-125 -111 -3 897 c-1 493 0 902 3 909 3 9 380 12 1835 12 l1830 0 -2 -1192z",
            800, 800);
        _fileIcon = AemiUi.CreateVectorIcon(
            "M2425 5901 c-61 -27 -111 -89 -125 -152 -8 -40 -10 -507 -8 -1784 l3 -1730 28 -47 c18 -31 44 -57 75 -75 l47 -28 1555 0 c1505 0 1556 1 1593 19 44 22 83 69 103 123 11 31 13 295 14 1438 l0 1400 -428 428 -427 427 -1195 0 c-1134 0 -1197 -1 -1235 -19z m2265 -468 c0 -156 5 -294 10 -314 17 -61 52 -102 109 -131 l55 -28 313 0 313 0 0 -1335 0 -1335 -1490 0 -1490 0 0 1710 0 1710 1090 0 1090 0 0 -277z m413 -64 c103 -100 187 -185 187 -190 0 -5 -84 -9 -190 -9 l-189 0 -3 190 c-2 105 0 190 3 190 3 0 90 -81 192 -181z",
            800, 800);
        UploadButton.Content = new AvaloniaImage { Source = _uploadIcon, Width = 18, Height = 18, Stretch = Stretch.Uniform };
        McpToolsButton.Content = new AvaloniaImage { Source = _wrenchIcon, Width = 18, Height = 18, Stretch = Stretch.Uniform };
        VoiceButton.Content = new AvaloniaImage { Source = _micIcon, Width = 18, Height = 18, Stretch = Stretch.Uniform };
        ToolTip.SetTip(UploadButton, "选择上传图片或文件");
        ToolTip.SetTip(McpToolsButton, "快速开启或关闭 MCP 服务");
        ToolTip.SetTip(VoiceButton, "\u5207\u6362\u8bed\u97f3/\u952e\u76d8\u8f93\u5165");
        ToolTip.SetTip(SendButton, "\u53d1\u9001\u5230\u5c0f\u7231\u7ec8\u7aef");
        ToolTip.SetTip(NewSessionButton, "\u5f00\u542f\u65b0\u7684\u901a\u8baf\u6863\u6848");
        ToolTip.SetTip(DeleteSessionButton, "\u5220\u9664\u5f53\u524d\u901a\u8baf\u6863\u6848");

        _pendingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        _pendingTimer.Tick += (_, _) =>
        {
            if (_pendingTextBlock is null)
            {
                return;
            }

            _pendingTextBlock.Text = _pendingFrames[_pendingFrameIndex % _pendingFrames.Length];
            _pendingFrameIndex++;
        };

        _flickerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _flickerTimer.Tick += (_, _) =>
        {
            _flickerPhase += 0.06;
            var t = 0.975 + 0.025 * Math.Sin(_flickerPhase);
            BackgroundContainer.Opacity = t;
            var glow = 0.8 + 0.2 * (0.5 + 0.5 * Math.Sin(_flickerPhase * 0.7));
            GlowLayerPink.Opacity = glow;
            GlowLayerBlue.Opacity = glow;
            GlowLayerWhite.Opacity = glow;
        };

        _statusHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusHideTimer.Tick += (_, _) =>
        {
            _statusHideTimer.Stop();
            ProviderSwitchStatusBorder.IsVisible = false;
        };

        SendButton.Click += async (_, _) =>
        {
            if (_isSending)
            {
                CancelCurrentSend();
                return;
            }
            await SendAsync();
        };
        UploadButton.Click += (_, _) => ShowUploadMenu();
        McpToolsButton.Click += (_, _) => ShowMcpToolsMenu();
        VoiceButton.Click += (_, _) => ToggleVoiceMode();
        VoiceRecordButton.AddHandler(PointerPressedEvent, VoiceRecordButton_OnPointerPressed, RoutingStrategies.Tunnel, true);
        VoiceRecordButton.AddHandler(PointerReleasedEvent, VoiceRecordButton_OnPointerReleased, RoutingStrategies.Tunnel, true);
        VoiceRecordButton.AddHandler(PointerCaptureLostEvent, VoiceRecordButton_OnPointerCaptureLost, RoutingStrategies.Tunnel, true);

        ToggleSidebarButton.Click += (_, _) => ToggleChatSidebar();
        CloseSidebarButton.Click += (_, _) => ToggleChatSidebar(forceOpen: false);
        OpenSettingsButton.Click += (_, _) => OpenSettings();
        NewSessionButton.Click += (_, _) =>
        {
            if (CanNavigateSessions()) StartNewSession();
        };
        RenameSessionButton.Click += async (_, _) => await RenameCurrentSessionAsync();
        DeleteSessionButton.Click += async (_, _) => await DeleteCurrentSessionAsync();
        SessionSearchBox.TextChanged += (_, _) => RefreshSessionSelector(_currentSessionId);
        SessionListBox.SelectionChanged += (_, _) =>
        {
            if (_isUpdatingSessionList || !CanNavigateSessions())
            {
                return;
            }

            if (SessionListBox.SelectedItem is ListBoxItem { Tag: string id } &&
                !string.IsNullOrWhiteSpace(id) &&
                !string.Equals(id, _currentSessionId, StringComparison.Ordinal))
            {
                LoadSession(id);
                if (ChatSplitView.DisplayMode == SplitViewDisplayMode.Overlay)
                {
                    ToggleChatSidebar(forceOpen: false);
                }
            }
        };

        JumpToLatestButton.Click += (_, _) => ScrollToBottom(force: true);
        ChatScrollViewer.ScrollChanged += (_, _) => UpdateScrollProximity();
        PromptIntroButton.Click += (_, _) => SetPromptText("介绍一下你自己吧。");
        PromptPlanButton.Click += (_, _) => SetPromptText("帮我整理一下今天的计划。");
        PromptImageButton.Click += async (_, _) => await PickAttachmentsAsync(imagesOnly: true);
        EmptyStateActionButton.Click += (_, _) => HandleEmptyStateAction();

        ProviderQuickSwitchBox.SelectionChanged += async (_, _) => await SwitchQuickProviderAsync();
        ModelQuickSwitchBox.SelectionChanged += async (_, _) => await SwitchQuickModelAsync();

        InputBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                var current = InputBox.Text ?? string.Empty;
                var caret = InputBox.CaretIndex;
                InputBox.Text = current.Insert(caret, Environment.NewLine);
                InputBox.CaretIndex = caret + Environment.NewLine.Length;
                e.Handled = true;
                return;
            }

            e.Handled = true;
            await SendAsync();
        };

        KeyDown += ChatWindow_OnKeyDown;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                UpdateAmbientAnimationState();
            }
        };

        Opened += (_, _) =>
        {
            AppLogger.Info("chat", "chat window opened");
            ApplyChatBackgroundImage();
            UpdateAmbientAnimationState();

            ChatSplitView.IsPaneOpen = _settingsService.Current.IsChatSidebarOpen;
            LoadLatestSessionOrCreateIfEmpty();
            RefreshProviderQuickSwitch();
            UpdateResponsiveLayout();
            UpdateEmptyState();
            SetUiState(ChatUiState.Idle);
        };
    }

    private async void ChatWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Escape)
            {
                if (_isSending)
                {
                    CancelCurrentSend();
                    e.Handled = true;
                }
                else if (_isVoiceMode)
                {
                    ToggleVoiceMode();
                    e.Handled = true;
                }
                else if (ChatSplitView.DisplayMode == SplitViewDisplayMode.Overlay && ChatSplitView.IsPaneOpen)
                {
                    ToggleChatSidebar(forceOpen: false);
                    e.Handled = true;
                }
            }
            return;
        }

        switch (e.Key)
        {
            case Key.N:
                if (CanNavigateSessions()) StartNewSession();
                e.Handled = true;
                break;
            case Key.O:
                await PickAttachmentsAsync(imagesOnly: false);
                e.Handled = true;
                break;
            case Key.OemComma:
                OpenSettings();
                e.Handled = true;
                break;
        }
    }

    private void ToggleChatSidebar(bool? forceOpen = null)
    {
        ChatSplitView.IsPaneOpen = forceOpen ?? !ChatSplitView.IsPaneOpen;
        _settingsService.Current.IsChatSidebarOpen = ChatSplitView.IsPaneOpen;
        _settingsService.Save();
        CloseSidebarButton.IsVisible = ChatSplitView.IsPaneOpen;
    }

    private void UpdateResponsiveLayout()
    {
        var narrow = Bounds.Width > 0 && Bounds.Width < 900;
        ChatSplitView.DisplayMode = narrow ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        CloseSidebarButton.IsVisible = ChatSplitView.IsPaneOpen;
    }

    private void OpenSettings()
    {
        if (Avalonia.Application.Current is App app)
        {
            app.OpenConfigFromUi();
        }
    }

    private void SetPromptText(string text)
    {
        if (_isVoiceMode) ToggleVoiceMode();
        InputBox.Text = text;
        InputBox.CaretIndex = text.Length;
        InputBox.Focus();
    }

    private void HandleEmptyStateAction()
    {
        if (_settingsService.ListProviders().Count == 0)
        {
            OpenSettings();
            return;
        }
        InputBox.Focus();
    }

    private void CancelCurrentSend()
    {
        if (_sendCancellationTokenSource is null || _sendCancellationTokenSource.IsCancellationRequested) return;
        SendButton.IsEnabled = false;
        SendButton.Content = "正在停止…";
        _sendCancellationTokenSource.Cancel();
    }

    private bool CanNavigateSessions()
        => !_isSending && !_voiceHolding && _pendingToolActions.Count == 0;

    private async Task RenameCurrentSessionAsync()
    {
        if (!CanNavigateSessions() || string.IsNullOrWhiteSpace(_currentSessionId)) return;
        var session = _sessionStore.GetSession(_currentSessionId);
        if (session is null) return;

        var title = await DialogService.PromptAsync(this, "重命名对话", "输入一个便于查找的名称。", session.Title, "保存名称");
        if (title is null) return;
        if (_sessionStore.RenameSession(_currentSessionId, title))
        {
            RefreshSessionSelector(_currentSessionId);
            ShowStatusMessage("对话名称已更新。");
        }
    }

    private async Task DeleteCurrentSessionAsync()
    {
        if (!CanNavigateSessions() || string.IsNullOrWhiteSpace(_currentSessionId)) return;
        var session = _sessionStore.GetSession(_currentSessionId);
        var title = session?.Title ?? "当前对话";
        if (!await DialogService.ConfirmAsync(this, "删除对话", $"确定删除「{title}」吗？相关会话记忆也会一并清理。", "删除对话")) return;

        var deletedId = _currentSessionId;
        _sessionStore.DeleteSession(deletedId);
        _ = Task.Run(() => _memoryOrchestrator.DeleteAllAsync(Mem0Scope.ForSession(deletedId)));
        LoadLatestSessionOrEmpty();
        ShowStatusMessage("对话已删除。");
    }

    private void SetUiState(ChatUiState state)
    {
        _interactionState.TransitionTo(state);
        var streaming = _interactionState.IsStreaming;
        var locked = _interactionState.IsInteractionLocked;
        var hasProvider = _settingsService.ListProviders().Count > 0;

        SendButton.IsEnabled = streaming || hasProvider;
        SendButton.Content = streaming ? "停止" : "发送";
        SendButton.Classes.Remove("primary");
        SendButton.Classes.Remove("danger");
        SendButton.Classes.Add(streaming ? "danger" : "primary");
        InputBox.IsEnabled = !locked && hasProvider;
        VoiceButton.IsEnabled = !locked && hasProvider;
        VoiceRecordButton.IsEnabled = !streaming && state != ChatUiState.WaitingConfirmation;
        NewSessionButton.IsEnabled = !locked;
        RenameSessionButton.IsEnabled = !locked && !string.IsNullOrWhiteSpace(_currentSessionId);
        DeleteSessionButton.IsEnabled = !locked && !string.IsNullOrWhiteSpace(_currentSessionId);
        SessionListBox.IsEnabled = !locked;
        SessionSearchBox.IsEnabled = !locked;
        OpenSettingsButton.IsEnabled = !streaming;
        UpdateProviderQuickSwitchEnabled();
    }

    private void UpdateEmptyState()
    {
        var isEmpty = _displayMessages.Count == 0 && _pendingToolActions.Count == 0;
        EmptyStatePanel.IsVisible = isEmpty;
        if (!isEmpty) return;

        var hasProvider = _settingsService.ListProviders().Count > 0;
        EmptyStateTitle.Text = hasProvider ? "听得到吗？" : "先接通信号吧";
        EmptyStateText.Text = hasProvider
            ? "今天想聊什么？也可以把图片或文件交给我。"
            : "还没有可用的 AI 服务。完成一次 Provider 配置后，我们就能开始聊天。";
        EmptyStateActionButton.Content = hasProvider ? "开始对话" : "前往设置";
        PromptIntroButton.IsVisible = hasProvider;
        PromptPlanButton.IsVisible = hasProvider;
        PromptImageButton.IsVisible = hasProvider;
    }

    private void UpdateScrollProximity()
    {
        var remaining = ChatScrollViewer.Extent.Height - ChatScrollViewer.Viewport.Height - ChatScrollViewer.Offset.Y;
        _userNearBottom = remaining <= AutoScrollThreshold;
        JumpToLatestButton.IsVisible = !_userNearBottom && _displayMessages.Count > 0;
    }
    private async Task SendAsync()
    {
        if (_isSending)
        {
            return;
        }

        var input = InputBox.Text?.Trim() ?? string.Empty;
        var attachments = _attachments.Snapshot();
        if (string.IsNullOrWhiteSpace(input) && attachments.Count == 0)
        {
            return;
        }

        await SendUserTurnAsync(input, attachments, clearInputBox: true);
    }

    /// <summary>
    /// \u7edf\u4e00\u7684\u7528\u6237\u6d88\u606f\u53d1\u9001\u4e3b\u6d41\u7a0b\uff08\u6587\u672c\u53d1\u9001 / \u8bed\u97f3\u6587\u672c\u53d1\u9001\u5171\u7528\uff09\u3002
    /// \u91cd\u65b0\u751f\u6210\u56de\u590d\uff08GenerateAssistantReplyForUserAsync\uff09\u56e0\u4e0a\u4e0b\u6587\u6765\u6e90\u4e0e\u6301\u4e45\u5316\u65b9\u5f0f\u4e0d\u540c\uff0c\u672a\u5408\u5165\u6b64\u5904\u3002
    /// </summary>
    private async Task SendUserTurnAsync(string text, IReadOnlyList<ChatAttachment>? attachments, bool clearInputBox)
    {
        if (_isSending || string.IsNullOrWhiteSpace(text) && (attachments is null || attachments.Count == 0))
        {
            return;
        }

        var visibleUserText = text.Trim();
        var modelInput = string.IsNullOrWhiteSpace(visibleUserText)
            ? "\u8bf7\u5206\u6790\u6211\u4e0a\u4f20\u7684\u9644\u4ef6\u3002"
            : visibleUserText;
        var attachmentList = attachments?.Select(attachment => attachment with { }).ToList()
            ?? new List<ChatAttachment>();

        _isSending = true;
        _sendCancellationTokenSource?.Dispose();
        _sendCancellationTokenSource = new CancellationTokenSource();
        SetUiState(ChatUiState.Streaming);
        RaiseActivityChanged(ChatActivityKind.Sending);
        UpdateProviderQuickSwitchEnabled();
        TextBlock? pending = null;
        _confirmationCreatedDuringGeneration = false;
        try
        {
            AppLogger.Info("chat", "send start");
            PauseAmbientFlicker();
            EnsureCurrentSession();

            // 先取历史（不含当前这一条），再持久化当前消息，避免当前消息在历史里重复出现一次。
            var recent = _sessionStore.GetRecentMessages(_currentSessionId, 40);
            AppLogger.Info("chat", $"[诊断] 输入文本: {modelInput.Substring(0, Math.Min(100, modelInput.Length))}... | recent 消息数: {recent.Count}");
            if (recent.Count > 0)
            {
                var lastMsg = recent[recent.Count - 1];
                AppLogger.Info("chat", $"[诊断] recent 最后一条: Role={lastMsg.Role}, Content={lastMsg.Content.Substring(0, Math.Min(80, lastMsg.Content.Length))}...");
            }

            var userMessage = new ChatMessageRecord
            {
                Role = "user",
                Content = visibleUserText,
                Timestamp = DateTimeOffset.UtcNow,
                Attachments = attachmentList.Select(attachment => attachment with { }).ToList()
            };
            _displayMessages.Add(userMessage);
            AddMessageBubble(_displayMessages.Count - 1, isAssistant: false, visibleUserText, isPending: false);
            _sessionStore.AppendMessage(_currentSessionId, userMessage.Role, userMessage.Content, userMessage.Attachments);
            RefreshSessionSelector(_currentSessionId);
            if (clearInputBox)
            {
                InputBox.Text = string.Empty;
            }

            pending = AddMessageBubble(_displayMessages.Count, isAssistant: true, string.Empty, isPending: true);
            _pendingTextBlock = pending;
            _pendingFrameIndex = 0;
            _pendingTimer.Start();

            // 发送前：检索相关长期记忆（Mem0）。与旧的 BuildPromptBlock 不同，
            // 这里是按当前用户消息做向量检索，只注入相关片段。
            var memoryBlock = await _memoryOrchestrator.BuildRelevantMemoryBlockAsync(_currentSessionId, modelInput);
            var prompt = BuildPromptWithRecentContext(recent, modelInput, attachmentList, memoryBlock);
            AppLogger.Info("chat", $"[诊断] 构建的 Prompt 前 500 字符: {prompt.Substring(0, Math.Min(500, prompt.Length))}...");

            _chatService.ClearHistory();

            var reply = await StreamReplyIntoAsync(prompt, pending, attachmentList, _sendCancellationTokenSource.Token);
            var sanitizedReply = SanitizeAssistantOutput(reply);
            if (ShouldSuppressConfirmationReply(sanitizedReply))
            {
                RenderCurrentMessages();
                RefreshSessionSelector(_currentSessionId);
                RaiseActivityChanged(ChatActivityKind.ToolWaiting);
                AppLogger.Info("chat", "send paused for tool confirmation");
                return;
            }

            pending.Text = string.IsNullOrWhiteSpace(sanitizedReply) ? "(\u65e0\u56de\u590d)" : sanitizedReply;
            ScrollToBottom();

            var assistantReply = pending.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(assistantReply))
            {
                assistantReply = "(\u65e0\u56de\u590d)";
                pending.Text = assistantReply;
            }

            assistantReply = FormatToolResultForUser(assistantReply);
            pending.Text = assistantReply;
            _displayMessages.Add(new ChatMessageRecord { Role = "assistant", Content = assistantReply, Timestamp = DateTimeOffset.UtcNow });
            _sessionStore.AppendMessage(_currentSessionId, "assistant", assistantReply);
            RenderCurrentMessages();
            RefreshSessionSelector(_currentSessionId);
            // 把这一轮对话写入 Mem0（后台执行，不阻塞 UI）。Mem0 内部自动抽取事实，无需手动压缩。
            _ = Task.Run(() => _memoryOrchestrator.AddTurnAsync(_currentSessionId, modelInput, assistantReply));
            RaiseActivityChanged(ChatActivityKind.Completed);
            AppLogger.Info("chat", "send completed");
        }
        catch (OperationCanceledException)
        {
            var partial = pending?.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(partial) && !_pendingFrames.Contains(partial, StringComparer.Ordinal))
            {
                var canceledText = partial + "\n\n（已停止生成）";
                _displayMessages.Add(new ChatMessageRecord { Role = "assistant", Content = canceledText, Timestamp = DateTimeOffset.UtcNow });
                _sessionStore.AppendMessage(_currentSessionId, "assistant", canceledText);
            }
            RenderCurrentMessages();
            RaiseActivityChanged(ChatActivityKind.Canceled);
            AppLogger.Info("chat", "send canceled");
        }
        catch (Exception ex)
        {
            AppLogger.Error("chat", "send failed", ex);
            var errorText = $"\u6267\u884c\u5931\u8d25\uff1a{ex.Message}";
            if (pending is not null)
            {
                pending.Text = errorText;
            }

            _displayMessages.Add(new ChatMessageRecord { Role = "assistant", Content = errorText, Timestamp = DateTimeOffset.UtcNow });
            _sessionStore.AppendMessage(_currentSessionId, "assistant", errorText);
            RenderCurrentMessages();
            SetUiState(ChatUiState.Failed);
            RaiseActivityChanged(ChatActivityKind.Failed);
        }
        finally
        {
            _pendingTimer.Stop();
            _pendingTextBlock = null;
            _isSending = false;
            _sendCancellationTokenSource?.Dispose();
            _sendCancellationTokenSource = null;
            ResumeAmbientFlicker();
            SetUiState(_pendingToolActions.Count > 0 ? ChatUiState.WaitingConfirmation : ChatUiState.Idle);
            UpdateProviderQuickSwitchEnabled();
            ClearPendingAttachments();
            UpdateEmptyState();
        }
    }

    private async Task<string> StreamReplyIntoAsync(
        string prompt,
        TextBlock target,
        IReadOnlyList<ChatAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        var builder = new System.Text.StringBuilder();
        var visibleText = string.Empty;
        var lastFlush = DateTimeOffset.MinValue;
        var timerStopped = false;

        await foreach (var chunk in _chatService.SendMessageStreamingAsync(prompt, attachments, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(chunk))
            {
                continue;
            }

            builder.Append(chunk);
            var current = builder.ToString();
            if (ShouldSuppressConfirmationReply(current))
            {
                break;
            }

            var now = DateTimeOffset.UtcNow;
            if ((now - lastFlush).TotalMilliseconds < 60 && current.Length - visibleText.Length < 24)
            {
                continue;
            }

            if (!timerStopped)
            {
                _pendingTimer.Stop();
                timerStopped = true;
            }

            visibleText = SanitizeAssistantOutput(current);
            target.Text = visibleText;
            ScrollToBottom();
            lastFlush = now;
        }

        if (!timerStopped)
        {
            _pendingTimer.Stop();
        }

        var finalText = SanitizeAssistantOutput(builder.ToString());
        if (!string.Equals(target.Text, finalText, StringComparison.Ordinal))
        {
            target.Text = finalText;
            ScrollToBottom();
        }

        return builder.ToString();
    }
    private void InputBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        _ = box;
    }

    private async Task PickAttachmentsAsync(bool imagesOnly)
    {
        if (_isSending)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = imagesOnly ? "选择要发送给小爱的图片" : "选择要发送给小爱的文件",
            AllowMultiple = true,
            FileTypeFilter = AttachmentService.BuildFileTypes(imagesOnly)
        });
        if (files.Count == 0)
        {
            return;
        }

        var notices = new List<string>();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                notices.Add("\u65e0\u6cd5\u8bfb\u53d6\u8be5\u9644\u4ef6\u7684\u672c\u5730\u8def\u5f84\u3002");
                continue;
            }

            var error = _attachments.TryAdd(path);
            if (!string.IsNullOrWhiteSpace(error))
            {
                notices.Add(error);
            }
        }

        RenderAttachmentChips();
        var message = notices.Count > 0
            ? string.Join(" ", notices)
            : _attachments.Count > 0
                ? $"\u5df2\u9644\u52a0 {_attachments.Count} \u4e2a\u6587\u4ef6\u3002"
                : string.Empty;

        if (!string.IsNullOrWhiteSpace(message))
        {
            ShowStatusMessage(message);
        }
    }


    private void ShowUploadMenu()
    {
        if (_isSending)
        {
            return;
        }

        var menu = new ContextMenu();
        var imageItem = new MenuItem
        {
            Header = "上传图片",
            Icon = new AvaloniaImage { Source = _imageIcon, Width = 16, Height = 16, Stretch = Stretch.Uniform }
        };
        imageItem.Click += async (_, _) => await PickAttachmentsAsync(imagesOnly: true);

        var fileItem = new MenuItem
        {
            Header = "上传文件",
            Icon = new AvaloniaImage { Source = _fileIcon, Width = 16, Height = 16, Stretch = Stretch.Uniform }
        };
        fileItem.Click += async (_, _) => await PickAttachmentsAsync(imagesOnly: false);

        menu.Items.Add(imageItem);
        menu.Items.Add(fileItem);
        menu.Open(UploadButton);
    }

    private void ShowMcpToolsMenu()
    {
        if (_isSending || _voiceHolding || _pendingToolActions.Count > 0)
        {
            return;
        }

        var menu = new ContextMenu();
        if (_chatService is AemiChatService aemiChatService)
        {
            menu.Items.Add(new MenuItem { Header = aemiChatService.McpStatus, IsEnabled = false });
            menu.Items.Add(new Separator());
        }

        // 受保护的内置服务（filesystem）在快速菜单里隐藏；
        // 已废弃的旧内置服务（memory——长期记忆改由 Mem0 提供）也隐藏，避免用户误删。
        // 与设置面板 McpConfigPanel 的过滤保持一致。
        var hiddenLegacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "memory" };
        var servers = _mcpServerStore.ListServers()
            .Where(s => !McpBuiltinRegistry.IsProtected(s.Id) && !hiddenLegacy.Contains(s.Id))
            .ToList();
        if (servers.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "暂无 MCP 服务", IsEnabled = false });
        }
        else
        {
            foreach (var server in servers)
            {
                var detail = string.IsNullOrWhiteSpace(server.LastError)
                    ? server.LastStatus ?? string.Empty
                    : server.LastError;
                var item = new MenuItem
                {
                    Header = $"{(server.Enabled ? "√" : "□")} {server.DisplayName} · {server.Transport.ToString().ToLowerInvariant()} · {BuildMcpShortStatus(server)}"
                };
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    ToolTip.SetTip(item, detail);
                }

                item.Click += (_, _) =>
                {
                    _mcpServerStore.SetEnabled(server.Id, !server.Enabled);
                    if (_chatService is AemiChatService service)
                    {
                        service.ReloadMcpTools();
                        ShowStatusMessage("MCP 工具正在后台刷新。");
                    }
                    else
                    {
                        ShowStatusMessage($"MCP 服务已{(!server.Enabled ? "开启" : "关闭")}：{server.DisplayName}");
                    }
                };
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        var configItem = new MenuItem { Header = "打开 MCP 配置（设置中心）" };
        configItem.Click += (_, _) =>
        {
            if (Avalonia.Application.Current is App app)
            {
                app.OpenConfigAtMcpTab();
            }
        };
        menu.Items.Add(configItem);
        menu.Open(McpToolsButton);
    }

    private static string BuildMcpShortStatus(McpServerConfig server)
    {
        if (!server.Enabled)
        {
            return "关闭";
        }

        if (string.Equals(server.LastStatus, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return "ok";
        }

        if (string.IsNullOrWhiteSpace(server.LastError))
        {
            return server.LastStatus ?? "未加载";
        }

        return server.LastError.Contains("超时", StringComparison.OrdinalIgnoreCase)
            ? "加载超时"
            : "连接失败";
    }
    private void RenderAttachmentChips()
    {
        AttachmentPanel.Children.Clear();
        AttachmentPanel.IsVisible = _attachments.Count > 0;

        foreach (var attachment in _attachments.Snapshot())
        {
            var removeButton = new Button
            {
                Content = "×",
                Width = 32,
                Height = 32,
                MinWidth = 32,
                MinHeight = 32,
                Padding = new Thickness(0),
                FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            removeButton.Classes.Add("ghost");
            ToolTip.SetTip(removeButton, "\u79fb\u9664\u9644\u4ef6");
            AutomationProperties.SetName(removeButton, $"\u79fb\u9664\u9644\u4ef6 {attachment.Name}");
            removeButton.Click += (_, _) =>
            {
                _attachments.Remove(attachment);
                RenderAttachmentChips();
            };

            var label = new TextBlock
            {
                Text = $"{AttachmentService.GetAttachmentKindLabel(attachment.Kind)} {attachment.Name} ({AttachmentService.FormatBytes(attachment.SizeBytes)})",
                Foreground = AemiUi.Brush(AemiUi.Ghost),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260,
                VerticalAlignment = VerticalAlignment.Center
            };

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children = { label, removeButton }
            };

            AttachmentPanel.Children.Add(new Border
            {
                Background = AemiUi.Brush(AemiUi.HaloSoft),
                BorderBrush = AemiUi.Brush(AemiUi.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(10, 5, 5, 5),
                Margin = new Thickness(0, 0, 8, 8),
                Child = panel
            });
        }
    }

    private void ClearPendingAttachments()
    {
        _attachments.Clear();
        RenderAttachmentChips();
    }

    private void ToggleVoiceMode()
    {
        _isVoiceMode = !_isVoiceMode;
        InputBox.IsVisible = !_isVoiceMode;
        VoiceRecordButton.IsVisible = _isVoiceMode;
        VoiceButton.Content = new AvaloniaImage
        {
            Source = _isVoiceMode ? _keyboardIcon : _micIcon,
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform
        };
    }

    private async void VoiceRecordButton_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            await _voiceCaptureLock.WaitAsync();
            try
            {
                if (_voiceHolding || _holdSpeechService is not null)
                {
                    return;
                }

                _voiceHolding = true;
                SetUiState(ChatUiState.VoiceListening);
                _voiceCaptureStartedAt = DateTimeOffset.UtcNow;
                e.Pointer.Capture(VoiceRecordButton);
                VoiceRecordButton.Content = "松开结束";
                RaiseActivityChanged(ChatActivityKind.VoiceListening);
                _holdSpeechService = new SpeechService();
                _voiceCaptureStartTask = _holdSpeechService.StartCaptureAsync();
            }
            finally
            {
                _voiceCaptureLock.Release();
            }

            if (_voiceCaptureStartTask is not null)
            {
                await _voiceCaptureStartTask;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("chat", "voice capture start failed", ex);
            await ResetVoiceCaptureStateAsync();
            SetUiState(ChatUiState.Failed);
            RaiseActivityChanged(ChatActivityKind.Failed);
        }
    }

    private async void VoiceRecordButton_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            e.Pointer.Capture(null);
            await StopVoiceCaptureAndSendAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("chat", "voice pointer release failed", ex);
            await ResetVoiceCaptureStateAsync();
            SetUiState(ChatUiState.Failed);
            RaiseActivityChanged(ChatActivityKind.Failed);
        }
    }

    private async void VoiceRecordButton_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        try
        {
            await StopVoiceCaptureAndSendAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("chat", "voice pointer capture lost failed", ex);
            await ResetVoiceCaptureStateAsync();
            SetUiState(ChatUiState.Failed);
            RaiseActivityChanged(ChatActivityKind.Failed);
        }
    }

    private async Task StopVoiceCaptureAndSendAsync()
    {
        SpeechService? speechService;
        Task? startTask;
        DateTimeOffset startedAt;

        await _voiceCaptureLock.WaitAsync();
        try
        {
            if (!_voiceHolding && _holdSpeechService is null)
            {
                return;
            }

            speechService = _holdSpeechService;
            startTask = _voiceCaptureStartTask;
            startedAt = _voiceCaptureStartedAt ?? DateTimeOffset.UtcNow;
            _voiceHolding = false;
            _holdSpeechService = null;
            _voiceCaptureStartTask = null;
            _voiceCaptureStartedAt = null;
            VoiceRecordButton.Content = "长按录音";
        }
        finally
        {
            _voiceCaptureLock.Release();
        }

        if (speechService is null)
        {
            return;
        }

        try
        {
            if (startTask is not null)
            {
                await startTask;
            }

            if (DateTimeOffset.UtcNow - startedAt < MinimumVoiceCaptureDuration)
            {
                AppLogger.Info("chat", "voice capture ignored because press was too short");
                SetUiState(ChatUiState.Idle);
                RaiseActivityChanged(ChatActivityKind.Idle);
                return;
            }

            var text = await speechService.StopCaptureAndRecognizeAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                await SendVoiceTextAsync(text.Trim());
            }
            else
            {
                SetUiState(ChatUiState.Idle);
                RaiseActivityChanged(ChatActivityKind.Idle);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("chat", "voice capture stop failed", ex);
            SetUiState(ChatUiState.Failed);
            RaiseActivityChanged(ChatActivityKind.Failed);
        }
        finally
        {
            speechService.Dispose();
        }
    }

    private async Task ResetVoiceCaptureStateAsync()
    {
        SpeechService? speechService;
        await _voiceCaptureLock.WaitAsync();
        try
        {
            speechService = _holdSpeechService;
            _holdSpeechService = null;
            _voiceCaptureStartTask = null;
            _voiceCaptureStartedAt = null;
            _voiceHolding = false;
            VoiceRecordButton.Content = "长按录音";
        }
        finally
        {
            _voiceCaptureLock.Release();
        }

        speechService?.Dispose();
        RaiseActivityChanged(ChatActivityKind.Idle);
        SetUiState(ChatUiState.Idle);
    }

    private async Task SendVoiceTextAsync(string text)
    {
        if (_isSending || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await SendUserTurnAsync(text, attachments: null, clearInputBox: false);
    }

    private string BuildPromptWithRecentContext(IReadOnlyList<ChatMessageRecord> recentMessages, string userInput, IReadOnlyList<ChatAttachment>? attachments, string? memoryBlock = null)
    {
        var rounds = recentMessages.TakeLast(40).ToList();
        var sb = new System.Text.StringBuilder();
        // memoryBlock 由调用方用 Mem0 按当前消息检索后传入（已含【长期记忆】标题）
        if (!string.IsNullOrWhiteSpace(memoryBlock))
        {
            sb.AppendLine(memoryBlock);
            sb.AppendLine();
        }

        if (rounds.Count > 0)
        {
            sb.AppendLine("以下是最近对话上下文，请结合上下文继续：");
            foreach (var m in rounds)
            {
                var role = m.Role == "assistant" ? "小爱" : "你";
                sb.Append(role).Append("：").AppendLine(m.Content);
            }
            sb.AppendLine();
        }

        // 当前这一条用户消息只出现一次（下方）。附件作为多模态内容由底层直接附在本次消息后，
        // 这里用文字提示模型「图片/文件已随消息附带，请直接查看」，而不是让模型去读路径。
        sb.Append("你：").AppendLine(userInput);
        if (attachments is { Count: > 0 })
        {
            var images = attachments.Where(a => a.Kind == ChatAttachmentKind.Image).ToList();
            var others = attachments.Where(a => a.Kind != ChatAttachmentKind.Image).ToList();
            if (images.Count > 0)
            {
                sb.AppendLine($"（本次消息已附带 {images.Count} 张图片，图片内容已直接随消息发送给你，请直接查看并分析图片，不要回复看不到或需要截图。）");
            }
            if (others.Count > 0)
            {
                sb.AppendLine($"（本次消息已附带 {others.Count} 个文件，文本类文件内容已直接随消息发送给你。）");
            }
        }

        sb.AppendLine("要求：如果用户要求执行电脑操作，请优先调用可用工具并给出执行反馈。反馈要像日常对话，不要展示确认编号、插件名、函数名、命令细节、可执行文件名或长串内部 ID；除非用户明确询问技术细节。只输出纯文本，不要 Markdown。");
        return sb.ToString();
    }

    private bool ShouldSuppressConfirmationReply(string? text)
    {
        if (_confirmationCreatedDuringGeneration)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains(ToolConfirmationService.PendingMarkerPrefix, StringComparison.OrdinalIgnoreCase)
               || text.Contains("确认编号", StringComparison.OrdinalIgnoreCase)
               || text.Contains("等待用户确认", StringComparison.OrdinalIgnoreCase)
               || text.Contains("高风险操作已暂停", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatToolResultForUser(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(无回复)";
        }

        var cleaned = text.Trim();
        if (cleaned.Contains(ToolConfirmationService.PendingMarkerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (cleaned.Contains("已取消高风险操作", StringComparison.OrdinalIgnoreCase))
        {
            return "好哒，这个操作已经取消啦。";
        }

        if (cleaned.Contains("该操作已不存在或已处理", StringComparison.OrdinalIgnoreCase))
        {
            return "这个操作已经处理过啦，不会重复执行。";
        }

        if (cleaned.StartsWith("文件已成功写入：", StringComparison.Ordinal))
        {
            return "写好了，文件已经帮你更新啦。";
        }

        if (cleaned.StartsWith("截图已保存到：", StringComparison.Ordinal))
        {
            return "截图已经保存好啦。";
        }

        if (cleaned.StartsWith("已优先启动本机应用：", StringComparison.Ordinal) ||
            cleaned.StartsWith("已启动应用：", StringComparison.Ordinal))
        {
            var appName = cleaned.Split('：').LastOrDefault()?.Trim() ?? string.Empty;
            var friendlyName = FriendlyAppName(appName);
            return $"{friendlyName}已经帮你打开啦。";
        }

        if (cleaned.StartsWith("已在浏览器中打开：", StringComparison.Ordinal))
        {
            return "网页已经帮你打开啦。";
        }

        if (cleaned.StartsWith("已在 ", StringComparison.Ordinal) && cleaned.Contains(" 中搜索：", StringComparison.Ordinal))
        {
            return "本机没找到对应应用，小爱先帮你打开搜索结果啦。";
        }

        cleaned = Regex.Replace(cleaned, ToolConfirmationService.PendingMarkerPrefix + "[a-zA-Z0-9]+", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "确认编号[：:]?\\s*[a-fA-F0-9]{12,}", string.Empty);
        cleaned = Regex.Replace(cleaned, "\\b[\\w.-]+\\.exe\\b", match => FriendlyAppName(match.Value), RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "\\b[a-fA-F0-9]{16,}\\b", string.Empty);
        cleaned = Regex.Replace(cleaned, "\\s{2,}", " ");
        return cleaned.Trim();
    }

    private static string FriendlyAppName(string appName)
    {
        var normalized = appName.Trim();
        return normalized.ToLowerInvariant() switch
        {
            "qqlive.exe" or "tencentvideo.exe" or "qqliveplayer.exe" => "腾讯视频",
            "bilibili.exe" => "哔哩哔哩",
            "cloudmusic.exe" or "neteasecloudmusic.exe" => "网易云音乐",
            "qqmusic.exe" => "QQ 音乐",
            "wechat.exe" or "weixin.exe" or "wxwork.exe" => "微信",
            "qq.exe" => "QQ",
            "msedge.exe" => "Edge",
            "chrome.exe" => "Chrome",
            _ when normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) => Path.GetFileNameWithoutExtension(normalized),
            _ => string.IsNullOrWhiteSpace(normalized) ? "应用" : normalized
        };
    }

    private TextBlock AddMessageBubble(int messageIndex, bool isAssistant, string text, bool isPending)
    {
        var root = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(0, 0, 0, 8),
            Focusable = !isPending
        };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(isAssistant ? "Auto,*" : "*,Auto"),
            Margin = new Thickness(0, 2, 0, 2)
        };
        row.Classes.Add("message-row");
        row.Bind(
            Control.WidthProperty,
            new Binding("Bounds.Width")
            {
                Source = MessagesPanel,
                Mode = BindingMode.OneWay
            });
        var avatar = new AvaloniaImage
        {
            Width = 38,
            Height = 38,
            Stretch = Stretch.UniformToFill,
            Source = isAssistant ? _assistantAvatar : GetUserAvatar(),
            Clip = new EllipseGeometry(new Rect(0, 0, 38, 38))
        };
        var avatarShell = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(3),
            Background = AemiUi.Brush(isAssistant ? AemiUi.PinkSoft : AemiUi.HaloSoft),
            BorderBrush = AemiUi.Brush(isAssistant ? AemiUi.Star : AemiUi.Halo),
            BorderThickness = new Thickness(1),
            Margin = isAssistant ? new Thickness(0, 4, 10, 0) : new Thickness(10, 4, 0, 0),
            Child = avatar
        };
        var bubble = new Border
        {
            MaxWidth = 720,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 11),
            Background = AemiUi.Brush(isAssistant ? AemiUi.Panel : AemiUi.PanelSoft),
            BorderBrush = AemiUi.Brush(isAssistant ? AemiUi.Border : AemiUi.Pink),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = isAssistant ? HorizontalAlignment.Left : HorizontalAlignment.Right
        };

        var timestamp = messageIndex >= 0 && messageIndex < _displayMessages.Count
            ? _displayMessages[messageIndex].Timestamp.ToLocalTime()
            : DateTimeOffset.Now;
        var speaker = isAssistant ? "小爱 · 飞行雪绒" : "你";
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = speaker,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = AemiUi.Brush(isAssistant ? AemiUi.Icon : AemiUi.TextSecondary)
        });
        var timeText = new TextBlock
        {
            Text = timestamp.ToString("HH:mm"),
            FontSize = 11,
            Foreground = AemiUi.Brush(AemiUi.TextMuted),
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(timeText, 1);
        header.Children.Add(timeText);

        var content = new StackPanel { Spacing = 7 };
        content.Children.Add(header);
        if (!isAssistant && !isPending && messageIndex >= 0 && messageIndex < _displayMessages.Count)
        {
            var attachmentPanel = BuildMessageAttachmentPanel(_displayMessages[messageIndex].Attachments, _attachmentRenderCts.Token);
            if (attachmentPanel.Children.Count > 0)
            {
                content.Children.Add(attachmentPanel);
            }
        }

        var streamText = new TextBlock
        {
            Text = isPending ? _pendingFrames[0] : text,
            Foreground = AemiUi.Brush(AemiUi.Ghost),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 23,
            FontSize = 15
        };
        if (isPending)
        {
            content.Children.Add(streamText);
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            content.Children.Add(new MarkdownPresenter(text));
        }

        if (!isPending && messageIndex >= 0)
        {
            content.Children.Add(BuildMessageActions(messageIndex, isAssistant));
            bubble.ContextMenu = BuildMessageContextMenu(messageIndex, isAssistant);
            AutomationProperties.SetName(root, $"{speaker}的消息，{timeText.Text}");
        }
        bubble.Child = content;

        if (isAssistant)
        {
            row.Children.Add(avatarShell);
            row.Children.Add(bubble);
            Grid.SetColumn(avatarShell, 0);
            Grid.SetColumn(bubble, 1);
        }
        else
        {
            row.Children.Add(bubble);
            row.Children.Add(avatarShell);
            Grid.SetColumn(bubble, 0);
            Grid.SetColumn(avatarShell, 1);
        }
        root.Children.Add(row);

        MessagesPanel.Children.Add(root);
        ScrollToBottom();
        return streamText;
    }
    private StackPanel BuildMessageAttachmentPanel(
        IReadOnlyList<ChatAttachment>? attachments,
        CancellationToken cancellationToken)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var attachment in attachments ?? Array.Empty<ChatAttachment>())
        {
            if (attachment.Kind == ChatAttachmentKind.Image)
            {
                var host = CreateImageAttachmentHost(attachment);
                panel.Children.Add(host);
                _ = LoadImageAttachmentAsync(host, attachment, cancellationToken);
                continue;
            }

            var unavailableReason = File.Exists(attachment.Path) ? null : "文件不存在";
            panel.Children.Add(AttachmentCardFactory.CreateFileCard(attachment, _fileIcon, unavailableReason));
        }
        return panel;
    }

    private static Border CreateImageAttachmentHost(ChatAttachment attachment)
    {
        var host = new Border
        {
            MaxWidth = 420,
            MinHeight = 96,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(12),
            Background = AemiUi.Brush(AemiUi.HaloSoft),
            BorderBrush = AemiUi.Brush(AemiUi.Border),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = new TextBlock
            {
                Text = "正在加载图片预览…",
                Margin = new Thickness(14),
                Foreground = AemiUi.Brush(AemiUi.TextMuted),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        AutomationProperties.SetName(host, $"图片附件 {attachment.Name}，正在加载");
        return host;
    }

    private async Task LoadImageAttachmentAsync(
        Border host,
        ChatAttachment attachment,
        CancellationToken cancellationToken)
    {
        try
        {
            var bitmap = await _attachmentThumbnailCache.GetAsync(attachment, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (bitmap is null)
                {
                    ShowUnavailableImageAttachment(host, attachment);
                    return;
                }

                host.MinHeight = 0;
                host.Background = AemiUi.Brush(AemiUi.PanelSoft);
                host.Child = new AvaloniaImage
                {
                    Source = bitmap,
                    MaxWidth = 420,
                    MaxHeight = 260,
                    Stretch = Stretch.Uniform
                };
                AutomationProperties.SetName(host, $"图片附件 {attachment.Name}");
                ToolTip.SetTip(host, attachment.Path);
            });
        }
        catch (OperationCanceledException)
        {
            // A new session/render pass superseded this preview.
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() => ShowUnavailableImageAttachment(host, attachment));
            }
        }
    }

    private void ShowUnavailableImageAttachment(Border host, ChatAttachment attachment)
    {
        var reason = File.Exists(attachment.Path) ? "图片无法预览" : "文件不存在";
        host.MinHeight = 0;
        host.Background = Brushes.Transparent;
        host.BorderThickness = new Thickness(0);
        host.Child = AttachmentCardFactory.CreateFileCard(attachment, _imageIcon, reason);
        AutomationProperties.SetName(host, $"不可用图片附件 {attachment.Name}：{reason}");
    }

    internal WrapPanel BuildMessageActions(int messageIndex, bool isAssistant)
    {
        var panel = new WrapPanel
        {
            HorizontalAlignment = isAssistant ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Margin = new Thickness(0, 5, 0, 0)
        };
        panel.Classes.Add("message-actions");
        AutomationProperties.SetName(panel, "消息操作");

        panel.Children.Add(BuildActionButton(_copyIcon, "复制", async () => await CopyMessageAsync(messageIndex)));
        if (isAssistant)
        {
            panel.Children.Add(BuildActionButton(_retryIcon, "重新回答", async () => await RegenerateAssistantAsync(messageIndex)));
        }
        panel.Children.Add(BuildActionButton(_deleteIcon, "删除", () => DeleteMessage(messageIndex), danger: true));

        return panel;
    }

    private ContextMenu BuildMessageContextMenu(int messageIndex, bool isAssistant)
    {
        var menu = new ContextMenu();
        var copyItem = new MenuItem { Header = "复制" };
        copyItem.Click += async (_, _) => await CopyMessageAsync(messageIndex);
        menu.Items.Add(copyItem);

        if (isAssistant)
        {
            var retryItem = new MenuItem { Header = "重新回答" };
            retryItem.Click += async (_, _) => await RegenerateAssistantAsync(messageIndex);
            menu.Items.Add(retryItem);
        }

        menu.Items.Add(new Separator());
        var deleteItem = new MenuItem { Header = "删除" };
        deleteItem.Click += (_, _) => DeleteMessage(messageIndex);
        menu.Items.Add(deleteItem);
        return menu;
    }

    private void OnPendingToolActionCreated(object? sender, PendingToolAction action)
    {
        _confirmationCreatedDuringGeneration = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (_pendingToolActions.ContainsKey(action.Id))
            {
                return;
            }

            _pendingToolActions[action.Id] = action;
            UpdateProviderQuickSwitchEnabled();
            SetUiState(ChatUiState.WaitingConfirmation);
            RaiseActivityChanged(ChatActivityKind.ToolWaiting);
            AddPendingToolActionCard(action);
            ScrollToBottom();
        }, DispatcherPriority.Background);
    }

    private void RenderPendingToolActions()
    {
        if (_toolConfirmationService is not null)
        {
            foreach (var action in _toolConfirmationService.PendingActions)
            {
                _pendingToolActions.TryAdd(action.Id, action);
            }
        }

        foreach (var action in _pendingToolActions.Values.OrderBy(x => x.CreatedAt))
        {
            AddPendingToolActionCard(action);
        }
    }

    private void AddPendingToolActionCard(PendingToolAction action)
    {
        var root = new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16),
            Margin = new Thickness(46, 2, 12, 8),
            Background = AemiUi.Brush(AemiUi.PanelSoft),
            BorderBrush = AemiUi.Brush(AemiUi.Border),
            BorderThickness = new Thickness(1),
            MaxWidth = ChatScrollViewer.Bounds.Width > 0
                ? Math.Max(260, ChatScrollViewer.Bounds.Width * 0.72)
                : 520
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(AemiUi.Badge("高风险操作确认", "danger"));
        panel.Children.Add(new TextBlock
        {
            Text = "高风险操作需要确认",
            Foreground = AemiUi.Brush(AemiUi.Error),
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            FontSize = 15
        });
        panel.Children.Add(new TextBlock
        {
            Text = action.Title,
            Foreground = AemiUi.Brush(AemiUi.Ghost),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            FontSize = 14
        });
        panel.Children.Add(new TextBlock
        {
            Text = action.Description,
            Foreground = AemiUi.Brush(AemiUi.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            MaxHeight = 120
        });

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8
        };

        var confirmButton = AemiUi.Button("确认执行", "primary", 92);
        confirmButton.Click += (_, _) => ResolvePendingToolAction(action.Id, confirm: true);

        var cancelButton = AemiUi.Button("取消", "ghost", 82);
        cancelButton.Click += (_, _) => ResolvePendingToolAction(action.Id, confirm: false);

        buttons.Children.Add(confirmButton);
        buttons.Children.Add(cancelButton);
        panel.Children.Add(buttons);
        root.Child = panel;
        MessagesPanel.Children.Add(root);
        _pendingActionCards[action.Id] = root;
    }

    /// <summary>从面板移除某条确认卡片（确认或取消后调用）。</summary>
    private void RemovePendingActionCard(string actionId)
    {
        if (_pendingActionCards.TryGetValue(actionId, out var card))
        {
            MessagesPanel.Children.Remove(card);
            _pendingActionCards.Remove(actionId);
        }
    }

    private void ResolvePendingToolAction(string actionId, bool confirm)
    {
        if (_toolConfirmationService is null)
        {
            return;
        }

        // 无论确认还是取消，先立即移除确认卡片（避免弹窗残留）。
        RemovePendingActionCard(actionId);

        var action = _toolConfirmationService.GetPendingAction(actionId);
        var isLongRunning = action?.IsLongRunning == true;
        _pendingToolActions.Remove(actionId);

        if (!confirm)
        {
            // 取消：Cancel 会立即触发 PendingActionCompleted（结果「已取消」），由 OnPendingToolActionCompleted 统一回填
            _toolConfirmationService.Cancel(actionId);
            return;
        }

        // 确认：
        // - 长任务（电脑控制）：先显示占位气泡 + 桌宠 Running 状态，让用户看到正在执行、UI 不卡。
        //   真实结果在后台执行完毕后由 OnPendingToolActionCompleted 回填（替换占位气泡）。
        // - 快操作：ConfirmAsync 后台执行，完成后同样由 OnPendingToolActionCompleted 回填。
        // 关键：ConfirmAsync 会先从 service 字典同步移除该 action，再后台执行闭包。
        // 必须在 RenderCurrentMessages 之前调用，否则 RenderPendingToolActions 会从
        // service.PendingActions 把刚移除的 action 又加回来、重画确认卡片（点一次不消失）。
        _toolConfirmationService.ConfirmAsync(actionId);

        if (isLongRunning)
        {
            EnsureCurrentSession();
            var placeholder = new ChatMessageRecord
            {
                Role = "assistant",
                Content = "小爱正在操作电脑，请稍候……（你可以随时移动鼠标，但请勿抢占键盘鼠标焦点）",
                Timestamp = DateTimeOffset.UtcNow
            };
            _displayMessages.Add(placeholder);
            _sessionStore.AppendMessage(_currentSessionId, "assistant", placeholder.Content);
            RenderCurrentMessages();
            ScrollToBottom();
            _runningActionPlaceholders[actionId] = _displayMessages.Count - 1;
            RaiseActivityChanged(ChatActivityKind.Sending); // 桌宠 Running 动画
        }
    }

    /// <summary>
    /// 后台确认任务执行完成（成功/失败/取消）。把结果回填到聊天：
    /// 若有占位气泡则替换，否则追加一条助手消息。
    /// 此回调可能从后台线程触发，UI 操作必须切回 UI 线程。
    /// </summary>
    private void OnPendingToolActionCompleted(object? sender, PendingActionResultEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnPendingToolActionCompleted(sender, e));
            return;
        }

        EnsureCurrentSession();
        var text = FormatToolResultForUser(e.Result);

        if (_runningActionPlaceholders.TryGetValue(e.Id, out var idx) && idx >= 0 && idx < _displayMessages.Count)
        {
            _displayMessages[idx] = new ChatMessageRecord
            {
                Role = "assistant",
                Content = text,
                Timestamp = DateTimeOffset.UtcNow
            };
            _runningActionPlaceholders.Remove(e.Id);
        }
        else
        {
            _displayMessages.Add(new ChatMessageRecord
            {
                Role = "assistant",
                Content = text,
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        _sessionStore.AppendMessage(_currentSessionId, "assistant", text);
        RenderCurrentMessages();
        ScrollToBottom();
        UpdateProviderQuickSwitchEnabled();
        SetUiState(_pendingToolActions.Count == 0 && _runningActionPlaceholders.Count == 0
            ? ChatUiState.Idle
            : ChatUiState.WaitingConfirmation);
        // 完成后写入 Mem0（后台），并恢复桌宠状态
        _ = Task.Run(() => _memoryOrchestrator.AddTurnAsync(_currentSessionId, text, text));
        RaiseActivityChanged(_pendingToolActions.Count == 0 && _runningActionPlaceholders.Count == 0
            ? ChatActivityKind.Completed
            : ChatActivityKind.ToolWaiting);
    }

    private Button BuildActionButton(IImage icon, string tooltip, Action onClick, bool danger = false)
    {
        var button = AemiUi.IconButton(icon, tooltip);
        ApplyMessageActionTone(button, danger);
        button.Click += (_, _) => onClick();
        return button;
    }

    private Button BuildActionButton(IImage icon, string tooltip, Func<Task> onClick, bool danger = false)
    {
        var button = AemiUi.IconButton(icon, tooltip);
        ApplyMessageActionTone(button, danger);
        button.Click += async (_, _) => await onClick();
        return button;
    }

    private static void ApplyMessageActionTone(Button button, bool danger)
    {
        button.Width = 38;
        button.Height = 34;
        button.MinWidth = 38;
        button.MinHeight = 34;
        if (!danger)
        {
            return;
        }

        button.Classes.Remove("ghost");
        button.Classes.Add("danger");
    }

    private async Task CopyMessageAsync(int index)
    {
        if (index < 0 || index >= _displayMessages.Count)
        {
            return;
        }

        var topLevel = GetTopLevel(this);
        if (topLevel?.Clipboard is not null)
        {
            await topLevel.Clipboard.SetTextAsync(_displayMessages[index].Content);
        }
    }

    private void DeleteMessage(int index)
    {
        if (index < 0 || index >= _displayMessages.Count)
        {
            return;
        }

        _displayMessages.RemoveAt(index);
        PersistCurrentMessages();
        RenderCurrentMessages();
    }

    private async Task RegenerateAssistantAsync(int assistantIndex)
    {
        if (assistantIndex <= 0 || assistantIndex >= _displayMessages.Count)
        {
            return;
        }

        var userMessage = _displayMessages
            .Take(assistantIndex)
            .LastOrDefault(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (userMessage is null)
        {
            return;
        }

        if (string.Equals(_displayMessages[assistantIndex].Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            _displayMessages.RemoveAt(assistantIndex);
            PersistCurrentMessages();
            RenderCurrentMessages();
        }

        await GenerateAssistantReplyForUserAsync(userMessage);
    }

    private async Task GenerateAssistantReplyForUserAsync(ChatMessageRecord userMessage)
    {
        if (_isSending)
        {
            return;
        }

        var attachments = userMessage.Attachments?.Select(attachment => attachment with { }).ToList()
            ?? new List<ChatAttachment>();
        var userContent = string.IsNullOrWhiteSpace(userMessage.Content) && attachments.Count > 0
            ? "请分析我上传的附件。"
            : userMessage.Content;

        _isSending = true;
        _sendCancellationTokenSource?.Dispose();
        _sendCancellationTokenSource = new CancellationTokenSource();
        SetUiState(ChatUiState.Streaming);
        RaiseActivityChanged(ChatActivityKind.Sending);
        UpdateProviderQuickSwitchEnabled();
        PauseAmbientFlicker();
        var pending = AddMessageBubble(_displayMessages.Count, true, string.Empty, true);
        _pendingTextBlock = pending;
        _pendingFrameIndex = 0;
        _pendingTimer.Start();
        _confirmationCreatedDuringGeneration = false;

        try
        {
            _chatService.ClearHistory();
            var recent = _displayMessages.TakeLast(40).ToList();
            var memoryBlock = await _memoryOrchestrator.BuildRelevantMemoryBlockAsync(_currentSessionId, userContent);
            var prompt = BuildPromptWithRecentContext(recent, userContent, attachments, memoryBlock);
            var reply = await StreamReplyIntoAsync(prompt, pending, attachments, _sendCancellationTokenSource.Token);
            var sanitized = SanitizeAssistantOutput(reply);
            if (ShouldSuppressConfirmationReply(sanitized))
            {
                RenderCurrentMessages();
                RaiseActivityChanged(ChatActivityKind.ToolWaiting);
                return;
            }

            var cleaned = string.IsNullOrWhiteSpace(sanitized) ? "(无回复)" : FormatToolResultForUser(sanitized);
            _displayMessages.Add(new ChatMessageRecord { Role = "assistant", Content = cleaned, Timestamp = DateTimeOffset.UtcNow });
            PersistCurrentMessages();
            RenderCurrentMessages();
            _ = Task.Run(() => _memoryOrchestrator.AddTurnAsync(_currentSessionId, userContent, cleaned));
            RaiseActivityChanged(ChatActivityKind.Completed);
        }
        catch (OperationCanceledException)
        {
            RenderCurrentMessages();
            RaiseActivityChanged(ChatActivityKind.Canceled);
        }
        catch (Exception ex)
        {
            _displayMessages.Add(new ChatMessageRecord { Role = "assistant", Content = $"执行失败：{ex.Message}", Timestamp = DateTimeOffset.UtcNow });
            PersistCurrentMessages();
            RenderCurrentMessages();
            SetUiState(ChatUiState.Failed);
            RaiseActivityChanged(ChatActivityKind.Failed);
        }
        finally
        {
            _pendingTimer.Stop();
            _pendingTextBlock = null;
            _isSending = false;
            _sendCancellationTokenSource?.Dispose();
            _sendCancellationTokenSource = null;
            ResumeAmbientFlicker();
            SetUiState(_pendingToolActions.Count > 0 ? ChatUiState.WaitingConfirmation : ChatUiState.Idle);
            UpdateProviderQuickSwitchEnabled();
        }
    }

    private void OnSettingsChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var requestedBackground = _settingsService.Current.ChatBackgroundImagePath;
            if (!string.Equals(_appliedChatBackgroundPath, requestedBackground, StringComparison.OrdinalIgnoreCase))
            {
                ApplyChatBackgroundImage();
            }

            UpdateAmbientAnimationState();
        });
    }

    private void OnProvidersChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 仅在非发送状态时刷新，避免发送过程中重置 UI
            if (!_isSending)
            {
                RefreshProviderQuickSwitch();
            }
        });
    }

    private void RefreshProviderQuickSwitch(string? selectedProvider = null, string? selectedModel = null)
    {
        _isLoadingProviderSwitch = true;
        try
        {
            ResetComboBoxItems(ProviderQuickSwitchBox);
            var currentProvider = SettingsService.NormalizeProviderName(selectedProvider ?? _settingsService.Current.CurrentProvider);
            foreach (var provider in _settingsService.ListProviders())
            {
                var item = new ComboBoxItem
                {
                    Content = provider,
                    Tag = provider
                };
                ProviderQuickSwitchBox.Items.Add(item);
                if (string.Equals(provider, currentProvider, StringComparison.OrdinalIgnoreCase))
                {
                    ProviderQuickSwitchBox.SelectedItem = item;
                }
            }

            RefreshModelQuickSwitch(selectedModel, currentProvider);
        }
        finally
        {
            _isLoadingProviderSwitch = false;
            UpdateProviderQuickSwitchEnabled();
        }
    }

    private void RefreshModelQuickSwitch(string? selectedModel = null, string? providerOverride = null)
    {
        var wasLoadingProviderSwitch = _isLoadingProviderSwitch;
        _isLoadingProviderSwitch = true;
        try
        {
            ResetComboBoxItems(ModelQuickSwitchBox);
            var provider = SettingsService.NormalizeProviderName(providerOverride ?? _settingsService.Current.CurrentProvider);
            var info = _settingsService.GetApiKeyInfo(provider);
            var currentModel = string.IsNullOrWhiteSpace(selectedModel)
                ? info?.ModelId ?? _settingsService.Current.DefaultModel
                : selectedModel;
            var models = _settingsService.GetProviderModels(provider, enabledOnly: true).ToList();
            if (!string.IsNullOrWhiteSpace(currentModel) &&
                models.All(m => !string.Equals(m.Id, currentModel, StringComparison.OrdinalIgnoreCase)))
            {
                models.Insert(0, new ProviderModel { Id = currentModel, IsEnabled = true });
            }

            foreach (var model in models)
            {
                var item = new ComboBoxItem
                {
                    Content = model.Id,
                    Tag = model.Id
                };
                ModelQuickSwitchBox.Items.Add(item);
                if (string.Equals(model.Id, currentModel, StringComparison.OrdinalIgnoreCase))
                {
                    ModelQuickSwitchBox.SelectedItem = item;
                }
            }
        }
        finally
        {
            _isLoadingProviderSwitch = wasLoadingProviderSwitch;
        }
    }

    private static void ResetComboBoxItems(ComboBox comboBox)
    {
        comboBox.SelectedIndex = -1;
        comboBox.SelectedItem = null;
        comboBox.Items.Clear();
    }

    private async Task SwitchQuickProviderAsync()
    {
        if (_isLoadingProviderSwitch || ProviderQuickSwitchBox.SelectedItem is not ComboBoxItem { Tag: string provider })
        {
            return;
        }

        if (!CanSwitchProviderOrModel())
        {
            RefreshProviderQuickSwitch();
            return;
        }

        if (!await _providerSwitchLock.WaitAsync(0))
        {
            return;
        }

        var oldProvider = _settingsService.Current.CurrentProvider;
        var oldModel = _settingsService.GetApiKeyInfo(oldProvider)?.ModelId ?? _settingsService.Current.DefaultModel;
        try
        {
            ShowStatusMessage("正在切换提供商...");
            UpdateProviderQuickSwitchEnabled(forceDisabled: true);

            if (string.Equals(SettingsService.NormalizeProviderName(provider), SettingsService.NormalizeProviderName(oldProvider), StringComparison.OrdinalIgnoreCase))
            {
                HideStatusMessage();
                return;
            }

            if (_settingsService.GetApiKeyInfo(provider) is null)
            {
                ShowStatusMessage("切换失败：未找到这个提供商。");
                RefreshProviderQuickSwitch(oldProvider, oldModel);
                return;
            }

            var switched = _settingsService.SwitchCurrentProvider(provider);
            if (!switched)
            {
                ShowStatusMessage("切换失败：配置没有保存成功。");
                RefreshProviderQuickSwitch(oldProvider, oldModel);
                return;
            }

            var activeProvider = _settingsService.Current.CurrentProvider;
            var targetInfo = _settingsService.GetApiKeyInfo(activeProvider);
            var targetModel = targetInfo?.ModelId ?? _settingsService.Current.DefaultModel;
            var ready = TryReloadChatServiceFromSettings(out var error);

            RefreshProviderQuickSwitch(activeProvider, targetModel);
            ShowStatusMessage(ready
                ? $"已切换到 {activeProvider} / {targetModel}"
                : $"已切换到 {activeProvider}，但服务暂时还没准备好：{error ?? "请检查 API Key、Endpoint 和模型配置。"}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("chat", "quick provider switch failed", ex);
            RefreshProviderQuickSwitch();
            ShowStatusMessage($"切换时遇到问题：{ex.Message}");
        }
        finally
        {
            _providerSwitchLock.Release();
            UpdateProviderQuickSwitchEnabled();
        }
    }

    private async Task SwitchQuickModelAsync()
    {
        if (_isLoadingProviderSwitch || ModelQuickSwitchBox.SelectedItem is not ComboBoxItem { Tag: string model })
        {
            return;
        }

        if (!CanSwitchProviderOrModel())
        {
            RefreshModelQuickSwitch();
            return;
        }

        if (!await _providerSwitchLock.WaitAsync(0))
        {
            return;
        }

        var provider = _settingsService.Current.CurrentProvider;
        var oldModel = _settingsService.GetApiKeyInfo(provider)?.ModelId ?? _settingsService.Current.DefaultModel;
        try
        {
            ShowStatusMessage("正在切换模型...");
            UpdateProviderQuickSwitchEnabled(forceDisabled: true);
            if (string.IsNullOrWhiteSpace(model) || string.Equals(model, oldModel, StringComparison.OrdinalIgnoreCase))
            {
                HideStatusMessage();
                return;
            }

            var switched = _settingsService.SwitchCurrentModel(provider, model);
            if (!switched)
            {
                RefreshModelQuickSwitch(oldModel, provider);
                ShowStatusMessage("模型切换失败：没有找到这个模型配置。");
                return;
            }

            var ready = TryReloadChatServiceFromSettings(out var error);
            RefreshModelQuickSwitch(model, provider);
            ShowStatusMessage(ready
                ? $"已切换模型：{model}"
                : $"已切换模型：{model}，但服务暂时还没准备好：{error ?? "请检查 API Key、Endpoint 和模型配置。"}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("chat", "quick model switch failed", ex);
            RefreshModelQuickSwitch();
            ShowStatusMessage($"模型切换时遇到问题：{ex.Message}");
        }
        finally
        {
            _providerSwitchLock.Release();
            UpdateProviderQuickSwitchEnabled();
        }
    }

    private bool CanSwitchProviderOrModel()
        => !_isSending && !_voiceHolding && _pendingToolActions.Count == 0;

    private void UpdateProviderQuickSwitchEnabled(bool forceDisabled = false)
    {
        var enabled = !forceDisabled && CanSwitchProviderOrModel();
        ProviderQuickSwitchBox.IsEnabled = enabled;
        ModelQuickSwitchBox.IsEnabled = enabled;
        UploadButton.IsEnabled = !forceDisabled && !_isSending && !_voiceHolding && _pendingToolActions.Count == 0;
        McpToolsButton.IsEnabled = enabled && _pendingToolActions.Count == 0;
    }

    private bool TryReloadChatServiceFromSettings(out string? error)
    {
        error = null;
        try
        {
            if (_chatService is AemiChatService aemiChatService)
            {
                return aemiChatService.TryReloadFromSettings(out error);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLogger.Error("chat", "reload chat service failed", ex);
            return false;
        }
    }

    private void PauseAmbientFlicker()
    {
        _flickerTimer.Stop();
        BackgroundContainer.Opacity = 1;
        GlowLayerPink.Opacity = 0.9;
        GlowLayerBlue.Opacity = 0.9;
        GlowLayerWhite.Opacity = 0.52;
    }

    private void UpdateAmbientAnimationState()
    {
        var shouldAnimate = IsVisible && WindowState != WindowState.Minimized && !_settingsService.Current.ReduceMotion;
        if (shouldAnimate)
        {
            ResumeAmbientFlicker();
            if (_settingsService.Current.EnableParticleEffects)
            {
                _particleEffect.Start(48);
            }
            else
            {
                _particleEffect.Stop();
            }
        }
        else
        {
            PauseAmbientFlicker();
            _particleEffect.Stop();
        }
    }

    private void ResumeAmbientFlicker()
    {
        if (!_settingsService.Current.ReduceMotion && IsVisible && WindowState != WindowState.Minimized)
        {
            _flickerTimer.Start();
        }
    }

    private void ScrollToBottom(bool force = false)
    {
        if (!force && !_userNearBottom)
        {
            JumpToLatestButton.IsVisible = _displayMessages.Count > 0;
            return;
        }

        if (_scrollPending) return;
        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                ChatScrollViewer.Offset = new Vector(ChatScrollViewer.Offset.X, double.MaxValue);
                _userNearBottom = true;
                JumpToLatestButton.IsVisible = false;
            }
            finally
            {
                _scrollPending = false;
            }
        }, DispatcherPriority.Background);
    }
    private IImage GetUserAvatar()
    {
        var type = _settingsService.Current.UserAvatarType;
        if (type == "female")
        {
            return _femaleAvatar;
        }

        if (type == "custom" && !string.IsNullOrWhiteSpace(_settingsService.Current.CustomUserAvatarPath) && File.Exists(_settingsService.Current.CustomUserAvatarPath))
        {
            // 缓存自定义头像 Bitmap，避免每条消息气泡都重新加载并泄漏旧实例（RES-006）。
            var path = _settingsService.Current.CustomUserAvatarPath;
            if (_customUserAvatar is null || _customUserAvatarPath != path)
            {
                try
                {
                    _customUserAvatar?.Dispose();
                    _customUserAvatar = new Bitmap(path);
                    _customUserAvatarPath = path;
                }
                catch
                {
                    _customUserAvatar = null;
                    _customUserAvatarPath = null;
                }
            }

            if (_customUserAvatar is not null)
            {
                return _customUserAvatar;
            }
        }

        return _maleAvatar;
    }

    private void ApplyChatBackgroundImage()
    {
        var path = _settingsService.Current.ChatBackgroundImagePath;
        _appliedChatBackgroundPath = path;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                SetChatBackground(new Bitmap(path));
                return;
            }
            catch
            {
            }
        }

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Aemeath-agent/Assets/static/chat-background-default.png"));
            SetChatBackground(new Bitmap(stream));
        }
        catch
        {
            ChatBackgroundHost.Background = AemiUi.Brush(AemiUi.Panel);
        }
    }

    /// <summary>设置聊天背景图：释放上一次缓存的 Bitmap，避免反复切换造成非托管内存累积（RES-008）。</summary>
    private void SetChatBackground(Bitmap bitmap)
    {
        if (!ReferenceEquals(_chatBackgroundBitmap, bitmap))
        {
            _chatBackgroundBitmap?.Dispose();
        }
        _chatBackgroundBitmap = bitmap;
        ChatBackgroundHost.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill, Opacity = 0.72 };
    }


    private void StartNewSession()
    {
        var session = _sessionStore.CreateSession();
        _currentSessionId = session.Id;
        _displayMessages.Clear();
        MessagesPanel.Children.Clear();
        RefreshSessionSelector(_currentSessionId);
        UpdateEmptyState();
        InputBox.Focus();
    }

    private void LoadLatestSessionOrCreateIfEmpty()
    {
        var sessions = _sessionStore.ListSessions();
        if (sessions.Count == 0)
        {
            _currentSessionId = string.Empty;
            _displayMessages.Clear();
            MessagesPanel.Children.Clear();
            RefreshSessionSelector(string.Empty);
            UpdateEmptyState();
            return;
        }

        RefreshSessionSelector(sessions[0].Id);
        LoadSession(sessions[0].Id);
    }

    private void LoadLatestSessionOrEmpty()
    {
        var sessions = _sessionStore.ListSessions();
        if (sessions.Count == 0)
        {
            _currentSessionId = string.Empty;
            _displayMessages.Clear();
            MessagesPanel.Children.Clear();
            RefreshSessionSelector(string.Empty);
            UpdateEmptyState();
            return;
        }

        RefreshSessionSelector(sessions[0].Id);
        LoadSession(sessions[0].Id);
    }

    public void BeginNewSession()
    {
        StartNewSession();
    }

    private void LoadSession(string sessionId)
    {
        var session = _sessionStore.GetSession(sessionId);
        if (session is null)
        {
            return;
        }

        _currentSessionId = session.Id;
        _displayMessages.Clear();
        _displayMessages.AddRange(session.Messages.Select(x => new ChatMessageRecord
        {
            Role = x.Role,
            Content = x.Content,
            Timestamp = x.Timestamp,
            Attachments = x.Attachments?.Select(attachment => attachment with { }).ToList() ?? new List<ChatAttachment>()
        }));
        RenderCurrentMessages();
        RefreshSessionSelector(_currentSessionId);
        ScrollToBottom(force: true);
    }

    private void RefreshSessionSelector(string selectedId)
    {
        _isUpdatingSessionList = true;
        try
        {
            var sessions = _sessionStore.ListSessions();
            var query = SessionSearchBox.Text?.Trim();
            var visible = string.IsNullOrWhiteSpace(query)
                ? sessions
                : sessions.Where(s => s.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

            SessionCountText.Text = string.IsNullOrWhiteSpace(query)
                ? $"共 {sessions.Count} 个对话"
                : $"找到 {visible.Count} / {sessions.Count} 个对话";
            SessionListBox.Items.Clear();

            foreach (var session in visible)
            {
                var title = new TextBlock
                {
                    Text = session.Title,
                    FontWeight = FontWeight.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = AemiUi.Brush(AemiUi.Ghost)
                };
                var time = new TextBlock
                {
                    Text = FormatSessionTime(session.UpdatedAt),
                    Classes = { "muted" },
                    FontSize = 11
                };
                var item = new ListBoxItem
                {
                    Tag = session.Id,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new StackPanel { Spacing = 3, Children = { title, time } }
                };
                AutomationProperties.SetName(item, $"{session.Title}，{time.Text}");
                SessionListBox.Items.Add(item);
                if (string.Equals(session.Id, selectedId, StringComparison.Ordinal)) SessionListBox.SelectedItem = item;
            }

            if (visible.Count == 0)
            {
                SessionListBox.Items.Add(new ListBoxItem
                {
                    IsEnabled = false,
                    Content = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(query) ? "还没有对话。" : "没有匹配的对话。",
                        Classes = { "muted" },
                        TextWrapping = TextWrapping.Wrap
                    }
                });
            }
        }
        finally
        {
            _isUpdatingSessionList = false;
        }
    }

    private static string FormatSessionTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (local.Date == now.Date) return $"今天 {local:HH:mm}";
        if (local.Date == now.Date.AddDays(-1)) return $"昨天 {local:HH:mm}";
        return local.ToString("MM-dd HH:mm");
    }
    private void EnsureCurrentSession()
    {
        if (!string.IsNullOrWhiteSpace(_currentSessionId))
        {
            return;
        }

        var session = _sessionStore.CreateSession();
        _currentSessionId = session.Id;
        _displayMessages.Clear();
        RefreshSessionSelector(_currentSessionId);
    }

    private void RenderCurrentMessages()
    {
        _attachmentRenderCts.Cancel();
        _attachmentRenderCts.Dispose();
        _attachmentRenderCts = new CancellationTokenSource();
        MessagesPanel.Children.Clear();
        _attachmentThumbnailCache.ReleaseRenderedBitmaps();
        for (var i = 0; i < _displayMessages.Count; i++)
        {
            var message = _displayMessages[i];
            var isAssistant = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase);
            var content = isAssistant ? SanitizeAssistantOutput(message.Content) : message.Content;
            AddMessageBubble(i, isAssistant, content, false);
        }

        RenderPendingToolActions();
        UpdateEmptyState();
    }

    private void PersistCurrentMessages()
    {
        if (string.IsNullOrWhiteSpace(_currentSessionId))
        {
            return;
        }

        _sessionStore.ReplaceMessages(_currentSessionId, _displayMessages);
        RefreshSessionSelector(_currentSessionId);
    }

    private void RaiseActivityChanged(ChatActivityKind kind)
    {
        ActivityChanged?.Invoke(this, new ChatActivityChangedEventArgs(kind));
    }

    private static string SanitizeAssistantOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = text;
        var endThink = cleaned.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (endThink >= 0)
        {
            cleaned = cleaned[(endThink + "</think>".Length)..];
        }
        cleaned = Regex.Replace(cleaned, "<think>[\\s\\S]*?</think>", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "```think[\\s\\S]*?```", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "```thinking[\\s\\S]*?```", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "<details[\\s\\S]*?<summary>\\s*think[\\s\\S]*?</details>", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "(?is)thinking\\.\\.\\.[\\s\\S]*?(?=\\n\\n|$)", string.Empty);
        cleaned = Regex.Replace(cleaned, "<reasoning>[\\s\\S]*?</reasoning>", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\[/?think\]", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "(?im)^>\\s*thinking.*$", string.Empty);
        cleaned = Regex.Replace(cleaned, @"(?im)^\s*think:\s*$", string.Empty);
        cleaned = Regex.Replace(cleaned, @"(?im)^\s*thinking:\s*$", string.Empty);
        return cleaned.Trim();
    }

    private void ShowStatusMessage(string message)
    {
        ProviderSwitchStatusText.Text = message;
        ProviderSwitchStatusBorder.IsVisible = true;
        _statusHideTimer.Stop();
        _statusHideTimer.Start();
    }

    private void HideStatusMessage()
    {
        _statusHideTimer.Stop();
        ProviderSwitchStatusBorder.IsVisible = false;
        ProviderSwitchStatusText.Text = string.Empty;
    }

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.Info("chat", "chat window closed dispose resources");
        _pendingTimer.Stop();
        _flickerTimer.Stop();
        _particleEffect.Stop();
        _holdSpeechService?.Dispose();
        _sendCancellationTokenSource?.Cancel();
        _sendCancellationTokenSource?.Dispose();
        _sendCancellationTokenSource = null;
        RaiseActivityChanged(ChatActivityKind.Idle);
        if (_toolConfirmationService is not null)
        {
            _toolConfirmationService.PendingActionCreated -= OnPendingToolActionCreated;
            _toolConfirmationService.PendingActionCompleted -= OnPendingToolActionCompleted;
        }
        _attachmentRenderCts.Cancel();
        _attachmentRenderCts.Dispose();
        _attachmentThumbnailCache.Dispose();
        _assistantAvatar.Dispose();
        _maleAvatar.Dispose();
        _femaleAvatar.Dispose();
        _customUserAvatar?.Dispose();
        _chatBackgroundBitmap?.Dispose();
        base.OnClosed(e);
    }
}

public sealed class ChatActivityChangedEventArgs(ChatActivityKind kind) : EventArgs
{
    public ChatActivityKind Kind { get; } = kind;
}

internal enum ChatUiState
{
    Idle,
    Streaming,
    VoiceListening,
    WaitingConfirmation,
    Failed,
    Canceled
}

public enum ChatActivityKind
{
    Idle,
    Sending,
    VoiceListening,
    ToolWaiting,
    Completed,
    Failed,
    Canceled
}
internal sealed class NoOpChatService : IChatService
{
    public string CurrentAssistantName => "\u5c0f\u7231";
    public bool IsProcessing => false;

    public Task<string> SendMessageAsync(string message, CancellationToken cancellationToken = default)
        => Task.FromResult("\u672a\u914d\u7f6e AI \u670d\u52a1\uff0c\u8bf7\u5148\u5728\u8bbe\u7f6e\u4e2d\u586b\u5199 API Key\u3002");

    public Task<string> SendMessageAsync(
        string message,
        IReadOnlyList<ChatAttachment>? attachments,
        CancellationToken cancellationToken = default)
        => SendMessageAsync(message, cancellationToken);

    public IAsyncEnumerable<string> SendMessageStreamingAsync(string message, CancellationToken cancellationToken = default)
        => SendMessageStreamingAsync(message, null, cancellationToken);

    public async IAsyncEnumerable<string> SendMessageStreamingAsync(
        string message,
        IReadOnlyList<ChatAttachment>? attachments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return "\u672a\u914d\u7f6e AI \u670d\u52a1\uff0c\u8bf7\u5148\u5728\u8bbe\u7f6e\u4e2d\u586b\u5199 API Key\u3002";
        await Task.CompletedTask;
    }

    public void ClearHistory() { }
    public Task<bool> SwitchProviderAsync(string providerName, string apiKey, string? endpoint = null) => Task.FromResult(false);
    public void RegisterTool(string toolName, string description, Func<string, Task<string>> handler) { }
}




