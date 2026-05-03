using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using Aemeath.Core.AI;
using Aemeath.Core.Configuration;
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
    private readonly LongTermMemoryStore _memoryStore;
    private readonly ParticleEffect _particleEffect;
    private readonly ToolConfirmationService? _toolConfirmationService;
    private readonly DispatcherTimer _pendingTimer;
    private readonly DispatcherTimer _flickerTimer;
    private readonly string[] _pendingFrames = [".", "..", "...", "...."];

    private int _pendingFrameIndex;
    private TextBlock? _pendingTextBlock;
    private bool _isSending;
    private double _flickerPhase;

    private readonly Bitmap _assistantAvatar;
    private readonly Bitmap _maleAvatar;
    private readonly Bitmap _femaleAvatar;
    private readonly DrawingImage _copyIcon;
    private readonly DrawingImage _deleteIcon;
    private readonly DrawingImage _retryIcon;
    private readonly DrawingImage _micIcon;
    private readonly DrawingImage _keyboardIcon;
    private readonly List<ChatMessageRecord> _displayMessages = [];
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

        _assistantAvatar = LoadBitmap("avares://Aemeath-agent/Assets/xiaoai-avatar.png");
        _maleAvatar = LoadBitmap("avares://Aemeath-agent/Assets/user-male.png");
        _femaleAvatar = LoadBitmap("avares://Aemeath-agent/Assets/user-female.png");
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
        VoiceButton.Content = new AvaloniaImage { Source = _micIcon, Width = 18, Height = 18, Stretch = Stretch.Uniform };

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

        SendButton.Click += async (_, _) => await SendAsync();
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

        var input = InputBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        _isSending = true;
        UpdateProviderQuickSwitchEnabled();
        var prompt = string.Empty;
        TextBlock? pending = null;
        _confirmationCreatedDuringGeneration = false;
        try
        {
            AppLogger.Info("chat", "send start");
            EnsureCurrentSession();
            var userMessage = new ChatMessageRecord { Role = "user", Content = input, Timestamp = DateTimeOffset.UtcNow };
            _displayMessages.Add(userMessage);
            AddMessageBubble(_displayMessages.Count - 1, isAssistant: false, input, isPending: false);
            _sessionStore.AppendMessage(_currentSessionId, userMessage.Role, userMessage.Content);
            InputBox.Text = string.Empty;

            pending = AddMessageBubble(_displayMessages.Count, isAssistant: true, string.Empty, isPending: true);
            _pendingTextBlock = pending;
            _pendingFrameIndex = 0;
            _pendingTimer.Start();

            var recent = _sessionStore.GetRecentMessages(_currentSessionId, 40);
            prompt = BuildPromptWithRecentContext(recent, input);

            _chatService.ClearHistory();

            var reply = await _chatService.SendMessageAsync(prompt);
            var sanitizedReply = SanitizeAssistantOutput(reply);
            if (ShouldSuppressConfirmationReply(sanitizedReply))
            {
                RenderCurrentMessages();
                RefreshSessionSelector(_currentSessionId);
                AppLogger.Info("chat", "send paused for tool confirmation");
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
            AppLogger.Info("chat", "send completed");
        }
        catch (Exception ex)
        {
            AppLogger.Error("chat", "send failed", ex);
            if (pending is not null && ex.Message.Contains("toolCallId", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var fallback = await _chatService.SendMessageAsync(prompt);
                    pending.Text = string.IsNullOrWhiteSpace(fallback) ? "(无回复)" : FormatToolResultForUser(SanitizeAssistantOutput(fallback));
                    _displayMessages.Add(new ChatMessageRecord { Role = "assistant", Content = pending.Text, Timestamp = DateTimeOffset.UtcNow });
                    _sessionStore.AppendMessage(_currentSessionId, "assistant", pending.Text);
                    RenderCurrentMessages();
                    await UpdateLongTermMemoryIfNeededAsync();
                    return;
                }
                catch (Exception fallbackEx)
                {
                    pending.Text = $"执行失败：{fallbackEx.Message}";
                    _displayMessages.Add(new ChatMessageRecord { Role = "assistant", Content = pending.Text, Timestamp = DateTimeOffset.UtcNow });
                    _sessionStore.AppendMessage(_currentSessionId, "assistant", pending.Text);
                    RenderCurrentMessages();
                    return;
                }
            }

            if (pending is not null)
            {
                pending.Text = $"执行失败：{ex.Message}";
                _displayMessages.Add(new ChatMessageRecord { Role = "assistant", Content = pending.Text, Timestamp = DateTimeOffset.UtcNow });
                _sessionStore.AppendMessage(_currentSessionId, "assistant", pending.Text);
                RenderCurrentMessages();
            }
            else
            {
                var errorText = $"执行失败：{ex.Message}";
                _displayMessages.Add(new ChatMessageRecord { Role = "assistant", Content = errorText, Timestamp = DateTimeOffset.UtcNow });
                _sessionStore.AppendMessage(_currentSessionId, "assistant", errorText);
                RenderCurrentMessages();
            }
        }
        finally
        {
            _pendingTimer.Stop();
            _pendingTextBlock = null;
            _isSending = false;
            UpdateProviderQuickSwitchEnabled();
        }
    }

    private void InputBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        _ = box;
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
                return;
            }

            var text = await speechService.StopCaptureAndRecognizeAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                await SendVoiceTextAsync(text.Trim());
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("chat", "voice capture stop failed", ex);
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
    }

    private async Task SendVoiceTextAsync(string text)
    {
        if (_isSending || string.IsNullOrWhiteSpace(text)) return;

        _isSending = true;
        UpdateProviderQuickSwitchEnabled();
        var prompt = string.Empty;
        TextBlock? pending = null;
        _confirmationCreatedDuringGeneration = false;
        try
        {
            AppLogger.Info("chat", "voice send start");
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

            var reply = await _chatService.SendMessageAsync(prompt);
            var sanitizedReply = SanitizeAssistantOutput(reply);
            if (ShouldSuppressConfirmationReply(sanitizedReply))
            {
                RenderCurrentMessages();
                RefreshSessionSelector(_currentSessionId);
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
        }
        finally
        {
            _pendingTimer.Stop();
            _pendingTextBlock = null;
            _isSending = false;
            UpdateProviderQuickSwitchEnabled();
        }
    }

    public async Task HandleWakeWordAsync()
    {
        if (_isSending)
        {
            return;
        }

        var wasVoiceMode = _isVoiceMode;
        if (!wasVoiceMode)
        {
            ToggleVoiceMode();
        }

        VoiceRecordButton.Content = "正在聆听...";

        try
        {
            using var speechService = new SpeechService();
            var text = await speechService.RecognizeSpeechAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                await SendVoiceTextAsync(text.Trim());
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("wakeword", "wake word follow-up speech failed", ex);
        }
        finally
        {
            VoiceRecordButton.Content = "长按录音";
            if (!wasVoiceMode && _isVoiceMode)
            {
                ToggleVoiceMode();
            }
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
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 4)
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
            Margin = isAssistant ? new Thickness(0, 6, 8, 0) : new Thickness(8, 6, 0, 0),
            Clip = new EllipseGeometry(new Rect(0, 0, 38, 38))
        };

        var bubble = new Border
        {
            MaxWidth = bubbleMax,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10),
            Background = isAssistant ? new SolidColorBrush(Color.Parse("#503F5E9B")) : new SolidColorBrush(Color.Parse("#5060B8F7")),
            BorderBrush = isAssistant ? new SolidColorBrush(Color.Parse("#88C2D2FF")) : new SolidColorBrush(Color.Parse("#88D3E4FF")),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = isAssistant ? HorizontalAlignment.Left : HorizontalAlignment.Right
        };

        var textBlock = new TextBlock
        {
            Text = isPending ? "..." : text,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            FontSize = 15
        };

        bubble.Child = textBlock;
        if (!isPending && messageIndex >= 0)
        {
            bubble.ContextMenu = BuildMessageContextMenu(messageIndex, isAssistant);
        }

        if (isAssistant)
        {
            row.Children.Add(avatar);
            row.Children.Add(bubble);
            Grid.SetColumn(avatar, 0);
            Grid.SetColumn(bubble, 1);
        }
        else
        {
            row.Children.Add(bubble);
            row.Children.Add(avatar);
            Grid.SetColumn(bubble, 0);
            Grid.SetColumn(avatar, 1);
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
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14),
            Margin = new Thickness(46, 2, 12, 8),
            Background = new SolidColorBrush(Color.Parse("#3A2A1F35")),
            BorderBrush = new SolidColorBrush(Color.Parse("#AAFFE07A")),
            BorderThickness = new Thickness(1),
            MaxWidth = ChatScrollViewer.Bounds.Width > 0
                ? Math.Max(260, ChatScrollViewer.Bounds.Width * 0.72)
                : 520
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "高风险操作需要确认",
            Foreground = new SolidColorBrush(Color.Parse("#FFEAB0")),
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            FontSize = 15
        });
        panel.Children.Add(new TextBlock
        {
            Text = action.Title,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14
        });
        panel.Children.Add(new TextBlock
        {
            Text = action.Description,
            Foreground = new SolidColorBrush(Color.Parse("#D7E2FF")),
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
            Background = new SolidColorBrush(Color.Parse("#FFE07A")),
            Foreground = new SolidColorBrush(Color.Parse("#07101E")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FFF1AF")),
            CornerRadius = new CornerRadius(8),
            MinWidth = 92
        };
        confirmButton.Click += (_, _) => ResolvePendingToolAction(action.Id, confirm: true);

        var cancelButton = new Button
        {
            Content = "取消",
            Background = new SolidColorBrush(Color.Parse("#23314D")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#3A4C75")),
            CornerRadius = new CornerRadius(8),
            MinWidth = 82
        };
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
    }

    private Button BuildActionButton(IImage icon, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Content = new AvaloniaImage { Source = icon, Width = 16, Height = 16, Stretch = Stretch.Uniform },
            Width = 36,
            Height = 30,
            Padding = new Thickness(6, 4),
            Background = new SolidColorBrush(Color.Parse("#2A324C77")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#6696B4DD")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private Button BuildActionButton(IImage icon, string tooltip, Func<Task> onClick)
    {
        var button = new Button
        {
            Content = new AvaloniaImage { Source = icon, Width = 16, Height = 16, Stretch = Stretch.Uniform },
            Width = 36,
            Height = 30,
            Padding = new Thickness(6, 4),
            Background = new SolidColorBrush(Color.Parse("#2A324C77")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#6696B4DD")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        ToolTip.SetTip(button, tooltip);
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
        UpdateProviderQuickSwitchEnabled();
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
            var reply = await _chatService.SendMessageAsync(prompt);
            var sanitized = SanitizeAssistantOutput(reply);
            if (ShouldSuppressConfirmationReply(sanitized))
            {
                RenderCurrentMessages();
                return;
            }

            var cleaned = string.IsNullOrWhiteSpace(sanitized) ? "(无回复)" : FormatToolResultForUser(sanitized);
            _displayMessages.Add(new ChatMessageRecord { Role = "assistant", Content = cleaned, Timestamp = DateTimeOffset.UtcNow });
            PersistCurrentMessages();
            RenderCurrentMessages();
            await UpdateLongTermMemoryIfNeededAsync();
        }
        catch (Exception ex)
        {
            _displayMessages.Add(new ChatMessageRecord { Role = "assistant", Content = $"执行失败：{ex.Message}", Timestamp = DateTimeOffset.UtcNow });
            PersistCurrentMessages();
            RenderCurrentMessages();
        }
        finally
        {
            _pendingTimer.Stop();
            _pendingTextBlock = null;
            _isSending = false;
            UpdateProviderQuickSwitchEnabled();
        }
    }

    private void RefreshProviderQuickSwitch(string? selectedProvider = null, string? selectedModel = null)
    {
        _isLoadingProviderSwitch = true;
        try
        {
            ProviderQuickSwitchBox.Items.Clear();
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
        ModelQuickSwitchBox.Items.Clear();
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
            ProviderSwitchStatusText.Text = "正在切换提供商...";
            UpdateProviderQuickSwitchEnabled(forceDisabled: true);

            if (string.Equals(SettingsService.NormalizeProviderName(provider), SettingsService.NormalizeProviderName(oldProvider), StringComparison.OrdinalIgnoreCase))
            {
                ProviderSwitchStatusText.Text = string.Empty;
                return;
            }

            if (_settingsService.GetApiKeyInfo(provider) is null)
            {
                ProviderSwitchStatusText.Text = "切换失败：未找到这个提供商。";
                RefreshProviderQuickSwitch(oldProvider, oldModel);
                return;
            }

            var switched = _settingsService.SwitchCurrentProvider(provider);
            if (!switched)
            {
                ProviderSwitchStatusText.Text = "切换失败：配置没有保存成功。";
                RefreshProviderQuickSwitch(oldProvider, oldModel);
                return;
            }

            var activeProvider = _settingsService.Current.CurrentProvider;
            var targetInfo = _settingsService.GetApiKeyInfo(activeProvider);
            var targetModel = targetInfo?.ModelId ?? _settingsService.Current.DefaultModel;
            var ready = TryReloadChatServiceFromSettings(out var error);

            RefreshProviderQuickSwitch(activeProvider, targetModel);
            ProviderSwitchStatusText.Text = ready
                ? $"已切换到 {activeProvider} / {targetModel}"
                : $"已切换到 {activeProvider}，但服务暂时还没准备好：{error ?? "请检查 API Key、Endpoint 和模型配置。"}";
        }
        catch (Exception ex)
        {
            AppLogger.Error("chat", "quick provider switch failed", ex);
            RefreshProviderQuickSwitch();
            ProviderSwitchStatusText.Text = $"切换时遇到问题：{ex.Message}";
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
            ProviderSwitchStatusText.Text = "正在切换模型...";
            UpdateProviderQuickSwitchEnabled(forceDisabled: true);
            if (string.IsNullOrWhiteSpace(model) || string.Equals(model, oldModel, StringComparison.OrdinalIgnoreCase))
            {
                ProviderSwitchStatusText.Text = string.Empty;
                return;
            }

            var switched = _settingsService.SwitchCurrentModel(provider, model);
            if (!switched)
            {
                RefreshModelQuickSwitch(oldModel, provider);
                ProviderSwitchStatusText.Text = "模型切换失败：没有找到这个模型配置。";
                return;
            }

            var ready = TryReloadChatServiceFromSettings(out var error);
            RefreshModelQuickSwitch(model, provider);
            ProviderSwitchStatusText.Text = ready
                ? $"已切换模型：{model}"
                : $"已切换模型：{model}，但服务暂时还没准备好：{error ?? "请检查 API Key、Endpoint 和模型配置。"}";
        }
        catch (Exception ex)
        {
            AppLogger.Error("chat", "quick model switch failed", ex);
            RefreshModelQuickSwitch();
            ProviderSwitchStatusText.Text = $"模型切换时遇到问题：{ex.Message}";
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

    private void ScrollToBottom()
    {
        Dispatcher.UIThread.Post(() =>
        {
            ChatScrollViewer.Offset = new Vector(ChatScrollViewer.Offset.X, double.MaxValue);
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
            try
            {
                return new Bitmap(_settingsService.Current.CustomUserAvatarPath);
            }
            catch
            {
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
                var bitmap = new Bitmap(path);
                ChatBackgroundHost.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill, Opacity = 0.28 };
                return;
            }
            catch
            {
            }
        }

        ChatBackgroundHost.Background = new SolidColorBrush(Color.Parse("#24141D3A"));
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

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.Info("chat", "chat window closed dispose resources");
        _pendingTimer.Stop();
        _flickerTimer.Stop();
        _particleEffect.Stop();
        _holdSpeechService?.Dispose();
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

internal sealed class NoOpChatService : IChatService
{
    public string CurrentAssistantName => "小爱";
    public bool IsProcessing => false;

    public Task<string> SendMessageAsync(string message, CancellationToken cancellationToken = default)
        => Task.FromResult("未配置 AI 服务，请先在设置中填写 API Key。");

    public Task<string> SummarizeAsync(string message, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

    public async IAsyncEnumerable<string> SendMessageStreamingAsync(string message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return "未配置 AI 服务，请先在设置中填写 API Key。";
        await Task.CompletedTask;
    }

    public void ClearHistory() { }
    public Task<bool> SwitchProviderAsync(string providerName, string apiKey, string? endpoint = null) => Task.FromResult(false);
    public void RegisterTool(string toolName, string description, Func<string, Task<string>> handler) { }
}
