using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Aemeath.Core.AI;
using Aemeath.Core.ComputerControl;
using Aemeath.Core.Configuration;
using Aemeath.Core.MCP;
using Aemeath.Core.Memory;
using Aemeath.Desktop.Services;
using Aemeath.Pet.Effects;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvaloniaColor = Avalonia.Media.Color;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace Aemeath.Desktop.Views;

public partial class ConfigWindow : Window
{
    private sealed record ProviderPreset(string Name, string Provider, string? Endpoint, string ModelId);
    private sealed record MemoryEntry(string Id, string Text, string ScopeLabel);
    private sealed record ProviderFormSnapshot(
        string EditingProvider,
        string Provider,
        string ApiKey,
        string Endpoint,
        string DefaultModel,
        string Models);
    private sealed record ComputerControlFormSnapshot(
        string Backend,
        string VisionProvider,
        string VisionModel,
        string UfoPythonPath);

    private readonly SettingsService _settingsService;
    private readonly IChatService _chatService;
    private readonly Func<string?>? _currentSessionIdProvider;
    private readonly ParticleEffect _particleEffect;
    private readonly ProviderProbeService _providerProbeService = new();
    private readonly McpDependencyService _mcpDependencyService = new();
    private readonly MemoryOrchestrator _memoryOrchestrator;
    private readonly Mem0DependencyService _mem0DependencyService;
    private readonly DispatcherTimer _flickerTimer;
    private readonly DispatcherTimer _simpleSettingsSaveTimer;
    private readonly List<ProviderModel> _currentModelCandidates = new();
    private readonly List<MemoryEntry> _memoryEntries = new();
    private readonly McpServerStore _mcpServerStore;
    private IPageTransition? _fullPageTransition;
    private bool _isLoadingProviderUi;
    private bool _isLoadingSettingsUi;
    private bool _isInitialized;
    private bool _isLoadingComputerControlUi;
    private bool _isProviderBusy;
    private bool _providerFormDirty;
    private bool _computerControlDirty;
    private bool _isApiKeyVisible;
    private bool _isInstallingMcpDependencies;
    private bool _isInstallingMem0;
    private bool _suppressSettingsPageChange;
    private bool _allowWindowClose;
    private bool _isClosingPromptOpen;
    private SettingsPageId _lastSettingsPageId = SettingsPageId.Provider;
    private ProviderFormSnapshot? _providerBaseline;
    private ComputerControlFormSnapshot? _computerControlBaseline;
    private string? _editingProviderName;
    private string? _selectedMemoryEntryId;
    private string _lastMcpDependencySource = "本次尚未下载";
    private double _flickerPhase;

    internal Func<Window, string, string, string, string, Task<UnsavedChangesDecision>> UnsavedChangesHandler { get; set; }
        = static (owner, title, message, discardText, saveText) =>
            DialogService.ChooseUnsavedChangesAsync(owner, title, message, discardText, saveText);

    internal SettingsPageId CurrentPageId => _lastSettingsPageId;

    public ConfigWindow() : this(new SettingsService(), new NoOpChatService(), null, null)
    {
    }

    public ConfigWindow(
        SettingsService settingsService,
        IChatService chatService,
        Func<string?>? currentSessionIdProvider = null)
        : this(settingsService, chatService, currentSessionIdProvider, null)
    {
    }

    internal ConfigWindow(
        SettingsService settingsService,
        IChatService chatService,
        Func<string?>? currentSessionIdProvider,
        McpServerStore? mcpServerStore)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _chatService = chatService;
        _currentSessionIdProvider = currentSessionIdProvider;
        _mcpServerStore = mcpServerStore ?? new McpServerStore();
        _particleEffect = new ParticleEffect(BackgroundParticleCanvas);
        _fullPageTransition = SettingsContentHost.PageTransition;
        _simpleSettingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _simpleSettingsSaveTimer.Tick += (_, _) =>
        {
            _simpleSettingsSaveTimer.Stop();
            SaveSimpleSettings();
        };

        // Mem0 记忆编排器 + 依赖服务。与 ChatWindow 共享同一数据目录，各自维护 Python 子进程。
        if (chatService is AemiChatService aemiChatService)
        {
            _memoryOrchestrator = new MemoryOrchestrator(
                () => aemiChatService.BuildMem0Config(),
                () => settingsService.Current.Mem0PythonPath);
        }
        else
        {
            _memoryOrchestrator = new MemoryOrchestrator(() => null, () => null);
        }

        _mem0DependencyService = new Mem0DependencyService(
            !string.IsNullOrWhiteSpace(settingsService.Current.UvExecutablePath) && File.Exists(settingsService.Current.UvExecutablePath)
                ? settingsService.Current.UvExecutablePath!
                : McpDependencyService.DefaultBinDirectory + "\\uv.exe",
            EnsureUvAsync);

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

        NewProviderButton.Click += async (_, _) => await BeginNewProviderAsync();
        SaveProviderButton.Click += async (_, _) => await SaveProviderAsync();
        CancelProviderButton.Click += (_, _) => CancelProviderChanges();
        TestProviderButton.Click += async (_, _) => await TestProviderAsync();
        FetchModelsButton.Click += async (_, _) => await FetchModelsAsync();
        AddManualModelButton.Click += (_, _) => AddManualModel();
        ToggleApiKeyButton.Click += (_, _) => ToggleApiKeyVisibility();
        ProviderPresetBox.SelectionChanged += (_, _) => ApplySelectedProviderPreset();
        ProviderNameBox.TextChanged += (_, _) => MarkProviderDirty();
        ApiKeyBox.TextChanged += (_, _) => MarkProviderDirty();
        EndpointBox.TextChanged += (_, _) => MarkProviderDirty();
        DefaultModelBox.SelectionChanged += (_, _) => MarkProviderDirty();
        ManualModelBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                AddManualModel();
            }
        };

        InitMcpPanel();
        InitSkillPanel();

        BrowseAvatarButton.Click += async (_, _) => await PickAvatarAsync();
        AvatarMaleRadio.Click += (_, _) => SaveAvatarSelection();
        AvatarFemaleRadio.Click += (_, _) => SaveAvatarSelection();
        AvatarCustomRadio.Click += (_, _) => SaveAvatarSelection();
        BrowseChatBackgroundButton.Click += async (_, _) => await PickChatBackgroundAsync();
        ClearChatBackgroundButton.Click += (_, _) => ClearChatBackground();

        DeleteMemoryButton.Click += async (_, _) => await DeleteSelectedMemoryAsync();
        ClearCurrentSessionMemoryButton.Click += async (_, _) => await ClearCurrentSessionMemoryAsync();
        ClearAllMemoryButton.Click += async (_, _) => await ClearAllMemoryAsync();
        RefreshMemoryButton.Click += async (_, _) => await RefreshMemoryListAsync();
        MemorySearchBox.TextChanged += (_, _) => RenderMemoryEntries();
        MemoryScopeBox.SelectionChanged += (_, _) => RenderMemoryEntries();
        MemoryListBox.SelectionChanged += (_, _) => OnMemorySelectionChanged();
        Mem0InstallButton.Click += async (_, _) => await InstallMem0Async();

        SaveComputerControlButton.Click += (_, _) => SaveComputerControlSettings();
        CancelComputerControlButton.Click += (_, _) => LoadComputerControlFromSettings();
        ComputerControlBackendBox.SelectionChanged += (_, _) => MarkComputerControlDirty();
        VisionProviderBox.SelectionChanged += (_, _) =>
        {
            PopulateVisionModelBox();
            MarkComputerControlDirty();
        };
        VisionModelBox.SelectionChanged += (_, _) => MarkComputerControlDirty();
        VisionModelBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty)
            {
                MarkComputerControlDirty();
            }
        };
        UfoPythonBox.TextChanged += (_, _) => MarkComputerControlDirty();
        UfoCheckButton.Click += async (_, _) => await CheckUfoAsync();

        AlwaysOnTopBox.Click += (_, _) => SaveSimpleSettings();
        MinimizeToTrayBox.Click += (_, _) => SaveSimpleSettings();
        AutoStartBox.Click += (_, _) => SaveSimpleSettings();
        ParticleEffectsBox.Click += (_, _) => SaveSimpleSettings();
        ReduceMotionBox.Click += (_, _) => SaveSimpleSettings();
        EnablePetBubblesBox.Click += (_, _) => SaveSimpleSettings();
        EnablePetIdleGreetingBox.Click += (_, _) => SaveSimpleSettings();
        EnablePetEdgeSnapBox.Click += (_, _) => SaveSimpleSettings();
        PetSizeBox.SelectionChanged += (_, _) => SaveSimpleSettings();
        PetOpacitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                PetOpacityValueText.Text = $"{PetOpacitySlider.Value:P0}";
                ScheduleSimpleSettingsSave();
            }
        };

        SizeChanged += (_, e) => UpdateResponsiveLayout(e.NewSize.Width);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                UpdateAmbientAnimationState();
            }
        };
        Closing += OnWindowClosing;
        Opened += (_, _) =>
        {
            UpdateResponsiveLayout(Bounds.Width);
            UpdateAmbientAnimationState();
            _ = RefreshMcpDependencyStatusAsync();
            _ = RefreshMcpOverallStatusAsync();
        };

        PopulateProviderPresets();
        PopulatePetSizeOptions();
        LoadFromSettings();
        InitializeSettingsNavigation();
        SettingsTabControl.SelectionChanged += async (_, _) => await OnSettingsPageSelectionChangedAsync();
        _isInitialized = true;
        UpdateProviderDirtyFromSnapshot();
        UpdateComputerControlDirtyFromSnapshot();
        _ = RefreshMemoryListAsync();
        _ = RefreshMem0StatusAsync();

        _settingsService.ProvidersChanged += OnProvidersChangedRefreshVision;
        Closed += (_, _) => _settingsService.ProvidersChanged -= OnProvidersChangedRefreshVision;
    }

    private void LoadFromSettings()
    {
        _isLoadingSettingsUi = true;
        try
        {
            var provider = string.IsNullOrWhiteSpace(_settingsService.Current.CurrentProvider)
                ? "openai"
                : _settingsService.Current.CurrentProvider;

            RefreshProviderCards();
            LoadProviderIntoForm(provider);

            AlwaysOnTopBox.IsChecked = _settingsService.Current.EnableAlwaysOnTop;
            MinimizeToTrayBox.IsChecked = _settingsService.Current.MinimizeToTray;
            try
            {
                AutoStartBox.IsChecked = AutoStartService.IsEnabled();
            }
            catch (Exception ex)
            {
                AppLogger.Error("config", "failed to read auto start state", ex);
                AutoStartBox.IsChecked = _settingsService.Current.EnableAutoStart;
            }

            ParticleEffectsBox.IsChecked = _settingsService.Current.EnableParticleEffects;
            ReduceMotionBox.IsChecked = _settingsService.Current.ReduceMotion;
            EnablePetBubblesBox.IsChecked = _settingsService.Current.EnablePetBubbles;
            EnablePetIdleGreetingBox.IsChecked = _settingsService.Current.EnablePetIdleGreeting;
            EnablePetEdgeSnapBox.IsChecked = _settingsService.Current.EnablePetEdgeSnap;
            PetOpacitySlider.Value = Math.Clamp(_settingsService.Current.PetOpacity, 0.65, 1.0);
            PetOpacityValueText.Text = $"{PetOpacitySlider.Value:P0}";
            SelectPetSize(_settingsService.Current.PetSizePreset);

            var avatarType = _settingsService.Current.UserAvatarType;
            AvatarMaleRadio.IsChecked = avatarType == "male";
            AvatarFemaleRadio.IsChecked = avatarType == "female";
            AvatarCustomRadio.IsChecked = avatarType == "custom";
            RefreshAvatarPreviewFromSelection();
            SetPreviewImage(ChatBackgroundPreviewImage, _settingsService.Current.ChatBackgroundImagePath);
            LoadComputerControlFromSettings();
        }
        finally
        {
            _isLoadingSettingsUi = false;
        }

        ApplyMotionPreference();
        SimpleSettingsStatusText.Text = "已同步";
        _ = RefreshMcpDependencyStatusAsync();
    }

    private async Task<bool> SaveProviderAsync()
    {
        if (_isProviderBusy || !TryValidateProviderForm(out var provider, out var apiKey, out var endpoint, out var defaultModel, out var models))
        {
            return false;
        }

        SetProviderBusy(true, "正在保存提供商…");
        try
        {
            _settingsService.UpdateApiKey(provider, apiKey, endpoint, defaultModel);
            _settingsService.SaveProviderModels(provider, models, defaultModel);
            if (!_settingsService.ListProviders().Any(p => string.Equals(p, _settingsService.Current.CurrentProvider, StringComparison.OrdinalIgnoreCase)))
            {
                _settingsService.SwitchCurrentProvider(provider);
            }

            string? reloadError = null;
            if (string.Equals(SettingsService.NormalizeProviderName(provider), SettingsService.NormalizeProviderName(_settingsService.Current.CurrentProvider), StringComparison.OrdinalIgnoreCase))
            {
                _settingsService.SwitchCurrentModel(provider, defaultModel);
                TryReloadChatServiceFromSettings(out reloadError);
            }

            RefreshProviderCards();
            LoadProviderIntoForm(provider);
            if (string.IsNullOrWhiteSpace(reloadError))
            {
                ShowSuccess("提供商配置已保存。");
            }
            else
            {
                ShowError($"提供商配置已保存，但当前服务暂时还没准备好：{reloadError}");
            }

            await Task.Yield();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("config", "save provider failed", ex);
            ProviderStatusText.Text = "保存失败：" + ex.Message;
            ShowError("保存提供商失败：" + ex.Message);
            return false;
        }
        finally
        {
            SetProviderBusy(false);
        }
    }

    private async Task TestProviderAsync()
    {
        await ProbeProviderAsync(updateModels: false);
    }

    private async Task FetchModelsAsync()
    {
        await ProbeProviderAsync(updateModels: true);
    }

    private async Task ProbeProviderAsync(bool updateModels)
    {
        if (_isProviderBusy || !TryValidateProviderConnectionFields(out var provider, out var apiKey, out var endpoint))
        {
            return;
        }

        SetProviderBusy(true, updateModels ? "正在获取模型…" : "正在测试连接…");
        try
        {
            var result = await _providerProbeService.FetchModelsAsync(provider, apiKey, endpoint);
            if (_settingsService.ListProviders().Any(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase)))
            {
                _settingsService.UpdateProviderConnectionStatus(provider, result.Status, result.Message);
            }

            if (result.Success && updateModels)
            {
                var selectedModel = GetSelectedDefaultModel();
                _isLoadingProviderUi = true;
                try
                {
                    _currentModelCandidates.Clear();
                    _currentModelCandidates.AddRange(result.Models.Select(CloneModel));
                    RenderModels(selectedModel);
                }
                finally
                {
                    _isLoadingProviderUi = false;
                }

                MarkProviderDirty();
            }

            RefreshProviderCards();
            ProviderStatusText.Text = result.Message;
            if (result.Success)
            {
                ShowSuccess(result.Message);
            }
            else
            {
                ShowError(result.Message);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("config", "provider probe failed", ex);
            ProviderStatusText.Text = "连接失败：" + ex.Message;
            ShowError("连接测试失败：" + ex.Message);
        }
        finally
        {
            SetProviderBusy(false);
        }
    }

    private bool TryValidateProviderConnectionFields(out string provider, out string apiKey, out string? endpoint)
    {
        ClearProviderValidation();
        provider = ProviderNameBox.Text?.Trim() ?? string.Empty;
        apiKey = ApiKeyBox.Text?.Trim() ?? string.Empty;
        endpoint = string.IsNullOrWhiteSpace(EndpointBox.Text) ? null : EndpointBox.Text.Trim();

        var valid = true;
        if (string.IsNullOrWhiteSpace(provider))
        {
            SetFieldError(ProviderNameBox, ProviderNameErrorText, "请输入提供商名称。");
            valid = false;
        }

        if (endpoint is not null &&
            (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            SetFieldError(EndpointBox, EndpointErrorText, "请输入以 http:// 或 https:// 开头的完整 URL。");
            valid = false;
        }

        if (!valid)
        {
            ProviderStatusText.Text = "请修正标出的字段后重试。";
        }

        return valid;
    }

    private bool TryValidateProviderForm(
        out string provider,
        out string apiKey,
        out string? endpoint,
        out string defaultModel,
        out List<ProviderModel> models)
    {
        defaultModel = string.Empty;
        models = new List<ProviderModel>();
        if (!TryValidateProviderConnectionFields(out provider, out apiKey, out endpoint))
        {
            return false;
        }

        defaultModel = GetSelectedDefaultModel();
        var selectedDefaultModel = defaultModel;
        models = CollectModels(selectedDefaultModel).ToList();
        if (string.IsNullOrWhiteSpace(selectedDefaultModel) ||
            models.All(model => !model.IsEnabled || !string.Equals(model.Id, selectedDefaultModel, StringComparison.OrdinalIgnoreCase)))
        {
            SetFieldError(DefaultModelBox, DefaultModelErrorText, "请启用并选择一个默认模型。");
            ProviderStatusText.Text = "默认模型必须来自已启用模型。";
            return false;
        }

        return true;
    }

    private void SetProviderBusy(bool isBusy, string? status = null)
    {
        _isProviderBusy = isBusy;
        ProviderBusyProgress.IsVisible = isBusy;
        SaveProviderButton.IsEnabled = !isBusy;
        TestProviderButton.IsEnabled = !isBusy;
        FetchModelsButton.IsEnabled = !isBusy;
        NewProviderButton.IsEnabled = !isBusy;
        CancelProviderButton.IsEnabled = !isBusy && _providerFormDirty;
        if (!string.IsNullOrWhiteSpace(status))
        {
            ProviderStatusText.Text = status;
        }
    }

    private void ToggleApiKeyVisibility()
    {
        _isApiKeyVisible = !_isApiKeyVisible;
        ApiKeyBox.PasswordChar = _isApiKeyVisible ? '\0' : '●';
        ToggleApiKeyButton.Content = _isApiKeyVisible ? "隐藏密钥" : "显示密钥";
        AutomationProperties.SetName(ToggleApiKeyButton, _isApiKeyVisible ? "隐藏 API Key" : "显示 API Key");
    }

    private void SaveSimpleSettings()
    {
        if (_isLoadingSettingsUi)
        {
            return;
        }

        _simpleSettingsSaveTimer.Stop();
        SimpleSettingsStatusText.Text = "正在保存…";
        try
        {
            _settingsService.Current.EnableAlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
            _settingsService.Current.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
            var enableAutoStart = AutoStartBox.IsChecked == true;
            if (enableAutoStart != _settingsService.Current.EnableAutoStart)
            {
                AutoStartService.SetEnabled(enableAutoStart);
            }

            _settingsService.Current.EnableAutoStart = enableAutoStart;
            _settingsService.Current.EnableParticleEffects = ParticleEffectsBox.IsChecked == true;
            _settingsService.Current.ReduceMotion = ReduceMotionBox.IsChecked == true;
            _settingsService.Current.EnablePetBubbles = EnablePetBubblesBox.IsChecked == true;
            _settingsService.Current.EnablePetIdleGreeting = EnablePetIdleGreetingBox.IsChecked == true;
            _settingsService.Current.EnablePetEdgeSnap = EnablePetEdgeSnapBox.IsChecked == true;
            _settingsService.Current.PetOpacity = Math.Clamp(PetOpacitySlider.Value, 0.65, 1.0);
            if (PetSizeBox.SelectedItem is ComboBoxItem { Tag: string petSize })
            {
                ApplyPetSizePresetToSettings(petSize);
            }

            _settingsService.Save();
            SimpleSettingsStatusText.Text = "已自动保存";
            ApplyMotionPreference();
        }
        catch (Exception ex)
        {
            AppLogger.Error("config", "failed to save simple settings", ex);
            AutoStartBox.IsChecked = _settingsService.Current.EnableAutoStart;
            SimpleSettingsStatusText.Text = "保存失败";
            ShowError($"设置保存失败：{ex.Message}");
        }
    }

    private void ScheduleSimpleSettingsSave()
    {
        if (_isLoadingSettingsUi)
        {
            return;
        }

        SimpleSettingsStatusText.Text = "等待保存…";
        _simpleSettingsSaveTimer.Stop();
        _simpleSettingsSaveTimer.Start();
    }

    private void SaveAvatarSelection()
    {
        if (_isLoadingSettingsUi)
        {
            return;
        }

        RefreshAvatarPreviewFromSelection();
        if (AvatarCustomRadio.IsChecked == true &&
            (string.IsNullOrWhiteSpace(_settingsService.Current.CustomUserAvatarPath) || !File.Exists(_settingsService.Current.CustomUserAvatarPath)))
        {
            AppearanceStatusText.Text = "请选择自定义头像文件后再切换。";
            return;
        }

        _settingsService.Current.UserAvatarType = AvatarCustomRadio.IsChecked == true
            ? "custom"
            : AvatarFemaleRadio.IsChecked == true
                ? "female"
                : "male";
        _settingsService.Save();
        AppearanceStatusText.Text = "头像已自动保存。";
    }

    private void ClearChatBackground()
    {
        _settingsService.Current.ChatBackgroundImagePath = null;
        SetPreviewImage(ChatBackgroundPreviewImage, null);
        _settingsService.Save();
        AppearanceStatusText.Text = "聊天背景已清除。";
    }

    private void ApplyMotionPreference()
    {
        if (_settingsService.Current.ReduceMotion)
        {
            if (SettingsContentHost.PageTransition is not null)
            {
                _fullPageTransition = SettingsContentHost.PageTransition;
            }

            SettingsContentHost.PageTransition = null;
        }
        else if (SettingsContentHost.PageTransition is null)
        {
            SettingsContentHost.PageTransition = _fullPageTransition;
        }

        UpdateAmbientAnimationState();
    }

    private void UpdateAmbientAnimationState()
    {
        var shouldAnimate = IsVisible && WindowState != WindowState.Minimized && !_settingsService.Current.ReduceMotion;
        if (shouldAnimate)
        {
            _flickerTimer.Start();
        }
        else
        {
            _flickerTimer.Stop();
            BackgroundContainer.Opacity = 1;
            GlowLayerPink.Opacity = 1;
            GlowLayerBlue.Opacity = 1;
            GlowLayerWhite.Opacity = 0.48;
        }

        if (shouldAnimate && _settingsService.Current.EnableParticleEffects)
        {
            _particleEffect.Start(32);
        }
        else
        {
            _particleEffect.Stop();
        }
    }

    private static void SetFieldError(Control field, TextBlock errorText, string message)
    {
        if (!field.Classes.Contains("invalid"))
        {
            field.Classes.Add("invalid");
        }

        errorText.Text = message;
        errorText.IsVisible = true;
        AutomationProperties.SetHelpText(field, message);
    }

    private static void ClearFieldError(Control field, TextBlock errorText)
    {
        field.Classes.Remove("invalid");
        errorText.Text = string.Empty;
        errorText.IsVisible = false;
        AutomationProperties.SetHelpText(field, string.Empty);
    }

    private void ClearProviderValidation()
    {
        ClearFieldError(ProviderNameBox, ProviderNameErrorText);
        ClearFieldError(EndpointBox, EndpointErrorText);
        ClearFieldError(DefaultModelBox, DefaultModelErrorText);
    }

    private async Task OnSettingsPageSelectionChangedAsync()
    {
        if (_suppressSettingsPageChange)
        {
            return;
        }

        var requestedPage = GetSettingsPageId(SettingsTabControl.SelectedItem);
        if (requestedPage is null)
        {
            SelectSettingsPage(_lastSettingsPageId);
            return;
        }

        await TryChangeSettingsPageAsync(requestedPage.Value);
    }

    internal async Task<bool> TryChangeSettingsPageAsync(SettingsPageId requestedPage)
    {
        if (requestedPage == _lastSettingsPageId)
        {
            SelectSettingsPage(requestedPage);
            return true;
        }

        var previousPage = _lastSettingsPageId;
        if (HasUnsavedChanges(previousPage))
        {
            SelectSettingsPage(previousPage);
            var decision = await UnsavedChangesHandler(
                this,
                "未保存更改",
                $"“{GetSettingsPageName(previousPage)}”页面还有未保存的配置。你可以留在当前页面，或在离开前放弃/保存更改。",
                "放弃并离开",
                "保存并离开");

            if (decision == UnsavedChangesDecision.Cancel)
            {
                return false;
            }

            if (decision == UnsavedChangesDecision.Discard)
            {
                DiscardChanges(previousPage);
            }
            else if (!await SaveChangesAsync(previousPage))
            {
                SelectSettingsPage(previousPage);
                return false;
            }
        }

        SettingsContentHost.IsTransitionReversed = (int)requestedPage < (int)previousPage;
        SelectSettingsPage(requestedPage);
        return true;
    }

    internal bool HasUnsavedChanges(SettingsPageId pageId)
    {
        return pageId switch
        {
            SettingsPageId.Provider => _providerFormDirty,
            SettingsPageId.ComputerControl => _computerControlDirty,
            SettingsPageId.Mcp => McpPanel.HasUnsavedChanges,
            _ => false
        };
    }

    private async Task<bool> SaveChangesAsync(SettingsPageId pageId)
    {
        return pageId switch
        {
            SettingsPageId.Provider => await SaveProviderAsync(),
            SettingsPageId.ComputerControl => SaveComputerControlSettings(),
            SettingsPageId.Mcp => await McpPanel.SaveCurrentServerAsync(),
            _ => true
        };
    }

    private void DiscardChanges(SettingsPageId pageId)
    {
        switch (pageId)
        {
            case SettingsPageId.Provider:
                CancelProviderChanges();
                break;
            case SettingsPageId.ComputerControl:
                LoadComputerControlFromSettings();
                break;
            case SettingsPageId.Mcp:
                McpPanel.DiscardUnsavedChanges();
                break;
        }
    }

    private async Task<bool> SaveAllDirtyPagesAsync()
    {
        foreach (var pageId in new[] { SettingsPageId.Provider, SettingsPageId.ComputerControl, SettingsPageId.Mcp })
        {
            if (HasUnsavedChanges(pageId) && !await SaveChangesAsync(pageId))
            {
                SelectSettingsPage(pageId);
                return false;
            }
        }

        return true;
    }

    private void DiscardAllDirtyPages()
    {
        foreach (var pageId in new[] { SettingsPageId.Provider, SettingsPageId.ComputerControl, SettingsPageId.Mcp })
        {
            if (HasUnsavedChanges(pageId))
            {
                DiscardChanges(pageId);
            }
        }
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowWindowClose || !HasAnyUnsavedChanges())
        {
            return;
        }

        if (_isClosingPromptOpen)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _isClosingPromptOpen = true;
        try
        {
            var decision = await UnsavedChangesHandler(
                this,
                "关闭设置中心",
                "Provider、电脑控制或 MCP 页面还有未保存配置。",
                "放弃并关闭",
                "保存并关闭");

            if (decision == UnsavedChangesDecision.Cancel)
            {
                return;
            }

            if (decision == UnsavedChangesDecision.Discard)
            {
                DiscardAllDirtyPages();
            }
            else if (!await SaveAllDirtyPagesAsync())
            {
                return;
            }

            _allowWindowClose = true;
            Close();
        }
        finally
        {
            _isClosingPromptOpen = false;
        }
    }

    private bool HasAnyUnsavedChanges()
        => _providerFormDirty || _computerControlDirty || McpPanel.HasUnsavedChanges;

    private void InitializeSettingsNavigation()
    {
        var transition = SettingsContentHost.PageTransition;
        SettingsContentHost.PageTransition = null;
        SelectSettingsPage(SettingsPageId.Provider);
        SettingsContentHost.PageTransition = transition;
    }

    private void SelectSettingsPage(SettingsPageId pageId)
    {
        var navigationItem = GetNavigationItem(pageId);
        _suppressSettingsPageChange = true;
        try
        {
            SettingsTabControl.SelectedItem = navigationItem;
            SettingsContentHost.Content = navigationItem.Tag;
            _lastSettingsPageId = pageId;
        }
        finally
        {
            _suppressSettingsPageChange = false;
        }
    }

    private ListBoxItem GetNavigationItem(SettingsPageId pageId)
    {
        return pageId switch
        {
            SettingsPageId.Provider => ProviderNavigationItem,
            SettingsPageId.Memory => MemoryNavigationItem,
            SettingsPageId.ComputerControl => ComputerControlNavigationItem,
            SettingsPageId.Mcp => McpNavigationItem,
            SettingsPageId.Skill => SkillNavigationItem,
            SettingsPageId.Pet => PetNavigationItem,
            SettingsPageId.Appearance => AppearanceNavigationItem,
            _ => ProviderNavigationItem
        };
    }

    private SettingsPageId? GetSettingsPageId(object? selectedItem)
    {
        if (ReferenceEquals(selectedItem, ProviderNavigationItem)) return SettingsPageId.Provider;
        if (ReferenceEquals(selectedItem, MemoryNavigationItem)) return SettingsPageId.Memory;
        if (ReferenceEquals(selectedItem, ComputerControlNavigationItem)) return SettingsPageId.ComputerControl;
        if (ReferenceEquals(selectedItem, McpNavigationItem)) return SettingsPageId.Mcp;
        if (ReferenceEquals(selectedItem, SkillNavigationItem)) return SettingsPageId.Skill;
        if (ReferenceEquals(selectedItem, PetNavigationItem)) return SettingsPageId.Pet;
        if (ReferenceEquals(selectedItem, AppearanceNavigationItem)) return SettingsPageId.Appearance;
        return null;
    }

    private static string GetSettingsPageName(SettingsPageId pageId)
    {
        return pageId switch
        {
            SettingsPageId.Provider => "AI 服务",
            SettingsPageId.Memory => "记忆",
            SettingsPageId.ComputerControl => "电脑控制",
            SettingsPageId.Mcp => "MCP",
            SettingsPageId.Skill => "Skill",
            SettingsPageId.Pet => "桌宠与界面",
            SettingsPageId.Appearance => "头像与背景",
            _ => pageId.ToString()
        };
    }

    private void UpdateResponsiveLayout(double width)
    {
        var isNarrow = width < 940;
        SettingsShellGrid.ColumnDefinitions = new ColumnDefinitions(width < 820 ? "156,*" : "176,*");

        ArrangeTwoPaneGrid(ProviderLayoutGrid, ProviderListPane, ProviderEditorPane, isNarrow, "270,*");
        ProviderCardsScrollViewer.MaxHeight = isNarrow ? 220 : 500;
        ArrangeTwoPaneGrid(MemoryLayoutGrid, MemoryListPane, MemoryDetailPane, isNarrow, "360,*");
        ArrangeTwoPaneGrid(AppearanceLayoutGrid, AvatarPane, ChatBackgroundPane, isNarrow, "*,*");

        if (isNarrow)
        {
            ComputerControlLayoutGrid.ColumnDefinitions = new ColumnDefinitions("*");
            ComputerControlLayoutGrid.RowDefinitions = new RowDefinitions("Auto,Auto,Auto");
            Grid.SetColumn(ComputerControlMainPane, 0);
            Grid.SetRow(ComputerControlMainPane, 0);
            Grid.SetColumn(ComputerControlAdvancedPane, 0);
            Grid.SetRow(ComputerControlAdvancedPane, 1);
            Grid.SetColumn(ComputerControlActionsPane, 0);
            Grid.SetRow(ComputerControlActionsPane, 2);
            Grid.SetColumnSpan(ComputerControlActionsPane, 1);
            ComputerControlAdvancedPane.Margin = new Thickness(0, 14, 0, 0);
        }
        else
        {
            ComputerControlLayoutGrid.ColumnDefinitions = new ColumnDefinitions("*,*");
            ComputerControlLayoutGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
            Grid.SetColumn(ComputerControlMainPane, 0);
            Grid.SetRow(ComputerControlMainPane, 0);
            Grid.SetColumn(ComputerControlAdvancedPane, 1);
            Grid.SetRow(ComputerControlAdvancedPane, 0);
            Grid.SetColumn(ComputerControlActionsPane, 0);
            Grid.SetRow(ComputerControlActionsPane, 1);
            Grid.SetColumnSpan(ComputerControlActionsPane, 2);
            ComputerControlAdvancedPane.Margin = new Thickness(14, 0, 0, 0);
        }
    }

    private static void ArrangeTwoPaneGrid(Grid grid, Control firstPane, Control secondPane, bool isNarrow, string wideColumns)
    {
        if (isNarrow)
        {
            grid.ColumnDefinitions = new ColumnDefinitions("*");
            grid.RowDefinitions = new RowDefinitions("Auto,Auto");
            Grid.SetColumn(firstPane, 0);
            Grid.SetRow(firstPane, 0);
            Grid.SetColumn(secondPane, 0);
            Grid.SetRow(secondPane, 1);
            secondPane.Margin = new Thickness(0, 14, 0, 0);
        }
        else
        {
            grid.ColumnDefinitions = new ColumnDefinitions(wideColumns);
            grid.RowDefinitions = new RowDefinitions("Auto");
            Grid.SetColumn(firstPane, 0);
            Grid.SetRow(firstPane, 0);
            Grid.SetColumn(secondPane, 1);
            Grid.SetRow(secondPane, 0);
            secondPane.Margin = new Thickness(14, 0, 0, 0);
        }
    }

    private void ApplyPetSizePresetToSettings(string preset)
    {
        _settingsService.Current.PetSizePreset = preset;
        var size = preset switch
        {
            "small" => 96,
            "large" => 160,
            _ => 125
        };
        _settingsService.Current.PetWidth = size;
        _settingsService.Current.PetHeight = size;
    }

    private void PopulateProviderPresets()
    {
        var presets = new[]
        {
            new ProviderPreset("OpenAI", "openai", "https://api.openai.com/v1", "gpt-4o"),
            new ProviderPreset("DeepSeek", "deepseek", "https://api.deepseek.com", "deepseek-v4-flash"),
            new ProviderPreset("Moonshot / Kimi", "moonshot", "https://api.moonshot.ai/v1", "kimi-k2.5"),
            new ProviderPreset("Custom", string.Empty, null, "gpt-4o")
        };

        ProviderPresetBox.Items.Clear();
        foreach (var preset in presets)
        {
            ProviderPresetBox.Items.Add(new ComboBoxItem
            {
                Content = preset.Name,
                Tag = preset
            });
        }

        ProviderPresetBox.SelectedIndex = -1;
    }

    private void PopulatePetSizeOptions()
    {
        PetSizeBox.Items.Clear();
        PetSizeBox.Items.Add(new ComboBoxItem { Content = "小巧", Tag = "small" });
        PetSizeBox.Items.Add(new ComboBoxItem { Content = "标准", Tag = "normal" });
        PetSizeBox.Items.Add(new ComboBoxItem { Content = "醒目", Tag = "large" });
    }

    private void SelectPetSize(string? preset)
    {
        var selected = string.IsNullOrWhiteSpace(preset) ? "normal" : preset;
        foreach (var item in PetSizeBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && string.Equals(tag, selected, StringComparison.OrdinalIgnoreCase))
            {
                PetSizeBox.SelectedItem = item;
                return;
            }
        }

        PetSizeBox.SelectedIndex = 1;
    }

    private void RefreshProviderCards()
    {
        ProviderCardsPanel.Children.Clear();
        foreach (var provider in _settingsService.ListProviders())
        {
            ProviderCardsPanel.Children.Add(BuildProviderCard(provider));
        }
    }

    private Control BuildProviderCard(string provider)
    {
        var info = _settingsService.GetApiKeyInfo(provider);
        var isCurrent = string.Equals(
            SettingsService.NormalizeProviderName(provider),
            SettingsService.NormalizeProviderName(_settingsService.Current.CurrentProvider),
            StringComparison.OrdinalIgnoreCase);
        var modelCount = info?.Models.Count(model => model.IsEnabled) ?? 0;
        var status = string.IsNullOrWhiteSpace(info?.LastConnectionMessage)
            ? "尚未测试"
            : info!.LastConnectionMessage;

        var border = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(13),
            Background = AemiUi.Brush(isCurrent ? AemiUi.PinkSoft : AemiUi.Panel),
            BorderBrush = AemiUi.Brush(isCurrent ? AemiUi.Star : AemiUi.Border),
            BorderThickness = new Thickness(1)
        };

        var panel = new StackPanel { Spacing = 7 };
        panel.Children.Add(AemiUi.Badge(isCurrent ? "当前连接" : "已保存", isCurrent ? "star" : "halo"));
        panel.Children.Add(new TextBlock
        {
            Text = provider,
            FontWeight = FontWeight.SemiBold,
            Foreground = AemiUi.Brush(AemiUi.Ghost),
            FontSize = 16
        });
        panel.Children.Add(new TextBlock
        {
            Text = info?.Endpoint ?? "默认 Endpoint",
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"默认：{info?.ModelId ?? _settingsService.Current.DefaultModel} · 已启用 {modelCount} 个",
            Classes = { "subtle" },
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = status,
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 42
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7
        };
        var editButton = new Button { Content = "编辑", Classes = { "ghost" }, MinWidth = 60 };
        AutomationProperties.SetName(editButton, $"编辑提供商 {provider}");
        editButton.Click += async (_, _) => await SelectProviderForEditingAsync(provider);
        var useButton = new Button { Content = "使用", Classes = { "primary" }, MinWidth = 60, IsEnabled = !isCurrent };
        AutomationProperties.SetName(useButton, $"切换到提供商 {provider}");
        useButton.Click += async (_, _) => await UseProviderAsync(provider);
        var deleteButton = new Button { Content = "删除", Classes = { "danger" }, MinWidth = 60 };
        AutomationProperties.SetName(deleteButton, $"删除提供商 {provider}");
        deleteButton.Click += async (_, _) => await DeleteProviderAsync(provider);

        buttons.Children.Add(editButton);
        buttons.Children.Add(useButton);
        buttons.Children.Add(deleteButton);
        panel.Children.Add(buttons);
        border.Child = panel;
        return border;
    }

    private async Task BeginNewProviderAsync()
    {
        if (!await ConfirmDiscardProviderChangesAsync())
        {
            return;
        }

        StartNewProviderForm();
    }

    private async Task SelectProviderForEditingAsync(string provider)
    {
        if (string.Equals(_editingProviderName, SettingsService.NormalizeProviderName(provider), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!await ConfirmDiscardProviderChangesAsync())
        {
            return;
        }

        LoadProviderIntoForm(provider);
    }

    private async Task<bool> ConfirmDiscardProviderChangesAsync()
    {
        if (!_providerFormDirty)
        {
            return true;
        }

        var decision = await UnsavedChangesHandler(
            this,
            "提供商更改尚未保存",
            "当前提供商表单还有未保存内容。",
            "放弃更改",
            "保存更改");
        return decision switch
        {
            UnsavedChangesDecision.Discard => true,
            UnsavedChangesDecision.Save => await SaveProviderAsync(),
            _ => false
        };
    }

    private void LoadProviderIntoForm(string provider)
    {
        _isLoadingProviderUi = true;
        try
        {
            var normalized = SettingsService.NormalizeProviderName(provider);
            var info = _settingsService.GetApiKeyInfo(normalized);
            var defaultModel = info?.ModelId ?? _settingsService.Current.DefaultModel;
            _editingProviderName = normalized;
            ProviderNameBox.Text = normalized;
            ApiKeyBox.Text = info?.Key ?? string.Empty;
            EndpointBox.Text = info?.Endpoint ?? string.Empty;
            ProviderStatusText.Text = info?.LastConnectionMessage ?? "可以测试连接或获取模型列表。";
            _currentModelCandidates.Clear();
            if (info is not null)
            {
                _currentModelCandidates.AddRange(info.Models.Select(CloneModel));
            }

            if (!string.IsNullOrWhiteSpace(defaultModel) &&
                _currentModelCandidates.All(model => !string.Equals(model.Id, defaultModel, StringComparison.OrdinalIgnoreCase)))
            {
                _currentModelCandidates.Add(new ProviderModel { Id = defaultModel, IsEnabled = true });
            }

            RenderModels(defaultModel);
            ClearProviderValidation();
            _isApiKeyVisible = false;
            ApiKeyBox.PasswordChar = '●';
            ToggleApiKeyButton.Content = "显示密钥";
        }
        finally
        {
            _isLoadingProviderUi = false;
        }

        CaptureProviderBaseline();
    }

    private void StartNewProviderForm()
    {
        _isLoadingProviderUi = true;
        try
        {
            _editingProviderName = null;
            ProviderNameBox.Text = string.Empty;
            ApiKeyBox.Text = string.Empty;
            EndpointBox.Text = string.Empty;
            ProviderStatusText.Text = "填写信息后保存，或先测试连接。";
            _currentModelCandidates.Clear();
            _currentModelCandidates.Add(new ProviderModel { Id = "gpt-4o", IsEnabled = true });
            RenderModels("gpt-4o");
            ClearProviderValidation();
        }
        finally
        {
            _isLoadingProviderUi = false;
        }

        CaptureProviderBaseline();
        ProviderNameBox.Focus();
    }

    private void CancelProviderChanges()
    {
        if (!string.IsNullOrWhiteSpace(_editingProviderName) &&
            _settingsService.ListProviders().Any(provider => string.Equals(provider, _editingProviderName, StringComparison.OrdinalIgnoreCase)))
        {
            LoadProviderIntoForm(_editingProviderName);
        }
        else
        {
            LoadProviderIntoForm(_settingsService.Current.CurrentProvider);
        }

        ProviderStatusText.Text = "未保存的更改已取消。";
    }

    private void ApplySelectedProviderPreset()
    {
        if (_isLoadingProviderUi || ProviderPresetBox.SelectedItem is not ComboBoxItem { Tag: ProviderPreset preset } || string.IsNullOrWhiteSpace(preset.Provider))
        {
            return;
        }

        _isLoadingProviderUi = true;
        try
        {
            var existing = _settingsService.GetApiKeyInfo(preset.Provider);
            ProviderNameBox.Text = preset.Provider;
            EndpointBox.Text = preset.Endpoint ?? string.Empty;
            ApiKeyBox.Text = existing?.Key ?? string.Empty;
            _currentModelCandidates.Clear();
            if (existing is not null)
            {
                _currentModelCandidates.AddRange(existing.Models.Select(CloneModel));
            }

            if (_currentModelCandidates.All(model => !string.Equals(model.Id, preset.ModelId, StringComparison.OrdinalIgnoreCase)))
            {
                _currentModelCandidates.Add(new ProviderModel { Id = preset.ModelId, IsEnabled = true });
            }

            RenderModels(existing?.ModelId ?? preset.ModelId);
        }
        finally
        {
            _isLoadingProviderUi = false;
        }

        MarkProviderDirty();
    }

    private void AddManualModel()
    {
        var modelId = ManualModelBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        if (_currentModelCandidates.All(model => !string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            _currentModelCandidates.Add(new ProviderModel
            {
                Id = modelId,
                IsEnabled = true,
                LastSeenAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            var existing = _currentModelCandidates.First(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase));
            existing.IsEnabled = true;
        }

        ManualModelBox.Text = string.Empty;
        RenderModels(modelId);
        MarkProviderDirty();
    }

    private void RenderModels(string? selectedModel)
    {
        var previousLoadingState = _isLoadingProviderUi;
        _isLoadingProviderUi = true;
        try
        {
            ModelsPanel.Children.Clear();
            foreach (var model in _currentModelCandidates.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToList())
            {
                var enableCheckBox = new CheckBox
                {
                    Content = string.IsNullOrWhiteSpace(model.OwnedBy) ? model.Id : $"{model.Id}  ({model.OwnedBy})",
                    IsChecked = model.IsEnabled,
                    VerticalAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(enableCheckBox, $"启用模型 {model.Id}");

                var visionCheckBox = new CheckBox
                {
                    Content = "视觉",
                    IsChecked = model.SupportsImageInput ?? true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                AutomationProperties.SetName(visionCheckBox, $"模型 {model.Id} 支持图片输入");
                ToolTip.SetTip(visionCheckBox, "勾选表示模型支持图片输入；取消后会改用视觉分析工具。");

                var removeButton = new Button
                {
                    Content = "移除",
                    Classes = { "danger" },
                    MinWidth = 68,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                AutomationProperties.SetName(removeButton, $"从配置中移除模型 {model.Id}");

                void UpdateEnabledState()
                {
                    model.IsEnabled = enableCheckBox.IsChecked == true;
                    RefreshDefaultModelOptions(GetSelectedDefaultModel());
                    MarkProviderDirty();
                }

                enableCheckBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property == ToggleButton.IsCheckedProperty)
                    {
                        UpdateEnabledState();
                    }
                };
                visionCheckBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property == ToggleButton.IsCheckedProperty)
                    {
                        model.SupportsImageInput = visionCheckBox.IsChecked == true;
                        MarkProviderDirty();
                    }
                };
                removeButton.Click += (_, _) =>
                {
                    _currentModelCandidates.Remove(model);
                    RenderModels(null);
                    MarkProviderDirty();
                };

                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                    MinHeight = 40
                };
                Grid.SetColumn(enableCheckBox, 0);
                Grid.SetColumn(visionCheckBox, 1);
                Grid.SetColumn(removeButton, 2);
                row.Children.Add(enableCheckBox);
                row.Children.Add(visionCheckBox);
                row.Children.Add(removeButton);
                ModelsPanel.Children.Add(row);
            }

            RefreshDefaultModelOptions(selectedModel);
        }
        finally
        {
            _isLoadingProviderUi = previousLoadingState;
        }
    }

    private void RefreshDefaultModelOptions(string? selectedModel)
    {
        var previousLoadingState = _isLoadingProviderUi;
        _isLoadingProviderUi = true;
        try
        {
            var fallback = _currentModelCandidates.FirstOrDefault(model => model.IsEnabled)?.Id;
            DefaultModelBox.Items.Clear();
            foreach (var model in _currentModelCandidates.Where(model => model.IsEnabled).OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ComboBoxItem { Content = model.Id, Tag = model.Id };
                DefaultModelBox.Items.Add(item);
                if (string.Equals(model.Id, selectedModel, StringComparison.OrdinalIgnoreCase))
                {
                    DefaultModelBox.SelectedItem = item;
                }
            }

            if (DefaultModelBox.SelectedItem is null && !string.IsNullOrWhiteSpace(fallback))
            {
                DefaultModelBox.SelectedItem = DefaultModelBox.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, fallback, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _isLoadingProviderUi = previousLoadingState;
        }

        if (DefaultModelBox.SelectedItem is not null)
        {
            ClearFieldError(DefaultModelBox, DefaultModelErrorText);
        }
    }

    private string GetSelectedDefaultModel()
    {
        return DefaultModelBox.SelectedItem is ComboBoxItem { Tag: string model }
            ? model
            : _currentModelCandidates.FirstOrDefault(candidate => candidate.IsEnabled)?.Id ?? string.Empty;
    }

    private IEnumerable<ProviderModel> CollectModels(string _)
    {
        return _currentModelCandidates
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .Select(CloneModel)
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last());
    }

    private void MarkProviderDirty()
    {
        if (_isLoadingProviderUi || !_isInitialized || _providerBaseline is null)
        {
            return;
        }

        UpdateProviderDirtyFromSnapshot();
    }

    private void CaptureProviderBaseline()
    {
        _providerBaseline = CreateProviderSnapshot();
        SetProviderDirty(false);
    }

    private void UpdateProviderDirtyFromSnapshot()
    {
        SetProviderDirty(_providerBaseline is not null && _providerBaseline != CreateProviderSnapshot());
    }

    private ProviderFormSnapshot CreateProviderSnapshot()
    {
        var models = string.Join("\n", _currentModelCandidates
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(model => string.Join("\u001F",
                model.Id.Trim().ToLowerInvariant(),
                model.OwnedBy?.Trim() ?? string.Empty,
                model.IsEnabled,
                model.LastSeenAt?.ToUniversalTime().ToString("O") ?? string.Empty,
                model.ContextLength?.ToString() ?? string.Empty,
                model.SupportsImageInput?.ToString() ?? string.Empty,
                model.SupportsVideoInput?.ToString() ?? string.Empty,
                model.SupportsReasoning?.ToString() ?? string.Empty)));

        return new ProviderFormSnapshot(
            SettingsService.NormalizeProviderName(_editingProviderName ?? string.Empty),
            SettingsService.NormalizeProviderName(ProviderNameBox.Text ?? string.Empty),
            ApiKeyBox.Text?.Trim() ?? string.Empty,
            EndpointBox.Text?.Trim() ?? string.Empty,
            GetSelectedDefaultModel().Trim(),
            models);
    }

    private void SetProviderDirty(bool isDirty)
    {
        _providerFormDirty = isDirty;
        ProviderDirtyText.IsVisible = isDirty;
        CancelProviderButton.IsEnabled = isDirty && !_isProviderBusy;
    }

    private async Task UseProviderAsync(string provider)
    {
        if (!await ConfirmDiscardProviderChangesAsync())
        {
            return;
        }

        if (!_settingsService.SwitchCurrentProvider(provider))
        {
            ShowError("切换失败：未找到该提供商。");
            return;
        }

        var ready = TryReloadChatServiceFromSettings(out var error);
        RefreshProviderCards();
        LoadProviderIntoForm(provider);
        if (ready)
        {
            ShowSuccess($"已切换到 {provider}。");
        }
        else
        {
            ShowError($"已切换到 {provider}，但当前服务暂时还没准备好：{error ?? "请检查 API Key、Endpoint 和模型配置。"}");
        }
    }

    private async Task DeleteProviderAsync(string provider)
    {
        if (!await DialogService.ConfirmAsync(this, "删除提供商", $"确定删除 {provider} 吗？此操作会同时移除保存的模型与连接信息。", "删除提供商"))
        {
            return;
        }

        if (!_settingsService.DeleteProvider(provider))
        {
            ShowError("删除失败：未找到该提供商。");
            return;
        }

        var ready = TryReloadChatServiceFromSettings(out var error);
        RefreshProviderCards();
        if (string.Equals(_editingProviderName, SettingsService.NormalizeProviderName(provider), StringComparison.OrdinalIgnoreCase))
        {
            LoadProviderIntoForm(_settingsService.Current.CurrentProvider);
        }

        if (ready)
        {
            ShowSuccess($"已删除 {provider}。");
        }
        else
        {
            ShowError($"已删除 {provider}，但当前服务暂时还没准备好：{error ?? "请检查 API Key、Endpoint 和模型配置。"}");
        }
    }

    private async Task RefreshMemoryListAsync()
    {
        _memoryEntries.Clear();
        _selectedMemoryEntryId = null;
        DeleteMemoryButton.IsEnabled = false;
        MemoryDetailScopeText.Text = "尚未选择";
        MemoryDetailText.Text = "从左侧选择一条记忆。";
        MemoryStatusText.Text = "正在从 Mem0 读取记忆……";

        try
        {
            var sessionId = _currentSessionIdProvider?.Invoke();
            var client = await GetMem0ClientAsync();
            if (client is null)
            {
                RenderMemoryEntries();
                MemoryStatusText.Text = "Mem0 未启用。请先安装依赖并配置可用的 Provider。";
                return;
            }

            var globalTask = client.GetAllAsync(Mem0Scope.GlobalUser, topK: 100);
            Task<System.Text.Json.JsonElement> sessionTask = string.IsNullOrWhiteSpace(sessionId)
                ? Task.FromResult(default(System.Text.Json.JsonElement))
                : client.GetAllAsync(Mem0Scope.ForSession(sessionId), topK: 100);

            await Task.WhenAll(globalTask, sessionTask);
            AddMem0Entries(globalTask.Result, "全局");
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                AddMem0Entries(sessionTask.Result, "会话");
            }

            RenderMemoryEntries();
        }
        catch (Exception ex)
        {
            AppLogger.Error("memory", "ConfigWindow 读取记忆失败", ex);
            RenderMemoryEntries();
            MemoryStatusText.Text = "读取记忆失败：" + ex.Message;
        }
    }

    private void AddMem0Entries(System.Text.Json.JsonElement response, string scopeLabel)
    {
        if (response.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !response.TryGetProperty("results", out var results) ||
            results.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                continue;
            }

            var id = item.TryGetProperty("id", out var idElement) && idElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? idElement.GetString()
                : null;
            var text = item.TryGetProperty("memory", out var memoryElement) && memoryElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? memoryElement.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(text))
            {
                _memoryEntries.Add(new MemoryEntry(id, text, scopeLabel));
            }
        }
    }

    private void RenderMemoryEntries()
    {
        var query = MemorySearchBox.Text?.Trim() ?? string.Empty;
        var scope = (MemoryScopeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var filtered = _memoryEntries
            .Where(entry => scope == "all" || string.Equals(entry.ScopeLabel, scope, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(query) || entry.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        MemoryListBox.Items.Clear();
        foreach (var entry in filtered)
        {
            var preview = entry.Text.Length <= 72 ? entry.Text : entry.Text[..72] + "…";
            var content = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock
                    {
                        Text = entry.ScopeLabel,
                        Classes = { "muted" },
                        FontSize = 11,
                        FontWeight = FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = preview,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = AemiUi.Brush(AemiUi.Ghost)
                    }
                }
            };
            var item = new ListBoxItem { Content = content, Tag = entry };
            AutomationProperties.SetName(item, $"{entry.ScopeLabel}记忆：{preview}");
            MemoryListBox.Items.Add(item);
        }

        MemoryStatusText.Text = _memoryEntries.Count == 0
            ? "暂无长期记忆。"
            : filtered.Count == _memoryEntries.Count
                ? $"共 {_memoryEntries.Count} 条长期记忆。"
                : $"显示 {filtered.Count} / {_memoryEntries.Count} 条长期记忆。";
        _selectedMemoryEntryId = null;
        DeleteMemoryButton.IsEnabled = false;
        MemoryDetailScopeText.Text = "尚未选择";
        MemoryDetailText.Text = filtered.Count == 0 ? "没有符合当前筛选条件的记忆。" : "从左侧选择一条记忆。";
    }

    private async Task<Mem0Client?> GetMem0ClientAsync()
    {
        if (!_settingsService.Current.Mem0Enabled) return null;
        if (string.IsNullOrWhiteSpace(_settingsService.Current.Mem0PythonPath)) return null;
        var config = (_chatService as AemiChatService)?.BuildMem0Config();
        if (config is null) return null;

        var client = new Mem0Client(_settingsService.Current.Mem0PythonPath!, Mem0DependencyService.DefaultDataDirectory, config);
        client.Diagnostics = (level, msg, ex) =>
        {
            if (ex is null) AppLogger.Info("memory", $"[{level}] {msg}");
            else AppLogger.Error("memory", msg, ex);
        };
        return client;
    }


    private void OnMemorySelectionChanged()
    {
        if (MemoryListBox.SelectedItem is ListBoxItem { Tag: MemoryEntry entry })
        {
            _selectedMemoryEntryId = entry.Id;
            MemoryDetailScopeText.Text = $"{entry.ScopeLabel}记忆 · ID {entry.Id}";
            MemoryDetailText.Text = entry.Text;
            DeleteMemoryButton.IsEnabled = true;
            return;
        }

        _selectedMemoryEntryId = null;
        DeleteMemoryButton.IsEnabled = false;
        MemoryDetailScopeText.Text = "尚未选择";
        MemoryDetailText.Text = "从左侧选择一条记忆。";
    }

    private async Task DeleteSelectedMemoryAsync()
    {
        OnMemorySelectionChanged();
        if (string.IsNullOrWhiteSpace(_selectedMemoryEntryId))
        {
            ShowError("请先选择一条记忆。");
            return;
        }

        if (!await DialogService.ConfirmAsync(this, "删除记忆", "确定删除选中的长期记忆吗？", "删除记忆"))
        {
            return;
        }

        try
        {
            var client = await GetMem0ClientAsync();
            if (client is null)
            {
                ShowError("Mem0 未启用。");
                return;
            }

            await client.DeleteAsync(_selectedMemoryEntryId);
            await RefreshMemoryListAsync();
            ShowSuccess("记忆已删除。");
        }
        catch (Exception ex)
        {
            ShowError("删除记忆失败：" + ex.Message);
        }
    }

    private async Task ClearCurrentSessionMemoryAsync()
    {
        var sessionId = _currentSessionIdProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            ShowError("当前没有可清空的打开会话。");
            return;
        }

        if (!await DialogService.ConfirmAsync(this, "清空当前会话记忆", "确定清空当前会话的长期记忆吗？", "清空记忆"))
        {
            return;
        }

        try
        {
            var client = await GetMem0ClientAsync();
            if (client is null)
            {
                ShowError("Mem0 未启用。");
                return;
            }

            await client.DeleteAllAsync(Mem0Scope.ForSession(sessionId));
            await RefreshMemoryListAsync();
            ShowSuccess("当前会话记忆已清空。");
        }
        catch (Exception ex)
        {
            ShowError("清空会话记忆失败：" + ex.Message);
        }
    }

    private async Task ClearAllMemoryAsync()
    {
        if (!await DialogService.ConfirmAsync(this, "清空全部记忆", "确定清空全部长期记忆吗？这个操作不能恢复。", "清空全部"))
        {
            return;
        }

        try
        {
            var client = await GetMem0ClientAsync();
            if (client is null)
            {
                ShowError("Mem0 未启用。");
                return;
            }

            await client.DeleteAllAsync(Mem0Scope.GlobalUser);
            await RefreshMemoryListAsync();
            ShowSuccess("全部长期记忆已清空。");
        }
        catch (Exception ex)
        {
            ShowError("清空记忆失败：" + ex.Message);
        }
    }

    // ===== 视觉模型提供商选择（与对话 Provider 打通） =====

    /// <summary>填充视觉模型提供商下拉：含「复用当前对话提供商」+ 所有已配置 Provider。</summary>
    private void PopulateVisionProviderBox(string? preferredProvider = null)
    {
        var providerToSelect = preferredProvider ?? (VisionProviderBox.SelectedItem as ComboBoxItem)?.Tag as string;
        VisionProviderBox.Items.Clear();
        var reuseItem = new ComboBoxItem { Content = "复用当前对话提供商", Tag = null as string };
        VisionProviderBox.Items.Add(reuseItem);

        ComboBoxItem selected = reuseItem;
        foreach (var provider in _settingsService.ListProviders())
        {
            var item = new ComboBoxItem { Content = provider, Tag = provider };
            VisionProviderBox.Items.Add(item);
            if (string.Equals(provider, providerToSelect, StringComparison.OrdinalIgnoreCase))
            {
                selected = item;
            }
        }

        VisionProviderBox.SelectedItem = selected;
    }

    private void PopulateVisionModelBox(string? preferredModel = null)
    {
        var modelToSelect = preferredModel ?? VisionModelBox.Text?.Trim();
        VisionModelBox.Items.Clear();
        var provider = (VisionProviderBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(provider))
        {
            provider = _settingsService.Current.CurrentProvider;
        }

        var enabledModels = _settingsService.GetProviderModels(provider, enabledOnly: true);
        foreach (var model in enabledModels)
        {
            VisionModelBox.Items.Add(new ComboBoxItem { Content = model.Id, Tag = model.Id });
        }

        if (!string.IsNullOrWhiteSpace(modelToSelect))
        {
            VisionModelBox.Text = modelToSelect;
            VisionModelBox.SelectedItem = VisionModelBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, modelToSelect, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var defaultModel = _settingsService.GetApiKeyInfo(provider)?.ModelId ?? enabledModels.FirstOrDefault()?.Id;
            VisionModelBox.Text = defaultModel ?? string.Empty;
            VisionModelBox.SelectedItem = VisionModelBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, defaultModel, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void LoadComputerControlFromSettings()
    {
        _isLoadingComputerControlUi = true;
        try
        {
            ComputerControlBackendBox.SelectedIndex = _settingsService.Current.ComputerControlBackend?.ToLowerInvariant() switch
            {
                "uia" => 1,
                "ufo" => 2,
                _ => 0
            };
            UfoPythonBox.Text = _settingsService.Current.UfoPythonPath ?? string.Empty;
            PopulateVisionProviderBox(_settingsService.Current.VisionProvider);
            PopulateVisionModelBox(_settingsService.Current.VisionModel);
            ClearFieldError(VisionModelBox, VisionModelErrorText);
            ComputerControlStatusText.Text = "保存后新的电脑控制任务会使用此配置。";
        }
        finally
        {
            _isLoadingComputerControlUi = false;
        }

        CaptureComputerControlBaseline();
    }

    private bool SaveComputerControlSettings()
    {
        var model = VisionModelBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            SetFieldError(VisionModelBox, VisionModelErrorText, "视觉模型不能为空。请先选择已启用模型。");
            ComputerControlStatusText.Text = "请补全必填字段。";
            VisionModelBox.Focus();
            return false;
        }

        var backend = (ComputerControlBackendBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        var ufoPython = UfoPythonBox.Text?.Trim();
        if (backend == "ufo" && string.IsNullOrWhiteSpace(ufoPython))
        {
            ComputerControlStatusText.Text = "选择 UFO 后端时必须填写 Python 解释器路径。";
            UfoPythonBox.Focus();
            return false;
        }

        try
        {
            _settingsService.Current.ComputerControlBackend = backend;
            _settingsService.Current.UfoPythonPath = string.IsNullOrWhiteSpace(ufoPython) ? null : ufoPython;
            SaveVisionProviderSelection();
            _settingsService.Save();
            ClearFieldError(VisionModelBox, VisionModelErrorText);
            SetComputerControlDirty(false);
            ComputerControlStatusText.Text = "电脑控制配置已保存。";
            CaptureComputerControlBaseline();
            ShowSuccess("电脑控制配置已保存。");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("config", "save computer control settings failed", ex);
            ComputerControlStatusText.Text = "保存失败：" + ex.Message;
            ShowError("电脑控制配置保存失败：" + ex.Message);
            return false;
        }
    }

    private void SaveVisionProviderSelection()
    {
        var provider = (VisionProviderBox.SelectedItem as ComboBoxItem)?.Tag as string;
        _settingsService.Current.VisionProvider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim();
        var model = VisionModelBox.Text?.Trim();
        _settingsService.Current.VisionModel = string.IsNullOrWhiteSpace(model) ? null : model;
        _settingsService.Current.VisionEndpoint = string.IsNullOrWhiteSpace(_settingsService.Current.VisionEndpoint)
            ? null
            : _settingsService.Current.VisionEndpoint;
    }

    private void MarkComputerControlDirty()
    {
        if (_isLoadingComputerControlUi || _isLoadingSettingsUi || !_isInitialized || _computerControlBaseline is null)
        {
            return;
        }

        ClearFieldError(VisionModelBox, VisionModelErrorText);
        UpdateComputerControlDirtyFromSnapshot();
    }

    private void CaptureComputerControlBaseline()
    {
        _computerControlBaseline = CreateComputerControlSnapshot();
        SetComputerControlDirty(false);
    }

    private void UpdateComputerControlDirtyFromSnapshot()
    {
        SetComputerControlDirty(_computerControlBaseline is not null && _computerControlBaseline != CreateComputerControlSnapshot());
    }

    private ComputerControlFormSnapshot CreateComputerControlSnapshot()
    {
        var backend = (ComputerControlBackendBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        var provider = (VisionProviderBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return new ComputerControlFormSnapshot(
            backend.Trim().ToLowerInvariant(),
            SettingsService.NormalizeProviderName(provider ?? string.Empty),
            VisionModelBox.Text?.Trim() ?? string.Empty,
            UfoPythonBox.Text?.Trim() ?? string.Empty);
    }

    private void SetComputerControlDirty(bool isDirty)
    {
        _computerControlDirty = isDirty;
        ComputerControlDirtyText.IsVisible = isDirty;
        CancelComputerControlButton.IsEnabled = isDirty;
    }

    private void OnProvidersChangedRefreshVision()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var previousProvider = (VisionProviderBox.SelectedItem as ComboBoxItem)?.Tag as string;
            var previousModel = VisionModelBox.Text?.Trim();
            _isLoadingComputerControlUi = true;
            try
            {
                PopulateVisionProviderBox(previousProvider);
                PopulateVisionModelBox(previousModel);
            }
            finally
            {
                _isLoadingComputerControlUi = false;
            }

            UpdateComputerControlDirtyFromSnapshot();
            if (_computerControlDirty)
            {
                ComputerControlStatusText.Text = "AI 服务列表已更新；当前未保存选择已保留。";
            }
        });
    }

    private async Task RefreshMem0StatusAsync()
    {
        if (Mem0InstallStatusText is null)
        {
            return;
        }

        try
        {
            var status = await _mem0DependencyService.CheckAsync();
            var text = status.Installed
                ? $"Mem0 运行环境已就绪。\nPython：{status.PythonPath}\n数据目录：{Mem0DependencyService.DefaultDataDirectory}"
                : $"Mem0 运行环境未就绪：{status.Error ?? "未安装"}";
            Mem0InstallStatusText.Text = text;
            if (Mem0InstallButton is not null)
            {
                Mem0InstallButton.IsEnabled = true;
                Mem0InstallButton.Content = status.Installed ? "重新安装 Mem0" : "安装 Mem0 依赖";
            }
        }
        catch (Exception ex)
        {
            Mem0InstallStatusText.Text = "Mem0 状态检测失败：" + ex.Message;
        }
    }

    private async Task InstallMem0Async()
    {
        if (_isInstallingMem0)
        {
            return;
        }

        _isInstallingMem0 = true;
        try
        {
            Mem0InstallButton.IsEnabled = false;
            Mem0InstallButton.Content = "正在安装…";
            var progress = new Progress<string>(m => Mem0InstallStatusText.Text = m);
            var result = await _mem0DependencyService.InstallAsync(progress);
            if (result.Success && !string.IsNullOrWhiteSpace(result.PythonPath))
            {
                _settingsService.Current.Mem0PythonPath = result.PythonPath;
                _settingsService.Save();
            }

            await RefreshMem0StatusAsync();
            await RefreshMemoryListAsync();
            if (result.Success)
            {
                ShowSuccess(result.Message);
            }
            else
            {
                ShowError(result.Message);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("config", "mem0 install failed", ex);
            ShowError("Mem0 依赖安装失败：" + ex.Message);
        }
        finally
        {
            _isInstallingMem0 = false;
        }
    }

    // ===== UFO（轨 B）检测 =====

    /// <summary>
    /// 确保 uv.exe（和 bun.exe）已下载。Mem0 安装前若缺 uv 会自动调用本方法（借鉴 OneDragon 环境自检思路）。
    /// 返回 (是否就绪, uv 绝对路径)。
    /// </summary>
    private async Task<(bool Ok, string? UvPath)> EnsureUvAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        try
        {
            var status = await _mcpDependencyService.CheckAsync(_settingsService.Current, cancellationToken);
            if (status.IsComplete)
            {
                _settingsService.Current.UvExecutablePath = status.UvPath;
                _settingsService.Current.BunExecutablePath = status.BunPath;
                _settingsService.Save();
                return (true, status.UvPath);
            }

            progress?.Report("正在下载 MCP 依赖（uv.exe、bun.exe）……");
            var result = await _mcpDependencyService.InstallMissingAsync(_settingsService.Current, progress, cancellationToken);
            _settingsService.Save();
            await RefreshMcpDependencyStatusAsync();
            return (result.Success && !string.IsNullOrWhiteSpace(_settingsService.Current.UvExecutablePath),
                _settingsService.Current.UvExecutablePath);
        }
        catch (Exception ex)
        {
            AppLogger.Error("config", "EnsureUv 失败", ex);
            return (false, null);
        }
    }

    private async Task CheckUfoAsync()
    {
        UfoCheckButton.IsEnabled = false;
        UfoCheckButton.Content = "正在检测…";
        try
        {
            UfoStatusText.Text = "正在检测 UFO…";
            var python = UfoPythonBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(python))
            {
                UfoStatusText.Text = "UFO 状态：请先填写 UFO Python 路径。";
                UfoPythonBox.Focus();
                return;
            }

            var installer = new UfoInstaller();
            var status = await installer.CheckAsync(python);
            UfoStatusText.Text = status.Installed
                ? $"UFO 状态：可用。\nPython：{status.PythonPath}\n桥接脚本：{status.RunnerScript}\n源码目录：{status.UfoSourceDir}\n检测结果尚未保存。"
                : $"UFO 状态：不可用。{status.Error}";
            if (status.Installed)
            {
                MarkComputerControlDirty();
            }
        }
        catch (Exception ex)
        {
            UfoStatusText.Text = "UFO 检测失败：" + ex.Message;
        }
        finally
        {
            UfoCheckButton.IsEnabled = true;
            UfoCheckButton.Content = "检测 UFO 是否可用";
        }
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
            AppLogger.Error("config", "reload chat service failed", ex);
            return false;
        }
    }

    private void SetupBuiltinMcpServers(bool showNotification = true)
    {
        var plugin = new McpChatPlugin();
        var uv = ResolveBuiltinExecutablePath(_settingsService.Current.UvExecutablePath, "uv.exe");
        var bun = ResolveBuiltinExecutablePath(_settingsService.Current.BunExecutablePath, "bun.exe");

        var result = plugin.SetupBuiltinMcpServers(uv, bun, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // SetupBuiltinMcpServers 现已通过 McpServerStore.SaveServer 写入 mcp/servers/ 单文件，
        // 与 McpRuntimeService 读取位置一致，无需再依赖旧 mcp_servers.json 聚合文件。

        if (!result.Contains("失败", StringComparison.Ordinal) && !result.Contains("未找到", StringComparison.Ordinal))
        {
            TriggerMcpBackgroundReload();
        }

        _settingsService.Current.UvExecutablePath = string.IsNullOrWhiteSpace(uv) ? null : uv;
        _settingsService.Current.BunExecutablePath = string.IsNullOrWhiteSpace(bun) ? null : bun;
        _settingsService.Save();

        if (!showNotification)
        {
            return;
        }

        if (result.Contains("失败", StringComparison.Ordinal) || result.Contains("未找到", StringComparison.Ordinal))
        {
            ShowError(result);
        }
        else
        {
            ShowSuccess(result);
        }
    }


    private void TriggerMcpBackgroundReload()
    {
        if (_chatService is AemiChatService aemiChatService)
        {
            aemiChatService.ReloadMcpTools();
        }
    }

    /// <summary>初始化 MCP 面板并订阅其事件，把依赖下载/内置服务/重新加载接到本窗口的逻辑上。</summary>
    private void InitMcpPanel()
    {
        // 注入真实 store 与 reload 回调（面板 XAML 声明时用的是空默认 store）
        McpPanel.Configure(_mcpServerStore, TriggerMcpBackgroundReload);

        McpPanel.DownloadDependenciesRequested += async (_, _) => await DownloadMcpDependenciesAsync();
        McpPanel.SetupBuiltinRequested += (_, _) => SetupBuiltinMcpServers();
        McpPanel.ReloadRequested += (_, _) =>
        {
            TriggerMcpBackgroundReload();
            _ = RefreshMcpOverallStatusAsync();
        };

        // ChatService 报告 MCP 状态变化时，同步到面板顶部状态条
        if (_chatService is AemiChatService aemiChatService)
        {
            aemiChatService.McpStatusChanged += (_, status) => McpPanel.UpdateOverallStatus(status);
        }
    }

    /// <summary>初始化 Skill 面板：复用 ChatService 的 SkillService 实例，reload 回调触发 AI 重建。</summary>
    private void InitSkillPanel()
    {
        if (_chatService is AemiChatService aemiChatService)
        {
            SkillPanel.Configure(aemiChatService.SkillService, () => aemiChatService.ReloadSkills());
        }
    }

    /// <summary>切换到 MCP 配置 Tab（供聊天栏快速跳转调用）。</summary>
    public void SelectMcpTab()
    {
        _ = SelectMcpPageAsync();
    }

    private async Task SelectMcpPageAsync()
    {
        if (!await TryChangeSettingsPageAsync(SettingsPageId.Mcp))
        {
            return;
        }

        await RefreshMcpOverallStatusAsync();
        McpPanel.RefreshServerList();
    }

    private async Task RefreshMcpDependencyStatusAsync()
    {
        try
        {
            var status = await _mcpDependencyService.CheckAsync(_settingsService.Current);
            var uvText = status.UvExists ? status.UvPath : "未找到";
            var bunText = status.BunExists ? status.BunPath : "未找到";
            var summary = status.IsComplete
                ? $"MCP 依赖已就绪（uv.exe、bun.exe 均已找到）。下载目录：{McpDependencyService.DefaultBinDirectory}"
                : $"MCP 依赖未完整：{FormatMissingMcpDependencies(status)}。下载目录：{McpDependencyService.DefaultBinDirectory}";

            _lastMcpDependencySource ??= "本次尚未下载";
            McpPanel.UpdateDependencyStatus($"{summary}\nuv.exe：{uvText}\nbun.exe：{bunText}\n最近来源：{_lastMcpDependencySource}");
        }
        catch (Exception ex)
        {
            McpPanel.UpdateDependencyStatus($"MCP 依赖检测失败：{ex.Message}");
        }
    }

    private async Task DownloadMcpDependenciesAsync()
    {
        if (_isInstallingMcpDependencies)
        {
            return;
        }

        _isInstallingMcpDependencies = true;
        try
        {
            var status = await _mcpDependencyService.CheckAsync(_settingsService.Current);
            if (status.IsComplete)
            {
                ApplyMcpDependencyStatusToSettings(status);
                _settingsService.Save();
                _lastMcpDependencySource = "本地已存在";
                await RefreshMcpDependencyStatusAsync();
                ShowSuccess("已检测到 uv.exe 和 bun.exe，不必下载。");
                return;
            }

            var progress = new Progress<string>(message => McpPanel.UpdateDependencyStatus(message));
            var result = await _mcpDependencyService.InstallMissingAsync(_settingsService.Current, progress);
            _settingsService.Save();
            _lastMcpDependencySource = result.UsedMirrors.Count > 0
                ? string.Join("、", result.UsedMirrors)
                : "本次未下载";

            await RefreshMcpDependencyStatusAsync();
            if (result.Success)
            {
                SetupBuiltinMcpServers(showNotification: false);
                ShowSuccess(result.Message);
            }
            else
            {
                ShowError(result.Message);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("config", "mcp dependency install failed", ex);
            McpPanel.UpdateDependencyStatus($"MCP 依赖下载失败：{ex.Message}");
            ShowError($"MCP 依赖下载失败：{ex.Message}");
        }
        finally
        {
            _isInstallingMcpDependencies = false;
        }
    }

    /// <summary>把实时 McpStatus 同步到 MCP 面板顶部的整体状态条。</summary>
    private async Task RefreshMcpOverallStatusAsync()
    {
        await Task.Yield();
        if (_chatService is AemiChatService aemiChatService)
        {
            McpPanel.UpdateOverallStatus(aemiChatService.McpStatus);
        }
    }

    private void ApplyMcpDependencyStatusToSettings(McpDependencyStatus status)
    {
        if (status.UvExists)
        {
            _settingsService.Current.UvExecutablePath = status.UvPath;
        }

        if (status.BunExists)
        {
            _settingsService.Current.BunExecutablePath = status.BunPath;
        }
    }

    private static string FormatMissingMcpDependencies(McpDependencyStatus status)
    {
        var missing = new List<string>();
        if (!status.UvExists)
        {
            missing.Add("uv.exe");
        }

        if (!status.BunExists)
        {
            missing.Add("bun.exe");
        }

        return string.Join("、", missing);
    }

    private async Task PickAvatarAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择头像图片",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("图片") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } }
            }
        });
        if (files is not { Count: > 0 })
        {
            return;
        }

        var output = CropToCircularAvatar(files[0].TryGetLocalPath() ?? files[0].Name);
        AvatarCustomRadio.IsChecked = true;
        _settingsService.Current.CustomUserAvatarPath = output;
        SetPreviewImage(AvatarPreviewImage, output);
        SaveAvatarSelection();
        AppearanceStatusText.Text = "自定义头像已保存。";
    }

    private async Task PickChatBackgroundAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择聊天背景图",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("图片") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } }
            }
        });
        if (files is not { Count: > 0 })
        {
            return;
        }

        var output = CropToChatBackground(files[0].TryGetLocalPath() ?? files[0].Name);
        _settingsService.Current.ChatBackgroundImagePath = output;
        SetPreviewImage(ChatBackgroundPreviewImage, output);
        _settingsService.Save();
        AppearanceStatusText.Text = "聊天背景已保存。";
    }

    private static void SetPreviewImage(Avalonia.Controls.Image target, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ReleaseImageSource(target);
            target.Source = null;
            return;
        }

        try
        {
            ReleaseImageSource(target);
            target.Source = new Bitmap(path);
        }
        catch
        {
            ReleaseImageSource(target);
            target.Source = null;
        }
    }

    /// <summary>释放 Image 上旧的 Bitmap 源，避免反复更换预览图造成非托管内存累积（RES-007）。</summary>
    private static void ReleaseImageSource(Avalonia.Controls.Image target)
    {
        if (target.Source is Bitmap old)
        {
            target.Source = null;
            old.Dispose();
        }
    }

    private void RefreshAvatarPreviewFromSelection()
    {
        if (AvatarCustomRadio.IsChecked == true)
        {
            SetPreviewImage(AvatarPreviewImage, _settingsService.Current.CustomUserAvatarPath);
            return;
        }

        var uri = AvatarFemaleRadio.IsChecked == true
            ? "avares://Aemeath-agent/Assets/static/user-female.png"
            : "avares://Aemeath-agent/Assets/static/user-male.png";
        SetPreviewImageFromResource(AvatarPreviewImage, uri);
    }

    private static void SetPreviewImageFromResource(Avalonia.Controls.Image target, string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            ReleaseImageSource(target);
            target.Source = new Bitmap(stream);
        }
        catch
        {
            target.Source = null;
        }
    }

    private static string CropToCircularAvatar(string sourcePath)
    {
        var managedDir = GetManagedAssetDirectory();
        var output = Path.Combine(managedDir, "user_avatar_circle.png");

        using var image = ImageSharpImage.Load<Rgba32>(sourcePath);
        var side = Math.Min(image.Width, image.Height);
        var x = (image.Width - side) / 2;
        var y = (image.Height - side) / 2;

        image.Mutate(ctx => ctx.Crop(new Rectangle(x, y, side, side)).Resize(256, 256));
        for (var yy = 0; yy < image.Height; yy++)
        {
            for (var xx = 0; xx < image.Width; xx++)
            {
                var dx = xx - image.Width / 2.0;
                var dy = yy - image.Height / 2.0;
                var radius = image.Width / 2.0;
                if (dx * dx + dy * dy > radius * radius)
                {
                    image[xx, yy] = new Rgba32(0, 0, 0, 0);
                }
            }
        }

        image.SaveAsPng(output);
        return output;
    }

    private static string CropToChatBackground(string sourcePath)
    {
        var managedDir = GetManagedAssetDirectory();
        var output = Path.Combine(managedDir, "chat_background_rect.png");

        const double targetRatio = 4d / 3d;
        using var image = ImageSharpImage.Load<Rgba32>(sourcePath);

        var ratio = image.Width / (double)image.Height;
        Rectangle crop;
        if (ratio > targetRatio)
        {
            var targetWidth = (int)(image.Height * targetRatio);
            var x = (image.Width - targetWidth) / 2;
            crop = new Rectangle(x, 0, targetWidth, image.Height);
        }
        else
        {
            var targetHeight = (int)(image.Width / targetRatio);
            var y = (image.Height - targetHeight) / 2;
            crop = new Rectangle(0, y, image.Width, targetHeight);
        }

        image.Mutate(ctx => ctx.Crop(crop).Resize(1280, 960));
        image.SaveAsPng(output);
        return output;
    }

    private static string GetManagedAssetDirectory()
    {
        var appDir = Path.Combine(AppContext.BaseDirectory, "user-data");
        try
        {
            Directory.CreateDirectory(appDir);
            return appDir;
        }
        catch
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aemeath", "user-data");
            Directory.CreateDirectory(appData);
            return appData;
        }
    }

    private static string ResolveBuiltinExecutablePath(string? configuredPath, string exeName)
    {
        return McpDependencyService.ResolveExecutablePath(configuredPath, exeName) ?? string.Empty;
    }

    private static ProviderModel CloneModel(ProviderModel model)
        => new()
        {
            Id = model.Id,
            OwnedBy = model.OwnedBy,
            IsEnabled = model.IsEnabled,
            LastSeenAt = model.LastSeenAt,
            ContextLength = model.ContextLength,
            SupportsImageInput = model.SupportsImageInput,
            SupportsVideoInput = model.SupportsVideoInput,
            SupportsReasoning = model.SupportsReasoning
        };

    private void ShowSuccess(string message)
    {
        SettingsToastHost.ShowToast(
            AemeathToastKind.Success,
            message,
            _settingsService.Current.ReduceMotion);
    }

    private void ShowError(string message)
    {
        SettingsToastHost.ShowToast(
            AemeathToastKind.Error,
            message,
            _settingsService.Current.ReduceMotion);
    }

    protected override void OnClosed(EventArgs e)
    {
        _simpleSettingsSaveTimer.Stop();
        _flickerTimer.Stop();
        _particleEffect.Stop();
        base.OnClosed(e);
    }
}

