using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Aemeath.Core.AI;
using Aemeath.Core.Configuration;
using Aemeath.Core.MCP;
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
    private readonly Action? _wakeWordSettingsChanged;
    private readonly Func<string?>? _currentSessionIdProvider;
    private readonly ParticleEffect _particleEffect;
    private readonly ProviderProbeService _providerProbeService = new();
    private readonly McpDependencyService _mcpDependencyService = new();
    private readonly LongTermMemoryStore _memoryStore = new();
    private readonly DispatcherTimer _flickerTimer;
    private readonly List<ProviderModel> _currentModelCandidates = new();
    private bool _isLoadingProviderUi;
    private bool _isInstallingMcpDependencies;
    private string? _selectedMemoryEntryId;
    private string _lastMcpDependencySource = "本次尚未下载";
    private double _flickerPhase;

    public ConfigWindow() : this(new SettingsService(), new NoOpChatService(), null, null)
    {
    }

    public ConfigWindow(
        SettingsService settingsService,
        IChatService chatService,
        Action? wakeWordSettingsChanged = null,
        Func<string?>? currentSessionIdProvider = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _chatService = chatService;
        _wakeWordSettingsChanged = wakeWordSettingsChanged;
        _currentSessionIdProvider = currentSessionIdProvider;
        _particleEffect = new ParticleEffect(BackgroundParticleCanvas);

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
        OpenMcpConfigButton.Click += (_, _) => new McpConfigWindow().ShowDialog(this);
        SetupBuiltinMcpButton.Click += (_, _) => SetupBuiltinMcpServers();
        DownloadMcpDependenciesButton.Click += async (_, _) => await DownloadMcpDependenciesAsync();

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

        MemoryListBox.SelectionChanged += (_, _) => LoadSelectedMemory();
        SaveMemoryButton.Click += (_, _) => SaveSelectedMemory();
        DeleteMemoryButton.Click += async (_, _) => await DeleteSelectedMemoryAsync();
        ClearCurrentSessionMemoryButton.Click += async (_, _) => await ClearCurrentSessionMemoryAsync();
        ClearAllMemoryButton.Click += async (_, _) => await ClearAllMemoryAsync();

        Opened += (_, _) =>
        {
            _flickerTimer.Start();
            if (_settingsService.Current.EnableParticleEffects)
            {
                _particleEffect.Start(90);
            }

            _ = RefreshMcpDependencyStatusAsync();
            SetupBuiltinMcpServers(showNotification: false);
        };

        PopulateProviderPresets();
        PopulatePetSizeOptions();
        LoadFromSettings();
        RefreshMemoryList();
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
        ParticleEffectsBox.IsChecked = _settingsService.Current.EnableParticleEffects;
        EnablePetBubblesBox.IsChecked = _settingsService.Current.EnablePetBubbles;
        EnablePetIdleGreetingBox.IsChecked = _settingsService.Current.EnablePetIdleGreeting;
        EnablePetEdgeSnapBox.IsChecked = _settingsService.Current.EnablePetEdgeSnap;
        PetOpacitySlider.Value = Math.Clamp(_settingsService.Current.PetOpacity, 0.65, 1.0);
        SelectPetSize(_settingsService.Current.PetSizePreset);
        EnableWakeWordBox.IsChecked = _settingsService.Current.EnableWakeWord;
        PicovoiceAccessKeyBox.Text = _settingsService.Current.PicovoiceAccessKey ?? string.Empty;

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
        _settingsService.Current.EnableParticleEffects = ParticleEffectsBox.IsChecked == true;
        _settingsService.Current.EnablePetBubbles = EnablePetBubblesBox.IsChecked == true;
        _settingsService.Current.EnablePetIdleGreeting = EnablePetIdleGreetingBox.IsChecked == true;
        _settingsService.Current.EnablePetEdgeSnap = EnablePetEdgeSnapBox.IsChecked == true;
        _settingsService.Current.PetOpacity = Math.Clamp(PetOpacitySlider.Value, 0.65, 1.0);
        if (PetSizeBox.SelectedItem is ComboBoxItem { Tag: string petSize })
        {
            ApplyPetSizePresetToSettings(petSize);
        }
        _settingsService.Current.EnableVoiceInput = true;
        _settingsService.Current.EnableWakeWord = EnableWakeWordBox.IsChecked == true;
        _settingsService.Current.PicovoiceAccessKey = string.IsNullOrWhiteSpace(PicovoiceAccessKeyBox.Text)
            ? null
            : PicovoiceAccessKeyBox.Text.Trim();
        _settingsService.Current.AzureSpeechKey = null;
        _settingsService.Current.AzureSpeechRegion = null;

        _settingsService.Current.UserAvatarType = AvatarCustomRadio.IsChecked == true
            ? "custom"
            : AvatarFemaleRadio.IsChecked == true
                ? "female"
                : "male";

        _settingsService.Save();
        _wakeWordSettingsChanged?.Invoke();
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
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Background = new SolidColorBrush(AvaloniaColor.Parse(isCurrent ? "#243B66" : "#142035")),
            BorderBrush = new SolidColorBrush(AvaloniaColor.Parse(isCurrent ? "#8AD2FF" : "#344A73")),
            BorderThickness = new Thickness(1)
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = isCurrent ? $"{provider}  当前" : provider,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White
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
        editButton.Click += (_, _) => LoadProviderIntoForm(provider);
        var useButton = new Button { Content = "使用", Classes = { "primary" }, MinWidth = 64, IsEnabled = !isCurrent };
        useButton.Click += (_, _) => UseProvider(provider);
        var deleteButton = new Button { Content = "删除", Classes = { "danger" }, MinWidth = 64 };
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
        DefaultModelBox.Items.Clear();

        foreach (var model in _currentModelCandidates.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            var checkBox = new CheckBox
            {
                Content = string.IsNullOrWhiteSpace(model.OwnedBy) ? model.Id : $"{model.Id}  ({model.OwnedBy})",
                Tag = model.Id,
                IsChecked = model.IsEnabled
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
            .Append(defaultModel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

    private void RefreshMemoryList()
    {
        MemoryListBox.Items.Clear();
        foreach (var entry in _memoryStore.ListEntries())
        {
            MemoryListBox.Items.Add(new ListBoxItem
            {
                Content = FormatMemoryEntry(entry),
                Tag = entry
            });
        }

        MemoryStatusText.Text = MemoryListBox.Items.Count == 0 ? "暂无长期记忆。" : $"共 {MemoryListBox.Items.Count} 条长期记忆。";
        MemoryEditorBox.Text = string.Empty;
        _selectedMemoryEntryId = null;
    }

    private static string FormatMemoryEntry(LongTermMemoryEntry entry)
    {
        var scope = string.Equals(entry.Scope, "global", StringComparison.OrdinalIgnoreCase) ? "全局" : "会话";
        var content = entry.Content.Length <= 42 ? entry.Content : entry.Content[..42] + "...";
        return $"{scope} / {entry.Category}：{content}";
    }

    private void LoadSelectedMemory()
    {
        if (MemoryListBox.SelectedItem is not ListBoxItem { Tag: LongTermMemoryEntry entry })
        {
            return;
        }

        _selectedMemoryEntryId = entry.Id;
        MemoryEditorBox.Text = entry.Content;
        MemoryStatusText.Text = $"{entry.Scope} / {entry.Category}，更新时间：{entry.UpdatedAt:yyyy-MM-dd HH:mm}";
    }

    private void SaveSelectedMemory()
    {
        if (MemoryListBox.SelectedItem is not ListBoxItem { Tag: LongTermMemoryEntry entry } ||
            string.IsNullOrWhiteSpace(_selectedMemoryEntryId))
        {
            ShowError("请先选择一条记忆。");
            return;
        }

        entry.Content = MemoryEditorBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(entry.Content))
        {
            ShowError("记忆内容不能为空。");
            return;
        }

        _memoryStore.UpsertEntry(entry);
        RefreshMemoryList();
        ShowSuccess("记忆已更新。");
    }

    private async Task DeleteSelectedMemoryAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedMemoryEntryId))
        {
            ShowError("请先选择一条记忆。");
            return;
        }

        if (!await ConfirmAsync("删除记忆", "确定删除选中的长期记忆吗？"))
        {
            return;
        }

        _memoryStore.DeleteEntry(_selectedMemoryEntryId);
        RefreshMemoryList();
        ShowSuccess("记忆已删除。");
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

        _memoryStore.ClearSession(sessionId);
        RefreshMemoryList();
        ShowSuccess("当前会话记忆已清空。");
    }

    private async Task ClearAllMemoryAsync()
    {
        if (!await ConfirmAsync("清空全部记忆", "确定清空全部长期记忆吗？这个操作不能恢复。"))
        {
            return;
        }

        _memoryStore.ClearAll();
        RefreshMemoryList();
        ShowSuccess("全部长期记忆已清空。");
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(AvaloniaColor.Parse("#10182A"))
        };

        var result = false;
        var okButton = new Button { Content = "确认", Classes = { "danger" }, MinWidth = 86 };
        var cancelButton = new Button { Content = "取消", Classes = { "ghost" }, MinWidth = 86 };
        okButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        dialog.Content = new Border
        {
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
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

    private async Task RefreshMcpDependencyStatusAsync()
    {
        try
        {
            var status = await _mcpDependencyService.CheckAsync(_settingsService.Current);
            McpDownloadDirectoryText.Text = McpDependencyService.DefaultBinDirectory;
            McpUvPathText.Text = status.UvExists ? status.UvPath : "未找到";
            McpBunPathText.Text = status.BunExists ? status.BunPath : "未找到";
            McpLastMirrorText.Text = _lastMcpDependencySource;
            McpDependencyStatusText.Text = status.IsComplete
                ? "MCP 依赖已就绪。"
                : $"MCP 依赖未完整：{FormatMissingMcpDependencies(status)}";
        }
        catch (Exception ex)
        {
            McpDependencyStatusText.Text = $"MCP 依赖检测失败：{ex.Message}";
        }
    }

    private async Task DownloadMcpDependenciesAsync()
    {
        if (_isInstallingMcpDependencies)
        {
            return;
        }

        _isInstallingMcpDependencies = true;
        DownloadMcpDependenciesButton.IsEnabled = false;
        SetupBuiltinMcpButton.IsEnabled = false;
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

            var progress = new Progress<string>(message => McpDependencyStatusText.Text = message);
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
            McpDependencyStatusText.Text = $"MCP 依赖下载失败：{ex.Message}";
            ShowError($"MCP 依赖下载失败：{ex.Message}");
        }
        finally
        {
            _isInstallingMcpDependencies = false;
            DownloadMcpDependenciesButton.IsEnabled = true;
            SetupBuiltinMcpButton.IsEnabled = true;
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
#pragma warning disable CS0618
        var dialog = new OpenFileDialog
        {
            AllowMultiple = false,
            Filters =
            {
                new FileDialogFilter { Name = "图片", Extensions = { "png", "jpg", "jpeg", "webp" } }
            }
        };
        var files = await dialog.ShowAsync(this);
#pragma warning restore CS0618
        if (files is not { Length: > 0 })
        {
            return;
        }

        var output = CropToCircularAvatar(files[0]);
        AvatarCustomRadio.IsChecked = true;
        _settingsService.Current.CustomUserAvatarPath = output;
        SetPreviewImage(AvatarPreviewImage, output);
        await SaveNonProviderSettingsAsync();
    }

    private async Task PickChatBackgroundAsync()
    {
#pragma warning disable CS0618
        var dialog = new OpenFileDialog
        {
            AllowMultiple = false,
            Filters =
            {
                new FileDialogFilter { Name = "图片", Extensions = { "png", "jpg", "jpeg", "webp" } }
            }
        };
        var files = await dialog.ShowAsync(this);
#pragma warning restore CS0618
        if (files is not { Length: > 0 })
        {
            return;
        }

        var output = CropToChatBackground(files[0]);
        _settingsService.Current.ChatBackgroundImagePath = output;
        SetPreviewImage(ChatBackgroundPreviewImage, output);
        await SaveNonProviderSettingsAsync();
    }

    private static void SetPreviewImage(Avalonia.Controls.Image target, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            target.Source = null;
            return;
        }

        try
        {
            target.Source = new Bitmap(path);
        }
        catch
        {
            target.Source = null;
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
            ? "avares://Aemeath-agent/Assets/user-female.png"
            : "avares://Aemeath-agent/Assets/user-male.png";
        SetPreviewImageFromResource(AvatarPreviewImage, uri);
    }

    private static void SetPreviewImageFromResource(Avalonia.Controls.Image target, string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
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
