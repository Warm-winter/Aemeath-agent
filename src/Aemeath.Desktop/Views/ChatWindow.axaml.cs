using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private const int MaxAttachmentCount = 6;
    private const long MaxAttachmentBytes = 10 * 1024 * 1024;

    private readonly IChatService _chatService;
    private readonly SettingsService _settingsService;
    private readonly ChatSessionStore _sessionStore;
    private readonly LongTermMemoryStore _memoryStore;
    private readonly ParticleEffect _particleEffect;
    private readonly ToolConfirmationService? _toolConfirmationService;
    private readonly DispatcherTimer _pendingTimer;
    private readonly DispatcherTimer _flickerTimer;
    private readonly DispatcherTimer _statusHideTimer;
    private readonly string[] _pendingFrames = ["星点同步中", "星点同步中.", "星点同步中..", "星点同步中..."];

    private int _pendingFrameIndex;
    private TextBlock? _pendingTextBlock;
    private bool _isSending;
    private bool _scrollPending;
    private double _flickerPhase;

    private readonly Bitmap _assistantAvatar;
    private readonly Bitmap _maleAvatar;
    private readonly Bitmap _femaleAvatar;
    private Bitmap? _customUserAvatar;
    private string? _customUserAvatarPath;
    private Bitmap? _chatBackgroundBitmap;
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
    private readonly List<ChatAttachment> _pendingAttachments = [];
    private readonly Dictionary<string, PendingToolAction> _pendingToolActions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan MinimumVoiceCaptureDuration = TimeSpan.FromMilliseconds(500);
    private readonly SemaphoreSlim _voiceCaptureLock = new(1, 1);
    private readonly SemaphoreSlim _memorySummaryLock = new(1, 1);
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

    public ChatWindow() : this(new NoOpChatService(), new SettingsService())
    {
    }

    public ChatWindow(IChatService chatService, SettingsService settingsService)
    {
        InitializeComponent();
        AppLogger.Info("chat", "chat window constructor start");
        _chatService = chatService;
        _settingsService = settingsService;
        _sessionStore = new ChatSessionStore();
        _memoryStore = new LongTermMemoryStore();
        _particleEffect = new ParticleEffect(BackgroundParticleCanvas);
        if (_chatService is AemiChatService aemiChatService)
        {
            _toolConfirmationService = aemiChatService.ToolConfirmationService;
            _toolConfirmationService.PendingActionCreated += OnPendingToolActionCreated;
        }

        _assistantAvatar = LoadBitmap("avares://Aemeath-agent/Assets/static/xiaoai-avatar.png");
        _maleAvatar = LoadBitmap("avares://Aemeath-agent/Assets/static/user-male.png");
        _femaleAvatar = LoadBitmap("avares://Aemeath-agent/Assets/static/user-female.png");
        // Copy icon (two overlapping rectangles)
        _copyIcon = CreateVectorIcon(
            "M4 2 C2.895 2 2 2.895 2 4 L2 14 L4 14 L4 4 L14 4 L14 2 Z " +
            "M8 6 C6.895 6 6 6.895 6 8 L6 18 C6 19.105 6.895 20 8 20 L18 20 C19.105 20 20 19.105 20 18 L20 8 C20 6.895 19.105 6 18 6 Z " +
            "M8 8 L18 8 L18 18 L8 18 Z",
            22, 22);
        // Delete / trash icon
        _deleteIcon = CreateVectorIcon(
            "M9 3 L9 4 L4 4 L4 6 L5 6 L5 20 C5 21.105 5.895 22 7 22 L17 22 C18.105 22 19 21.105 19 20 L19 6 L20 6 L20 4 L15 4 L15 3 Z " +
            "M7 6 L17 6 L17 20 L7 20 Z " +
            "M9 8 L9 18 L11 18 L11 8 Z " +
            "M13 8 L13 18 L15 18 L15 8 Z",
            24, 24);
        // Retry / refresh icon (circular arrow)
        _retryIcon = CreateVectorIcon(
            "M17.65 6.35 C16.2 4.9 14.21 4 12 4 C7.58 4 4.01 7.58 4.01 12 C4.01 16.42 7.58 20 12 20 C15.73 20 18.84 17.45 19.73 14 L17.65 14 " +
            "C16.83 16.33 14.61 18 12 18 C8.69 18 6 15.31 6 12 C6 8.69 8.69 6 12 6 C13.66 6 15.14 6.69 16.22 7.78 L13 11 L20 11 L20 4 Z",
            24, 24);
        // Microphone icon
        _micIcon = CreateVectorIcon(
            "M12 14 C13.66 14 14.99 12.66 14.99 11 L15 5 C15 3.34 13.66 2 12 2 C10.34 2 9 3.34 9 5 L9 11 C9 12.66 10.34 14 12 14 Z " +
            "M17.3 11 C17.3 14 14.76 16.1 12 16.1 C9.24 16.1 6.7 14 6.7 11 L5 11 C5 14.41 7.72 17.23 11 17.72 L11 21 L13 21 L13 17.72 " +
            "C16.28 17.23 19 14.41 19 11 Z",
            24, 24);
        // Keyboard icon
        _keyboardIcon = CreateVectorIcon(
            "M2 6 C2 4.9 2.9 4 4 4 L20 4 C21.1 4 22 4.9 22 6 L22 18 C22 19.1 21.1 20 20 20 L4 20 C2.9 20 2 19.1 2 18 Z " +
            "M4 6 L4 18 L20 18 L20 6 Z " +
            "M5 8 L7 8 L7 10 L5 10 Z M8 8 L10 8 L10 10 L8 10 Z M11 8 L13 8 L13 10 L11 10 Z M14 8 L16 8 L16 10 L14 10 Z M17 8 L19 8 L19 10 L17 10 Z " +
            "M5 11 L7 11 L7 13 L5 13 Z M8 11 L10 11 L10 13 L8 13 Z M11 11 L13 11 L13 13 L11 13 Z M14 11 L16 11 L16 13 L14 13 Z M17 11 L19 11 L19 13 L17 13 Z " +
            "M7 14 L17 14 L17 16 L7 16 Z",
            24, 24);
        _uploadIcon = CreateVectorIcon(
            "M3830 5115 l0 -955 -965 0 -965 0 0 -85 0 -85 965 0 965 0 0 -965 0 -965 85 0 85 0 2 963 3 962 958 3 957 2 0 85 0 85 -960 0 -960 0 0 955 0 955 -85 0 -85 0 0 -955z",
            800, 800);
        _wrenchIcon = CreateSvgTransformedVectorIcon(
            "M4495 6174 c-530 -89 -947 -486 -1061 -1010 -12 -55 -17 -126 -17 -234 -1 -174 15 -278 63 -413 l29 -82 -222 -225 c-122 -124 -427 -432 -677 -685 -660 -665 -684 -692 -725 -775 -48 -102 -61 -187 -42 -288 20 -104 65 -192 139 -268 166 -173 426 -204 628 -74 44 28 570 551 1333 1326 l277 281 93 -27 c129 -39 264 -54 416 -47 333 14 620 139 853 372 192 191 304 399 353 657 21 108 21 326 1 440 -17 90 -58 234 -82 280 -17 34 -62 58 -106 58 -29 0 -65 -32 -375 -342 l-342 -341 -103 22 c-57 12 -152 32 -212 44 l-109 22 -38 160 c-21 88 -41 178 -44 200 -7 46 -47 1 442 496 230 233 243 248 243 283 0 54 -15 75 -73 100 -170 74 -442 104 -642 70z",
            800, 800);
        _imageIcon = CreateVectorIcon(
            "M1953 5440 c-12 -5 -26 -18 -32 -29 -8 -14 -11 -424 -11 -1339 l0 -1319 23 -21 23 -22 1960 0 1961 0 21 23 22 23 0 1321 c0 1286 -1 1321 -19 1344 l-19 24 -1954 2 c-1113 1 -1962 -2 -1975 -7z m3795 -1362 l-3 -1193 -1830 0 -1830 0 -3 161 -2 161 87 79 c91 82 365 327 395 351 17 15 37 0 473 -355 82 -67 152 -121 157 -120 6 3 584 506 1051 917 l138 122 117 -108 c158 -145 962 -857 970 -860 4 -2 36 24 71 55 l63 58 -73 66 c-41 36 -265 236 -499 444 -234 208 -474 422 -533 476 -60 54 -112 98 -116 98 -4 0 -34 -24 -67 -53 -101 -92 -1106 -971 -1120 -982 -10 -7 -22 -1 -46 21 -26 24 -285 238 -549 452 l-27 22 -119 -108 c-65 -60 -174 -158 -243 -219 l-125 -111 -3 897 c-1 493 0 902 3 909 3 9 380 12 1835 12 l1830 0 -2 -1192z",
            800, 800);
        _fileIcon = CreateVectorIcon(
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

        SendButton.Click += async (_, _) => await SendAsync();
        UploadButton.Click += (_, _) => ShowUploadMenu();
        McpToolsButton.Click += (_, _) => ShowMcpToolsMenu();
        VoiceButton.Click += (_, _) => ToggleVoiceMode();
        VoiceRecordButton.AddHandler(PointerPressedEvent, VoiceRecordButton_OnPointerPressed, RoutingStrategies.Tunnel, true);
        VoiceRecordButton.AddHandler(PointerReleasedEvent, VoiceRecordButton_OnPointerReleased, RoutingStrategies.Tunnel, true);
        VoiceRecordButton.AddHandler(PointerCaptureLostEvent, VoiceRecordButton_OnPointerCaptureLost, RoutingStrategies.Tunnel, true);
        NewSessionButton.Click += (_, _) => StartNewSession();
        DeleteSessionButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_currentSessionId))
            {
                return;
            }

            _sessionStore.DeleteSession(_currentSessionId);
            _memoryStore.ClearSession(_currentSessionId);
            LoadLatestSessionOrEmpty();
        };

        ProviderQuickSwitchBox.SelectionChanged += async (_, _) => await SwitchQuickProviderAsync();
        ModelQuickSwitchBox.SelectionChanged += async (_, _) => await SwitchQuickModelAsync();

        SessionSelector.SelectionChanged += (_, _) =>
        {
            if (SessionSelector.SelectedItem is ComboBoxItem { Tag: string id } && !string.IsNullOrWhiteSpace(id))
            {
                LoadSession(id);
            }
        };

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

        Opened += (_, _) =>
        {
            AppLogger.Info("chat", "chat window opened");
            ApplyChatBackgroundImage();
            _flickerTimer.Start();
            if (_settingsService.Current.EnableParticleEffects)
            {
                _particleEffect.Start(120);
            }

            LoadLatestSessionOrCreateIfEmpty();
            RefreshProviderQuickSwitch();
        };
    }

    private async Task SendAsync()
    {
        if (_isSending)
        {
            return;
        }

        var input = InputBox.Text?.Trim() ?? string.Empty;
        var attachments = _pendingAttachments.ToList();
        if (string.IsNullOrWhiteSpace(input) && attachments.Count == 0)
        {
            return;
        }

        _isSending = true;
        RaiseActivityChanged(ChatActivityKind.Sending);
        UpdateProviderQuickSwitchEnabled();
        var prompt = string.Empty;
        TextBlock? pending = null;
        _confirmationCreatedDuringGeneration = false;
        try
        {
            AppLogger.Info("chat", "send start");
            PauseAmbientFlicker();
            EnsureCurrentSession();
            var userInput = string.IsNullOrWhiteSpace(input) ? "\u8bf7\u5206\u6790\u6211\u4e0a\u4f20\u7684\u9644\u4ef6\u3002" : input;
            var visibleUserContent = BuildVisibleUserContent(userInput, attachments);
            var userMessage = new ChatMessageRecord { Role = "user", Content = visibleUserContent, Timestamp = DateTimeOffset.UtcNow };
            _displayMessages.Add(userMessage);
            AddMessageBubble(_displayMessages.Count - 1, isAssistant: false, visibleUserContent, isPending: false);
            _sessionStore.AppendMessage(_currentSessionId, userMessage.Role, userMessage.Content);
            InputBox.Text = string.Empty;

            pending = AddMessageBubble(_displayMessages.Count, isAssistant: true, string.Empty, isPending: true);
            _pendingTextBlock = pending;
            _pendingFrameIndex = 0;
            _pendingTimer.Start();

            var recent = _sessionStore.GetRecentMessages(_currentSessionId, 40);
            prompt = BuildPromptWithRecentContext(recent, userInput);

            _chatService.ClearHistory();

            var reply = await StreamReplyIntoAsync(prompt, pending, attachments);
            var sanitizedReply = SanitizeAssistantOutput(reply);
            if (ShouldSuppressConfirmationReply(sanitizedReply))
            {
                RenderCurrentMessages();
                RefreshSessionSelector(_currentSessionId);
                ClearPendingAttachments();
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
            ClearPendingAttachments();
            await UpdateLongTermMemoryIfNeededAsync();
            RaiseActivityChanged(ChatActivityKind.Completed);
            AppLogger.Info("chat", "send completed");
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
            RaiseActivityChanged(ChatActivityKind.Failed);
        }
        finally
        {
            _pendingTimer.Stop();
            _pendingTextBlock = null;
            _isSending = false;
            ResumeAmbientFlicker();
            UpdateProviderQuickSwitchEnabled();
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
            FileTypeFilter = BuildAttachmentFileTypes(imagesOnly)
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

            if (_pendingAttachments.Count >= MaxAttachmentCount)
            {
                notices.Add($"\u6700\u591a\u4e00\u6b21\u9644\u52a0 {MaxAttachmentCount} \u4e2a\u6587\u4ef6\u3002");
                break;
            }

            if (_pendingAttachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var attachment = TryCreateAttachment(path, out var error);
            if (attachment is null)
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    notices.Add(error);
                }

                continue;
            }

            _pendingAttachments.Add(attachment);
        }

        RenderAttachmentChips();
        var message = notices.Count > 0
            ? string.Join(" ", notices)
            : _pendingAttachments.Count > 0
                ? $"\u5df2\u9644\u52a0 {_pendingAttachments.Count} \u4e2a\u6587\u4ef6\u3002"
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

        var servers = _mcpServerStore.ListServers();
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
    private static IReadOnlyList<FilePickerFileType> BuildAttachmentFileTypes(bool imagesOnly)
    {
        var imageType = new FilePickerFileType("图片")
        {
            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp"],
            MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/gif", "image/bmp"]
        };

        if (imagesOnly)
        {
            return [imageType];
        }

        return
        [
            new FilePickerFileType("文本与代码")
            {
                Patterns = ["*.txt", "*.md", "*.markdown", "*.cs", "*.json", "*.xml", "*.xaml", "*.axaml", "*.yaml", "*.yml", "*.log", "*.csv", "*.tsv", "*.html", "*.css", "*.js", "*.ts", "*.py", "*.ps1", "*.bat"],
                MimeTypes = ["text/plain", "text/markdown", "application/json", "application/xml", "text/csv", "text/html", "text/css", "text/javascript"]
            },
            FilePickerFileTypes.All
        ];
    }
    private ChatAttachment? TryCreateAttachment(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = "\u6587\u4ef6\u4e0d\u5b58\u5728\uff1a" + Path.GetFileName(path);
                return null;
            }

            var info = new FileInfo(path);
            if (info.Length > MaxAttachmentBytes)
            {
                error = $"\u6587\u4ef6\u8fc7\u5927\uff1a{info.Name}\uff0c\u5355\u4e2a\u9644\u4ef6\u6700\u5927 {FormatBytes(MaxAttachmentBytes)}\u3002";
                return null;
            }

            var extension = info.Extension.ToLowerInvariant();
            var kind = GetAttachmentKind(extension);
            return new ChatAttachment(
                info.FullName,
                info.Name,
                GetMimeType(extension, kind),
                kind,
                info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = "\u65e0\u6cd5\u8bfb\u53d6\u6587\u4ef6\uff1a" + ex.Message;
            return null;
        }
    }

    private void RenderAttachmentChips()
    {
        AttachmentPanel.Children.Clear();
        AttachmentPanel.IsVisible = _pendingAttachments.Count > 0;

        foreach (var attachment in _pendingAttachments.ToList())
        {
            var removeButton = new Button
            {
                Content = "x",
                Width = 28,
                Height = 28,
                MinWidth = 28,
                MinHeight = 28,
                Padding = new Thickness(0),
                FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            removeButton.Classes.Add("ghost");
            ToolTip.SetTip(removeButton, "\u79fb\u9664\u9644\u4ef6");
            removeButton.Click += (_, _) =>
            {
                _pendingAttachments.Remove(attachment);
                RenderAttachmentChips();
            };

            var label = new TextBlock
            {
                Text = $"{GetAttachmentKindLabel(attachment.Kind)} {attachment.Name} ({FormatBytes(attachment.SizeBytes)})",
                Foreground = Brushes.White,
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
                Background = new SolidColorBrush(Color.Parse("#1A73C7FF")),
                BorderBrush = new SolidColorBrush(Color.Parse("#6673C7FF")),
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
        _pendingAttachments.Clear();
        RenderAttachmentChips();
    }

    private static string BuildVisibleUserContent(string userInput, IReadOnlyList<ChatAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return userInput;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(userInput);
        sb.AppendLine();
        sb.AppendLine("\u9644\u4ef6\uff1a");
        foreach (var attachment in attachments)
        {
            sb.AppendLine($"- {attachment.Name} ({GetAttachmentKindLabel(attachment.Kind)}, {FormatBytes(attachment.SizeBytes)})");
        }

        return sb.ToString().TrimEnd();
    }

    private static ChatAttachmentKind GetAttachmentKind(string extension)
    {
        if (extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp")
        {
            return ChatAttachmentKind.Image;
        }

        return extension is ".txt" or ".md" or ".markdown" or ".cs" or ".json" or ".xml" or ".xaml" or ".axaml" or ".yaml" or ".yml" or ".log" or ".csv" or ".tsv" or ".html" or ".css" or ".js" or ".ts" or ".py" or ".ps1" or ".bat"
            ? ChatAttachmentKind.Text
            : ChatAttachmentKind.Other;
    }

    private static string GetMimeType(string extension, ChatAttachmentKind kind)
    {
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".json" => "application/json",
            ".xml" or ".xaml" or ".axaml" => "application/xml",
            ".csv" => "text/csv",
            ".tsv" => "text/tab-separated-values",
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "text/javascript",
            _ => kind == ChatAttachmentKind.Text ? "text/plain" : "application/octet-stream"
        };
    }

    private static string GetAttachmentKindLabel(ChatAttachmentKind kind)
    {
        return kind switch
        {
            ChatAttachmentKind.Image => "\u56fe\u7247",
            ChatAttachmentKind.Text => "\u6587\u672c",
            _ => "\u6587\u4ef6"
        };
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.##} MB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:0.##} KB" : $"{bytes} B";
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
                RaiseActivityChanged(ChatActivityKind.Idle);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("chat", "voice capture stop failed", ex);
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
    }

    private async Task SendVoiceTextAsync(string text)
    {
        if (_isSending || string.IsNullOrWhiteSpace(text)) return;

        _isSending = true;
        RaiseActivityChanged(ChatActivityKind.Sending);
        UpdateProviderQuickSwitchEnabled();
        var prompt = string.Empty;
        TextBlock? pending = null;
        _confirmationCreatedDuringGeneration = false;
        try
        {
            AppLogger.Info("chat", "voice send start");
            PauseAmbientFlicker();
            EnsureCurrentSession();
            var userMessage = new ChatMessageRecord { Role = "user", Content = text, Timestamp = DateTimeOffset.UtcNow };
            _displayMessages.Add(userMessage);
            AddMessageBubble(_displayMessages.Count - 1, isAssistant: false, text, isPending: false);
            _sessionStore.AppendMessage(_currentSessionId, userMessage.Role, userMessage.Content);

            pending = AddMessageBubble(_displayMessages.Count, isAssistant: true, string.Empty, isPending: true);
            _pendingTextBlock = pending;
            _pendingFrameIndex = 0;
            _pendingTimer.Start();

            var recent = _sessionStore.GetRecentMessages(_currentSessionId, 40);
            prompt = BuildPromptWithRecentContext(recent, text);

            _chatService.ClearHistory();

            var reply = await StreamReplyIntoAsync(prompt, pending);
            var sanitizedReply = SanitizeAssistantOutput(reply);
            if (ShouldSuppressConfirmationReply(sanitizedReply))
            {
                RenderCurrentMessages();
                RefreshSessionSelector(_currentSessionId);
                RaiseActivityChanged(ChatActivityKind.ToolWaiting);
                AppLogger.Info("chat", "voice send paused for tool confirmation");
                return;
            }

            pending.Text = string.IsNullOrWhiteSpace(sanitizedReply) ? "(无回复)" : sanitizedReply;
            ScrollToBottom();

            var assistantReply = pending.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(assistantReply))
            {
                assistantReply = "(无回复)";
                pending.Text = assistantReply;
            }

            assistantReply = FormatToolResultForUser(assistantReply);
            pending.Text = assistantReply;
            _displayMessages.Add(new ChatMessageRecord { Role = "assistant", Content = assistantReply, Timestamp = DateTimeOffset.UtcNow });
            _sessionStore.AppendMessage(_currentSessionId, "assistant", assistantReply);
            RenderCurrentMessages();
            RefreshSessionSelector(_currentSessionId);
            await UpdateLongTermMemoryIfNeededAsync();
            RaiseActivityChanged(ChatActivityKind.Completed);
            AppLogger.Info("chat", "voice send completed");
        }
        catch (Exception ex)
        {
            AppLogger.Error("chat", "voice send failed", ex);
            if (pending is not null)
            {
                pending.Text = $"执行失败：{ex.Message}";
                _displayMessages.Add(new ChatMessageRecord { Role = "assistant", Content = pending.Text, Timestamp = DateTimeOffset.UtcNow });
                _sessionStore.AppendMessage(_currentSessionId, "assistant", pending.Text);
                RenderCurrentMessages();
            }
            RaiseActivityChanged(ChatActivityKind.Failed);
        }
        finally
        {
            _pendingTimer.Stop();
            _pendingTextBlock = null;
            _isSending = false;
            ResumeAmbientFlicker();
            UpdateProviderQuickSwitchEnabled();
        }
    }

    private string BuildPromptWithRecentContext(IReadOnlyList<ChatMessageRecord> recentMessages, string userInput)
    {
        var rounds = recentMessages.TakeLast(40).ToList();
        var sb = new System.Text.StringBuilder();
        var longTermMemory = _memoryStore.BuildPromptBlock(_currentSessionId);
        if (!string.IsNullOrWhiteSpace(longTermMemory))
        {
            sb.AppendLine("【长期记忆】");
            sb.AppendLine("以下是本地保存的长期记忆，只用于保持连续性。不要主动提到记忆文件或内部字段。");
            sb.AppendLine(longTermMemory);
            sb.AppendLine();
        }
        sb.AppendLine("以下是最近20轮对话上下文，请结合上下文继续：");
        foreach (var m in rounds)
        {
            var role = m.Role == "assistant" ? "小爱" : "你";
            sb.Append(role).Append("：").AppendLine(m.Content);
        }

        sb.AppendLine("你：" + userInput);
        sb.AppendLine("要求：如果用户要求执行电脑操作，请优先调用可用工具并给出执行反馈。反馈要像日常对话，不要展示确认编号、插件名、函数名、命令细节、可执行文件名或长串内部 ID；除非用户明确询问技术细节。只输出纯文本，不要 Markdown。");
        return sb.ToString();
    }

    private async Task UpdateLongTermMemoryIfNeededAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentSessionId))
        {
            return;
        }

        if (!await _memorySummaryLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var session = _sessionStore.GetSession(_currentSessionId);
            if (session is null)
            {
                return;
            }

            var completedRounds = CountCompletedRounds(session.Messages);
            var summarizedRounds = _memoryStore.GetSummarizedRounds(_currentSessionId);
            if (completedRounds - summarizedRounds < 5)
            {
                return;
            }

            var prompt = BuildMemorySummaryPrompt(session.Messages, summarizedRounds, completedRounds);
            string rawSummary;
            try
            {
                rawSummary = await _chatService.SummarizeAsync(prompt);
            }
            catch (Exception ex)
            {
                AppLogger.Error("memory", "AI memory summary failed, using fallback", ex);
                rawSummary = BuildFallbackMemorySummary(session.Messages);
            }

            var parsed = ParseMemorySummary(rawSummary);
            if (string.IsNullOrWhiteSpace(parsed.Summary))
            {
                parsed = parsed with { Summary = BuildFallbackMemorySummary(session.Messages) };
            }

            _memoryStore.SaveSummary(
                _currentSessionId,
                completedRounds,
                parsed.Summary,
                parsed.Facts,
                parsed.OpenThreads,
                parsed.Preferences);
            AppLogger.Info("memory", $"long-term memory updated: session={_currentSessionId}, rounds={completedRounds}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("memory", "long-term memory update failed", ex);
        }
        finally
        {
            _memorySummaryLock.Release();
        }
    }

    private string BuildMemorySummaryPrompt(IReadOnlyList<ChatMessageRecord> messages, int summarizedRounds, int completedRounds)
    {
        var recent = messages.TakeLast(20).ToList();
        var existingMemory = _memoryStore.BuildPromptBlock(_currentSessionId, 1200);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("请更新 Aemeath 的本地长期记忆。只输出 JSON，不要 Markdown。");
        sb.AppendLine("JSON 格式：{\"summary\":\"...\",\"preferences\":[\"...\"],\"facts\":[\"...\"],\"openThreads\":[\"...\"]}");
        sb.AppendLine("要求：只保留用户偏好、未完成事项、重要事实和本会话摘要；不要编造；不要写寒暄。");
        sb.AppendLine($"已总结到第 {summarizedRounds} 轮；当前完成第 {completedRounds} 轮。");
        if (!string.IsNullOrWhiteSpace(existingMemory))
        {
            sb.AppendLine("已有长期记忆：");
            sb.AppendLine(existingMemory);
        }

        sb.AppendLine("最近对话：");
        foreach (var message in recent)
        {
            var role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "小爱" : "用户";
            sb.Append(role).Append("：").AppendLine(message.Content);
        }

        return sb.ToString();
    }

    private static int CountCompletedRounds(IReadOnlyList<ChatMessageRecord> messages)
    {
        var users = messages.Count(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        var assistants = messages.Count(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        return Math.Min(users, assistants);
    }

    private static string BuildFallbackMemorySummary(IReadOnlyList<ChatMessageRecord> messages)
    {
        var recent = messages.TakeLast(10)
            .Select(m => $"{(string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "小爱" : "用户")}：{m.Content}");
        var text = string.Join("\n", recent).Trim();
        return text.Length <= 900 ? text : text[..900];
    }

    private static MemorySummaryResult ParseMemorySummary(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new MemorySummaryResult(string.Empty, [], [], []);
        }

        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            return new MemorySummaryResult(raw.Trim(), [], [], []);
        }

        try
        {
            var result = JsonSerializer.Deserialize<MemorySummaryDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return new MemorySummaryResult(
                result?.Summary?.Trim() ?? string.Empty,
                CleanList(result?.Preferences),
                CleanList(result?.Facts),
                CleanList(result?.OpenThreads));
        }
        catch
        {
            return new MemorySummaryResult(raw.Trim(), [], [], []);
        }
    }

    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    private static IReadOnlyList<string> CleanList(IEnumerable<string>? values)
        => values?
               .Where(v => !string.IsNullOrWhiteSpace(v))
               .Select(v => v.Trim())
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .Take(12)
               .ToList()
           ?? [];

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
            Spacing = 7,
            Margin = new Thickness(0, 0, 0, 6)
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(isAssistant ? "Auto,*" : "*,Auto"),
            Margin = new Thickness(0, 2, 0, 2)
        };

        var bubbleMax = ChatScrollViewer.Bounds.Width > 0
            ? Math.Max(220, ChatScrollViewer.Bounds.Width * 0.68)
            : 420;

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
            Background = AemiUi.Brush(isAssistant ? "#FFD1E5" : "#FFE1EE"),
            BorderBrush = AemiUi.Brush(isAssistant ? AemiUi.Star : AemiUi.Halo),
            BorderThickness = new Thickness(1),
            Margin = isAssistant ? new Thickness(0, 4, 10, 0) : new Thickness(10, 4, 0, 0),
            Child = avatar
        };

        var bubble = new Border
        {
            MaxWidth = bubbleMax,
            CornerRadius = new CornerRadius(isAssistant ? 16 : 14),
            Padding = new Thickness(14, 12),
            Background = AemiUi.Brush(isAssistant ? "#FFFFFF" : "#FFF8FB"),
            BorderBrush = AemiUi.Brush(isAssistant ? "#F3C2D4" : "#FF69B4"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = isAssistant ? HorizontalAlignment.Left : HorizontalAlignment.Right
        };

        var content = new StackPanel { Spacing = 7 };
        content.Children.Add(AemiUi.Badge(isAssistant ? "小爱 · Digital Ghost" : "漂泊者 · Local Signal", isAssistant ? "halo" : "pink"));

        var textBlock = new TextBlock
        {
            Text = isPending ? _pendingFrames[0] : text,
            Foreground = AemiUi.Brush(AemiUi.Ghost),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 23,
            FontSize = 15
        };

        content.Children.Add(textBlock);
        bubble.Child = content;
        if (!isPending && messageIndex >= 0)
        {
            bubble.ContextMenu = BuildMessageContextMenu(messageIndex, isAssistant);
        }

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

        if (!isPending && messageIndex >= 0)
        {
            var actions = BuildMessageActions(messageIndex, isAssistant);
            root.Children.Add(actions);
        }

        MessagesPanel.Children.Add(root);
        ScrollToBottom();
        return textBlock;
    }

    private WrapPanel BuildMessageActions(int messageIndex, bool isAssistant)
    {
        var panel = new WrapPanel
        {
            HorizontalAlignment = isAssistant ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Margin = isAssistant ? new Thickness(46, 2, 8, 4) : new Thickness(8, 2, 46, 4)
        };

        panel.Children.Add(BuildActionButton(_copyIcon, "复制", async () => await CopyMessageAsync(messageIndex)));
        if (isAssistant)
        {
            panel.Children.Add(BuildActionButton(_retryIcon, "重新回答", async () => await RegenerateAssistantAsync(messageIndex)));
        }
        panel.Children.Add(BuildActionButton(_deleteIcon, "删除", () => DeleteMessage(messageIndex)));

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
            Background = AemiUi.Brush("#FFF8FB"),
            BorderBrush = AemiUi.Brush("#F3C2D4"),
            BorderThickness = new Thickness(1),
            MaxWidth = ChatScrollViewer.Bounds.Width > 0
                ? Math.Max(260, ChatScrollViewer.Bounds.Width * 0.72)
                : 520
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(AemiUi.Badge("高风险工具确认 · Manual Gate", "star"));
        panel.Children.Add(new TextBlock
        {
            Text = "高风险操作需要确认",
            Foreground = new SolidColorBrush(Color.Parse("#E84D8E")),
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

        var confirmButton = new Button
        {
            Content = "确认执行",
            Background = new SolidColorBrush(Color.Parse("#FF69B4")),
            Foreground = new SolidColorBrush(Color.Parse("#07101E")),
            BorderBrush = new SolidColorBrush(Color.Parse("#E84D8E")),
            CornerRadius = new CornerRadius(8),
            MinWidth = 92
        };
        confirmButton.Content = "确认执行";
        confirmButton.Classes.Add("primary");
        confirmButton.Click += (_, _) => ResolvePendingToolAction(action.Id, confirm: true);

        var cancelButton = new Button
        {
            Content = "取消",
            Background = new SolidColorBrush(Color.Parse("#FFE1EE")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#F3C2D4")),
            CornerRadius = new CornerRadius(8),
            MinWidth = 82
        };
        cancelButton.Content = "取消";
        cancelButton.Classes.Add("ghost");
        cancelButton.Click += (_, _) => ResolvePendingToolAction(action.Id, confirm: false);

        buttons.Children.Add(confirmButton);
        buttons.Children.Add(cancelButton);
        panel.Children.Add(buttons);
        root.Child = panel;
        MessagesPanel.Children.Add(root);
    }

    private async void ResolvePendingToolAction(string actionId, bool confirm)
    {
        if (_toolConfirmationService is null)
        {
            return;
        }

        var result = confirm
            ? _toolConfirmationService.Confirm(actionId)
            : _toolConfirmationService.Cancel(actionId);

        _pendingToolActions.Remove(actionId);
        EnsureCurrentSession();
        var userText = FormatToolResultForUser(result);
        _displayMessages.Add(new ChatMessageRecord
        {
            Role = "assistant",
            Content = userText,
            Timestamp = DateTimeOffset.UtcNow
        });
        _sessionStore.AppendMessage(_currentSessionId, "assistant", userText);
        RenderCurrentMessages();
        ScrollToBottom();
        UpdateProviderQuickSwitchEnabled();
        await UpdateLongTermMemoryIfNeededAsync();
        RaiseActivityChanged(_pendingToolActions.Count == 0 ? ChatActivityKind.Completed : ChatActivityKind.ToolWaiting);
    }

    private Button BuildActionButton(IImage icon, string tooltip, Action onClick)
    {
        var button = AemiUi.IconButton(icon, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private Button BuildActionButton(IImage icon, string tooltip, Func<Task> onClick)
    {
        var button = AemiUi.IconButton(icon, tooltip);
        button.Click += async (_, _) => await onClick();
        return button;
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

        await GenerateAssistantReplyForUserAsync(userMessage.Content);
    }

    private async Task GenerateAssistantReplyForUserAsync(string userContent)
    {
        if (_isSending)
        {
            return;
        }

        _isSending = true;
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
            var prompt = BuildPromptWithRecentContext(recent, userContent);
            var reply = await StreamReplyIntoAsync(prompt, pending);
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
            await UpdateLongTermMemoryIfNeededAsync();
            RaiseActivityChanged(ChatActivityKind.Completed);
        }
        catch (Exception ex)
        {
            _displayMessages.Add(new ChatMessageRecord { Role = "assistant", Content = $"执行失败：{ex.Message}", Timestamp = DateTimeOffset.UtcNow });
            PersistCurrentMessages();
            RenderCurrentMessages();
            RaiseActivityChanged(ChatActivityKind.Failed);
        }
        finally
        {
            _pendingTimer.Stop();
            _pendingTextBlock = null;
            _isSending = false;
            ResumeAmbientFlicker();
            UpdateProviderQuickSwitchEnabled();
        }
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

    private void ResumeAmbientFlicker()
    {
        _flickerTimer.Start();
    }

    private void ScrollToBottom()
    {
        if (_scrollPending)
        {
            return;
        }

        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                ChatScrollViewer.Offset = new Vector(ChatScrollViewer.Offset.X, double.MaxValue);
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
            ChatBackgroundHost.Background = new SolidColorBrush(Color.Parse("#FFFFFFFF"));
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

    private static Bitmap LoadBitmap(string uri)
    {
        using var stream = AssetLoader.Open(new Uri(uri));
        return new Bitmap(stream);
    }

    private static DrawingImage CreateVectorIcon(string pathData, double width, double height)
    {
        var geometry = StreamGeometry.Parse(pathData);
        var drawing = new GeometryDrawing
        {
            Geometry = geometry,
            Brush = new SolidColorBrush(Colors.White)
        };
        var group = new DrawingGroup();
        group.Children.Add(drawing);
        return new DrawingImage(group);
    }

    private static DrawingImage CreateSvgTransformedVectorIcon(string pathData, double width, double height)
    {
        var geometry = StreamGeometry.Parse(pathData);
        var drawing = new GeometryDrawing
        {
            Geometry = geometry,
            Brush = new SolidColorBrush(Colors.White)
        };
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(width / 8000d, -height / 8000d));
        transform.Children.Add(new TranslateTransform(0, height));

        var group = new DrawingGroup { Transform = transform };
        group.Children.Add(drawing);
        return new DrawingImage(group);
    }

    private void StartNewSession()
    {
        var session = _sessionStore.CreateSession();
        _currentSessionId = session.Id;
        _displayMessages.Clear();
        MessagesPanel.Children.Clear();
        RefreshSessionSelector(_currentSessionId);
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
            Timestamp = x.Timestamp
        }));
        RenderCurrentMessages();
    }

    private void RefreshSessionSelector(string selectedId)
    {
        var sessions = _sessionStore.ListSessions();
        SessionSelector.Items.Clear();
        foreach (var s in sessions)
        {
            SessionSelector.Items.Add(new ComboBoxItem
            {
                Content = s.Title,
                Tag = s.Id
            });
        }

        var selected = SessionSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), selectedId, StringComparison.Ordinal));
        SessionSelector.SelectedItem = selected;
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
        MessagesPanel.Children.Clear();
        for (var i = 0; i < _displayMessages.Count; i++)
        {
            var message = _displayMessages[i];
            var isAssistant = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase);
            var content = isAssistant ? SanitizeAssistantOutput(message.Content) : message.Content;
            AddMessageBubble(i, isAssistant, content, false);
        }

        RenderPendingToolActions();
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
        RaiseActivityChanged(ChatActivityKind.Idle);
        if (_toolConfirmationService is not null)
        {
            _toolConfirmationService.PendingActionCreated -= OnPendingToolActionCreated;
        }
        _assistantAvatar.Dispose();
        _maleAvatar.Dispose();
        _femaleAvatar.Dispose();
        base.OnClosed(e);
    }
    private sealed record MemorySummaryResult(
        string Summary,
        IReadOnlyList<string> Preferences,
        IReadOnlyList<string> Facts,
        IReadOnlyList<string> OpenThreads);

    private sealed class MemorySummaryDto
    {
        public string? Summary { get; set; }
        public List<string>? Preferences { get; set; }
        public List<string>? Facts { get; set; }
        public List<string>? OpenThreads { get; set; }
    }
}

public sealed class ChatActivityChangedEventArgs(ChatActivityKind kind) : EventArgs
{
    public ChatActivityKind Kind { get; } = kind;
}

public enum ChatActivityKind
{
    Idle,
    Sending,
    VoiceListening,
    ToolWaiting,
    Completed,
    Failed
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

    public Task<string> SummarizeAsync(string message, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

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




