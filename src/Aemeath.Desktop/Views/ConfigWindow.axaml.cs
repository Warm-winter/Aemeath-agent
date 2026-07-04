using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
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

    private readonly SettingsService _settingsService;
    private readonly IChatService _chatService;
    private readonly Func<string?>? _currentSessionIdProvider;
    private readonly ParticleEffect _particleEffect;
    private readonly ProviderProbeService _providerProbeService = new();
    private readonly McpDependencyService _mcpDependencyService = new();
    private readonly MemoryOrchestrator _memoryOrchestrator;
    private readonly Mem0DependencyService _mem0DependencyService;
    private McpServerStore _mcpServerStore = new();
    private readonly DispatcherTimer _flickerTimer;
    private readonly List<ProviderModel> _currentModelCandidates = new();
    private bool _isLoadingProviderUi;
    private bool _isInstallingMcpDependencies;
    private bool _isInstallingMem0;
    private string? _selectedMemoryEntryId;
    private string _lastMcpDependencySource = "本次尚未下载";
    private double _flickerPhase;

    public ConfigWindow() : this(new SettingsService(), new NoOpChatService(), null)
    {
    }

    public ConfigWindow(
        SettingsService settingsService,
        IChatService chatService,
        Func<string?>? currentSessionIdProvider = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _chatService = chatService;
        _currentSessionIdProvider = currentSessionIdProvider;
        _particleEffect = new ParticleEffect(BackgroundParticleCanvas);

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

        NewProviderButton.Click += (_, _) => StartNewProviderForm();
        SaveProviderButton.Click += async (_, _) => await SaveProviderAsync();
        TestProviderButton.Click += async (_, _) => await TestProviderAsync();
        FetchModelsButton.Click += async (_, _) => await FetchModelsAsync();
        AddManualModelButton.Click += (_, _) => AddManualModel();
        ProviderPresetBox.SelectionChanged += (_, _) => ApplySelectedProviderPreset();

        SaveAllButton.Click += async (_, _) => await SaveNonProviderSettingsAsync();

        // MCP 面板：合并自原独立 McpConfigWindow。面板负责服务增删改/测试/导入，
        // 依赖下载与内置服务配置仍由本窗口处理（通过事件回调）。
        InitMcpPanel();
        InitSkillPanel();

        BrowseAvatarButton.Click += async (_, _) => await PickAvatarAsync();
        AvatarMaleRadio.Click += (_, _) => RefreshAvatarPreviewFromSelection();
        AvatarFemaleRadio.Click += (_, _) => RefreshAvatarPreviewFromSelection();
        AvatarCustomRadio.Click += (_, _) => RefreshAvatarPreviewFromSelection();
        BrowseChatBackgroundButton.Click += async (_, _) => await PickChatBackgroundAsync();
        ClearChatBackgroundButton.Click += async (_, _) =>
        {
            _settingsService.Current.ChatBackgroundImagePath = null;
            SetPreviewImage(ChatBackgroundPreviewImage, null);
            await SaveNonProviderSettingsAsync();
        };

        DeleteMemoryButton.Click += async (_, _) => await DeleteSelectedMemoryAsync();
        ClearCurrentSessionMemoryButton.Click += async (_, _) => await ClearCurrentSessionMemoryAsync();
        ClearAllMemoryButton.Click += async (_, _) => await ClearAllMemoryAsync();
        Mem0InstallButton.Click += async (_, _) => await InstallMem0Async();
        UfoCheckButton.Click += async (_, _) => await CheckUfoAsync();

        Opened += (_, _) =>
        {
            _flickerTimer.Start();
            if (_settingsService.Current.EnableParticleEffects)
            {
                _particleEffect.Start(90);
            }

            _ = RefreshMcpDependencyStatusAsync();
            // 不再在每次打开设置窗口时自动调用 SetupBuiltinMcpServers。
            // 内置服务配置只在用户显式点击「一键配置内置服务」时执行。
            _ = RefreshMcpOverallStatusAsync();
        };

        PopulateProviderPresets();
        PopulatePetSizeOptions();
        LoadFromSettings();
        _ = RefreshMemoryListAsync();
        _ = RefreshMem0StatusAsync();

        // 订阅 Provider/Model 变更事件：用户在「提供商配置」Tab 保存新模型列表后，
        // 自动刷新「电脑控制」Tab 的视觉模型下拉框，并尽量保留当前选择。
        _settingsService.ProvidersChanged += OnProvidersChangedRefreshVision;
        Closed += (_, _) => _settingsService.ProvidersChanged -= OnProvidersChangedRefreshVision;
    }

    private void LoadFromSettings()
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
        EnablePetBubblesBox.IsChecked = _settingsService.Current.EnablePetBubbles;
        // 辅助视觉模型（电脑控制面板）：提供商 + 模型选择，与对话 Provider 打通
        PopulateVisionProviderBox();
        UfoPythonBox.Text = _settingsService.Current.UfoPythonPath ?? string.Empty;
        ComputerControlBackendBox.SelectedIndex = _settingsService.Current.ComputerControlBackend?.ToLowerInvariant() switch
        {
            "uia" => 1,
            "ufo" => 2,
            _ => 0
        };
        EnablePetIdleGreetingBox.IsChecked = _settingsService.Current.EnablePetIdleGreeting;
        EnablePetEdgeSnapBox.IsChecked = _settingsService.Current.EnablePetEdgeSnap;
        PetOpacitySlider.Value = Math.Clamp(_settingsService.Current.PetOpacity, 0.65, 1.0);
        SelectPetSize(_settingsService.Current.PetSizePreset);

        var avatarType = _settingsService.Current.UserAvatarType;
        AvatarMaleRadio.IsChecked = avatarType == "male";
        AvatarFemaleRadio.IsChecked = avatarType == "female";
        AvatarCustomRadio.IsChecked = avatarType == "custom";
        RefreshAvatarPreviewFromSelection();
        SetPreviewImage(ChatBackgroundPreviewImage, _settingsService.Current.ChatBackgroundImagePath);
        _ = RefreshMcpDependencyStatusAsync();
    }

    private async Task SaveProviderAsync()
    {
        var provider = string.IsNullOrWhiteSpace(ProviderNameBox.Text) ? "openai" : ProviderNameBox.Text.Trim();
        var apiKey = ApiKeyBox.Text?.Trim() ?? string.Empty;
        var endpoint = string.IsNullOrWhiteSpace(EndpointBox.Text) ? null : EndpointBox.Text.Trim();
        var defaultModel = GetSelectedDefaultModel();
        if (string.IsNullOrWhiteSpace(defaultModel))
        {
            ShowError("请先填写或选择一个默认模型。");
            return;
        }

        var models = CollectModels(defaultModel).ToList();
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
        await Task.CompletedTask;
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
        var provider = string.IsNullOrWhiteSpace(ProviderNameBox.Text) ? "openai" : ProviderNameBox.Text.Trim();
        var apiKey = ApiKeyBox.Text?.Trim() ?? string.Empty;
        var endpoint = string.IsNullOrWhiteSpace(EndpointBox.Text) ? null : EndpointBox.Text.Trim();
        ProviderStatusText.Text = updateModels ? "正在获取模型..." : "正在测试连接...";

        var result = await _providerProbeService.FetchModelsAsync(provider, apiKey, endpoint);
        _settingsService.UpdateProviderConnectionStatus(provider, result.Status, result.Message);

        if (result.Success && updateModels)
        {
            _currentModelCandidates.Clear();
            _currentModelCandidates.AddRange(result.Models.Select(CloneModel));
            RenderModels(GetSelectedDefaultModel());
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

    private async Task SaveNonProviderSettingsAsync()
    {
        _settingsService.Current.EnableAlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _settingsService.Current.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        var enableAutoStart = AutoStartBox.IsChecked == true;
        try
        {
            AutoStartService.SetEnabled(enableAutoStart);
            _settingsService.Current.EnableAutoStart = enableAutoStart;
        }
        catch (Exception ex)
        {
            AppLogger.Error("config", "failed to save auto start state", ex);
            ShowError($"开机自启动设置失败：{ex.Message}");
            return;
        }

        _settingsService.Current.EnableParticleEffects = ParticleEffectsBox.IsChecked == true;
        _settingsService.Current.EnablePetBubbles = EnablePetBubblesBox.IsChecked == true;
        // 辅助视觉模型
        SaveVisionProviderSelection();
        _settingsService.Current.UfoPythonPath = string.IsNullOrWhiteSpace(UfoPythonBox.Text) ? null : UfoPythonBox.Text.Trim();
        if (ComputerControlBackendBox.SelectedItem is ComboBoxItem { Tag: string backendTag })
        {
            _settingsService.Current.ComputerControlBackend = backendTag;
        }
        _settingsService.Current.EnablePetIdleGreeting = EnablePetIdleGreetingBox.IsChecked == true;
        _settingsService.Current.EnablePetEdgeSnap = EnablePetEdgeSnapBox.IsChecked == true;
        _settingsService.Current.PetOpacity = Math.Clamp(PetOpacitySlider.Value, 0.65, 1.0);
        if (PetSizeBox.SelectedItem is ComboBoxItem { Tag: string petSize })
        {
            ApplyPetSizePresetToSettings(petSize);
        }
        _settingsService.Current.EnableVoiceInput = true;
        _settingsService.Current.AzureSpeechKey = null;
        _settingsService.Current.AzureSpeechRegion = null;

        _settingsService.Current.UserAvatarType = AvatarCustomRadio.IsChecked == true
            ? "custom"
            : AvatarFemaleRadio.IsChecked == true
                ? "female"
                : "male";

        _settingsService.Save();
        ShowSuccess("设置已保存。");
        await Task.CompletedTask;
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

        ProviderPresetBox.SelectedIndex = 0;
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
        var modelCount = info?.Models.Count(m => m.IsEnabled) ?? 0;
        var status = string.IsNullOrWhiteSpace(info?.LastConnectionMessage)
            ? "尚未测试"
            : info!.LastConnectionMessage;

        var border = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14),
            Background = AemiUi.Brush(isCurrent ? "#FFE1EE" : "#FFFFFF"),
            BorderBrush = AemiUi.Brush(isCurrent ? AemiUi.Star : AemiUi.Border),
            BorderThickness = new Thickness(1)
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(AemiUi.Badge(isCurrent ? "当前链路" : "Provider Archive", isCurrent ? "star" : "halo"));
        panel.Children.Add(new TextBlock
        {
            Text = isCurrent ? $"{provider}  当前" : provider,
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
            Text = $"模型：{info?.ModelId ?? _settingsService.Current.DefaultModel} / 已启用 {modelCount} 个",
            Classes = { "subtle" },
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = status,
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        var editButton = new Button { Content = "编辑", Classes = { "ghost" }, MinWidth = 64 };
        editButton.Content = "编辑";
        editButton.Click += (_, _) => LoadProviderIntoForm(provider);
        var useButton = new Button { Content = "使用", Classes = { "primary" }, MinWidth = 64, IsEnabled = !isCurrent };
        useButton.Content = "使用";
        useButton.Click += (_, _) => UseProvider(provider);
        var deleteButton = new Button { Content = "删除", Classes = { "danger" }, MinWidth = 64 };
        deleteButton.Content = "删除";
        deleteButton.Click += async (_, _) => await DeleteProviderAsync(provider);

        buttons.Children.Add(editButton);
        buttons.Children.Add(useButton);
        buttons.Children.Add(deleteButton);
        panel.Children.Add(buttons);
        border.Child = panel;
        return border;
    }

    private void LoadProviderIntoForm(string provider)
    {
        _isLoadingProviderUi = true;
        try
        {
            var normalized = SettingsService.NormalizeProviderName(provider);
            var info = _settingsService.GetApiKeyInfo(normalized);
            var defaultModel = info?.ModelId ?? _settingsService.Current.DefaultModel;
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
                _currentModelCandidates.All(m => !string.Equals(m.Id, defaultModel, StringComparison.OrdinalIgnoreCase)))
            {
                _currentModelCandidates.Add(new ProviderModel { Id = defaultModel, IsEnabled = true });
            }

            RenderModels(defaultModel);
        }
        finally
        {
            _isLoadingProviderUi = false;
        }
    }

    private void StartNewProviderForm()
    {
        ProviderNameBox.Text = string.Empty;
        ApiKeyBox.Text = string.Empty;
        EndpointBox.Text = string.Empty;
        ProviderStatusText.Text = "填写信息后保存，或先测试连接。";
        _currentModelCandidates.Clear();
        _currentModelCandidates.Add(new ProviderModel { Id = "gpt-4o", IsEnabled = true });
        RenderModels("gpt-4o");
    }

    private void ApplySelectedProviderPreset()
    {
        if (_isLoadingProviderUi || ProviderPresetBox.SelectedItem is not ComboBoxItem { Tag: ProviderPreset preset })
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(preset.Provider))
        {
            return;
        }

        var existing = _settingsService.GetApiKeyInfo(preset.Provider);
        ProviderNameBox.Text = preset.Provider;
        EndpointBox.Text = preset.Endpoint ?? string.Empty;
        ApiKeyBox.Text = existing?.Key ?? string.Empty;
        _currentModelCandidates.Clear();
        if (existing is not null)
        {
            _currentModelCandidates.AddRange(existing.Models.Select(CloneModel));
        }

        if (_currentModelCandidates.All(m => !string.Equals(m.Id, preset.ModelId, StringComparison.OrdinalIgnoreCase)))
        {
            _currentModelCandidates.Add(new ProviderModel { Id = preset.ModelId, IsEnabled = true });
        }

        RenderModels(existing?.ModelId ?? preset.ModelId);
    }

    private void AddManualModel()
    {
        var modelId = ManualModelBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        if (_currentModelCandidates.All(m => !string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            _currentModelCandidates.Add(new ProviderModel
            {
                Id = modelId,
                IsEnabled = true,
                LastSeenAt = DateTimeOffset.UtcNow
            });
        }

        ManualModelBox.Text = string.Empty;
        RenderModels(modelId);
    }

    private void RenderModels(string? selectedModel)
    {
        ModelsPanel.Children.Clear();
        DefaultModelBox.SelectedItem = null;
        DefaultModelBox.SelectedIndex = -1;
        DefaultModelBox.Items.Clear();

        foreach (var model in _currentModelCandidates.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            var checkBox = new CheckBox
            {
                Content = string.IsNullOrWhiteSpace(model.OwnedBy) ? model.Id : $"{model.Id}  ({model.OwnedBy})",
                Tag = model.Id,
                IsChecked = model.IsEnabled,
                Foreground = AemiUi.Brush(AemiUi.Ghost),
                Padding = new Thickness(6, 4),
                Margin = new Thickness(0, 0, 0, 2)
            };
            ModelsPanel.Children.Add(checkBox);

            var item = new ComboBoxItem
            {
                Content = model.Id,
                Tag = model.Id
            };
            DefaultModelBox.Items.Add(item);
            if (string.Equals(model.Id, selectedModel, StringComparison.OrdinalIgnoreCase))
            {
                DefaultModelBox.SelectedItem = item;
            }
        }

        if (DefaultModelBox.SelectedItem is null && DefaultModelBox.Items.Count > 0)
        {
            DefaultModelBox.SelectedIndex = 0;
        }
    }

    private string GetSelectedDefaultModel()
    {
        if (DefaultModelBox.SelectedItem is ComboBoxItem { Tag: string model })
        {
            return model;
        }

        return _currentModelCandidates.FirstOrDefault(m => m.IsEnabled)?.Id ?? string.Empty;
    }

    private IEnumerable<ProviderModel> CollectModels(string defaultModel)
    {
        var enabledIds = ModelsPanel.Children
            .OfType<CheckBox>()
            .Where(c => c.IsChecked == true && c.Tag is string)
            .Select(c => (string)c.Tag!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 注意：不再强制将 defaultModel 加入 enabledIds。
        // 默认模型的启用/禁用状态完全由用户在 UI 中的勾选决定。

        return _currentModelCandidates
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .Select(m =>
            {
                var clone = CloneModel(m);
                clone.IsEnabled = enabledIds.Contains(clone.Id);
                return clone;
            })
            .Concat(enabledIds
                .Where(id => _currentModelCandidates.All(m => !string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
                .Select(id => new ProviderModel { Id = id, IsEnabled = true, LastSeenAt = DateTimeOffset.UtcNow }))
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last());
    }

    private void UseProvider(string provider)
    {
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
        if (!await ConfirmAsync("删除提供商", $"确定删除 {provider} 吗？"))
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
        LoadProviderIntoForm(_settingsService.Current.CurrentProvider);
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
        MemoryListBox.Items.Clear();
        _selectedMemoryEntryId = null;
        MemoryStatusText.Text = "正在从 Mem0 读取记忆……";

        try
        {
            // 列出全局用户记忆 + 当前会话记忆
            var sessionId = _currentSessionIdProvider?.Invoke();
            var client = await GetMem0ClientAsync();
            if (client is null)
            {
                MemoryStatusText.Text = "Mem0 未启用（请在下方安装依赖并配置 Provider）。";
                return;
            }

            var globalTask = client.GetAllAsync(Mem0Scope.GlobalUser, topK: 100);
            Task<System.Text.Json.JsonElement> sessionTask;
            if (sessionId is null)
            {
                sessionTask = Task.FromResult<System.Text.Json.JsonElement>(default);
            }
            else
            {
                sessionTask = client.GetAllAsync(Mem0Scope.ForSession(sessionId), topK: 100);
            }

            await Task.WhenAll(globalTask, sessionTask);

            AddMem0ItemsToBox(globalTask.Result, "全局");
            if (sessionId is not null)
            {
                AddMem0ItemsToBox(sessionTask.Result, "会话");
            }

            MemoryStatusText.Text = MemoryListBox.Items.Count == 0
                ? "暂无长期记忆。"
                : $"共 {MemoryListBox.Items.Count} 条长期记忆（Mem0）。";
        }
        catch (Exception ex)
        {
            AppLogger.Error("memory", "ConfigWindow 读取记忆失败", ex);
            MemoryStatusText.Text = "读取记忆失败：" + ex.Message;
        }
    }

    private void AddMem0ItemsToBox(System.Text.Json.JsonElement resp, string scopeLabel)
    {
        if (resp.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        if (!resp.TryGetProperty("results", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array) return;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String ? idEl.GetString() : null;
            var text = item.TryGetProperty("memory", out var memEl) && memEl.ValueKind == System.Text.Json.JsonValueKind.String ? memEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text)) continue;

            var preview = text!.Length <= 42 ? text : text[..42] + "...";
            MemoryListBox.Items.Add(new ListBoxItem
            {
                Content = $"{scopeLabel}：{preview}",
                Tag = $"{id}||{text}"
            });
        }
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
        // 选中一条记忆时记录其 id，供删除使用（编辑功能已按需求移除）
        if (MemoryListBox.SelectedItem is ListBoxItem { Tag: string tag })
        {
            var parts = tag.Split("||", 2);
            _selectedMemoryEntryId = parts.Length == 2 ? parts[0] : null;
        }
    }

    private async Task DeleteSelectedMemoryAsync()
    {
        OnMemorySelectionChanged();
        if (string.IsNullOrWhiteSpace(_selectedMemoryEntryId))
        {
            ShowError("请先选择一条记忆。");
            return;
        }

        if (!await ConfirmAsync("删除记忆", "确定删除选中的长期记忆吗？"))
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

        if (!await ConfirmAsync("清空当前会话记忆", "确定清空当前会话的长期记忆吗？"))
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
        if (!await ConfirmAsync("清空全部记忆", "确定清空全部长期记忆吗？这个操作不能恢复。"))
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
    private void PopulateVisionProviderBox()
    {
        VisionProviderBox.Items.Clear();
        // 第一项：复用当前对话提供商
        var reuseItem = new ComboBoxItem { Content = "复用当前对话提供商", Tag = null as string };
        VisionProviderBox.Items.Add(reuseItem);

        var providers = _settingsService.ListProviders();
        ComboBoxItem? selected = reuseItem;
        foreach (var p in providers)
        {
            var item = new ComboBoxItem { Content = p, Tag = p };
            VisionProviderBox.Items.Add(item);
            if (string.Equals(p, _settingsService.Current.VisionProvider, StringComparison.OrdinalIgnoreCase))
            {
                selected = item;
            }
        }
        VisionProviderBox.SelectedItem = selected;

        PopulateVisionModelBox();
        VisionProviderBox.SelectionChanged += (_, _) => PopulateVisionModelBox();

        // 若已有保存的视觉模型，回填
        var savedModel = _settingsService.Current.VisionModel;
        if (!string.IsNullOrWhiteSpace(savedModel))
        {
            VisionModelBox.Text = savedModel;
        }

        // 视觉 Key 不回填明文（安全），留空表示复用提供商 Key
    }

    /// <summary>根据当前选中的视觉提供商，填充其已获取的模型列表（便于选择支持图片的模型）。</summary>
    private void PopulateVisionModelBox()
    {
        VisionModelBox.Items.Clear();
        var provider = (VisionProviderBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(provider))
        {
            provider = _settingsService.Current.CurrentProvider;
        }

        foreach (var m in _settingsService.GetProviderModels(provider, enabledOnly: true))
        {
            VisionModelBox.Items.Add(new ComboBoxItem { Content = m.Id, Tag = m.Id });
        }
    }

    /// <summary>
    /// 当 Provider/Model 配置变更时，刷新视觉模型下拉框，并尽量保留用户当前选择。
    /// 通过 Dispatcher.UIThread.Post 切到 UI 线程执行，避免后台线程操作控件。
    /// </summary>
    private void OnProvidersChangedRefreshVision()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 记录当前选择
            var prevProviderTag = (VisionProviderBox.SelectedItem as ComboBoxItem)?.Tag as string;
            var prevModel = VisionModelBox.Text?.Trim();

            // 重新填充
            PopulateVisionProviderBox();

            // 尝试恢复 Provider 选择
            if (prevProviderTag is not null)
            {
                for (int i = 0; i < VisionProviderBox.Items.Count; i++)
                {
                    if (VisionProviderBox.Items[i] is ComboBoxItem item &&
                        string.Equals(item.Tag as string, prevProviderTag, StringComparison.OrdinalIgnoreCase))
                    {
                        VisionProviderBox.SelectedIndex = i;
                        break;
                    }
                }
            }

            // PopulateVisionProviderBox 内部会调用 PopulateVisionModelBox，模型列表已刷新
            // 尝试恢复 Model 选择（若新列表中仍存在）
            if (!string.IsNullOrWhiteSpace(prevModel))
            {
                foreach (var item in VisionModelBox.Items)
                {
                    if (item is ComboBoxItem cbItem &&
                        string.Equals(cbItem.Tag as string, prevModel, StringComparison.OrdinalIgnoreCase))
                    {
                        VisionModelBox.Text = prevModel;
                        break;
                    }
                }
            }
        });
    }

    private void SaveVisionProviderSelection()
    {
        var provider = (VisionProviderBox.SelectedItem as ComboBoxItem)?.Tag as string;
        _settingsService.Current.VisionProvider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim();

        var model = VisionModelBox.Text?.Trim();
        _settingsService.Current.VisionModel = string.IsNullOrWhiteSpace(model) ? null : model;

        // VisionApiKey 复用所选提供商已保存的 Key，无需独立输入
        // 选择新提供商时，旧的独立端点/Key 若为空则自动用该提供商
        if (string.IsNullOrWhiteSpace(_settingsService.Current.VisionEndpoint))
        {
            _settingsService.Current.VisionEndpoint = null;
        }
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
        try
        {
            UfoStatusText.Text = "正在检测 UFO…";
            var python = UfoPythonBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(python))
            {
                python = _settingsService.Current.UfoPythonPath;
            }

            if (string.IsNullOrWhiteSpace(python))
            {
                UfoStatusText.Text = "UFO 状态：未填写 UFO Python 路径。请先克隆 UFO 并 pip install -r requirements.txt，再把其 venv 的 python.exe 路径填入上方。";
                return;
            }

            var installer = new UfoInstaller();
            var status = await installer.CheckAsync(python);
            UfoStatusText.Text = status.Installed
                ? $"UFO 状态：可用。\nPython：{status.PythonPath}\n桥接脚本：{status.RunnerScript}\n（源码目录：{status.UfoSourceDir}）"
                : $"UFO 状态：不可用。{status.Error}";

            // 检测通过则记住 python 路径
            if (status.Installed && !string.IsNullOrWhiteSpace(python))
            {
                _settingsService.Current.UfoPythonPath = python;
                _settingsService.Save();
            }
        }
        catch (Exception ex)
        {
            UfoStatusText.Text = "UFO 检测失败：" + ex.Message;
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,            Height = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = AemiUi.Brush(AemiUi.Void)
        };

        var result = false;
        var okButton = new Button { Content = "确认", Classes = { "danger" }, MinWidth = 86 };
        var cancelButton = new Button { Content = "取消", Classes = { "ghost" }, MinWidth = 86 };
        okButton.Content = "确认";
        cancelButton.Content = "取消";
        okButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        dialog.Content = new Border
        {
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(18),
            Background = AemiUi.Brush(AemiUi.Glass),
            BorderBrush = AemiUi.Brush(AemiUi.Star),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    AemiUi.Badge("危险档案确认 · Manual Gate", "star"),
                    new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = AemiUi.Brush(AemiUi.Ghost) },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Classes = { "subtle" } },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = { cancelButton, okButton }
                    }
                }
            }
        };

        await dialog.ShowDialog(this);
        return result;
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
        _mcpServerStore = new McpServerStore();
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
        if (SettingsTabControl.Items.Count > 3)
        {
            // Tab 顺序：0=提供商配置, 1=记忆管理, 2=电脑控制, 3=MCP配置
            SettingsTabControl.SelectedIndex = 3;
        }

        _ = RefreshMcpOverallStatusAsync();
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
        await SaveNonProviderSettingsAsync();
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
        await SaveNonProviderSettingsAsync();
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

        image.Mutate(ctx => ctx.Crop(crop).Resize(1400, 1050));
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
        new WindowNotificationManager(this)
        {
            Position = NotificationPosition.TopRight,
            MaxItems = 1
        }.Show(new Notification("成功", message, NotificationType.Success));
    }

    private void ShowError(string message)
    {
        new WindowNotificationManager(this)
        {
            Position = NotificationPosition.TopRight,
            MaxItems = 1
        }.Show(new Notification("失败", message, NotificationType.Error));
    }

    protected override void OnClosed(EventArgs e)
    {
        _flickerTimer.Stop();
        _particleEffect.Stop();
        base.OnClosed(e);
    }
}

