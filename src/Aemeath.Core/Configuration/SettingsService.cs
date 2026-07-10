using System.Text.Json;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Aemeath.Core.Configuration;

public class SettingsService
{
    private readonly string _settingsPath;
    private Settings _settings;

    public Settings Current => _settings;

    /// <summary>提供商/模型配置变更时触发，订阅者可据此刷新 UI。</summary>
    public event Action? ProvidersChanged;

    /// <summary>Raised after any settings snapshot is written successfully.</summary>
    public event Action? SettingsChanged;

    private void RaiseProvidersChanged()
    {
        ProvidersChanged?.Invoke();
    }

    public SettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Aemeath",
            "settings.json"))
    {
    }

    internal SettingsService(string settingsPath)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        _settings = LoadOrDefault();
    }

    private Settings LoadOrDefault()
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                if (!json.Contains("\"EnableParticleEffects\"", StringComparison.Ordinal))
                {
                    settings.EnableParticleEffects = true;
                }
                if (settings.PetOpacity <= 0)
                {
                    settings.PetOpacity = 1.0;
                }
                settings.PetOpacity = Math.Clamp(settings.PetOpacity, 0.65, 1.0);
                if (string.IsNullOrWhiteSpace(settings.PetSizePreset))
                {
                    settings.PetSizePreset = "normal";
                }
                foreach (var key in settings.ApiKeys.Values)
                {
                    key.Key = TryDecrypt(key.Key);
                    MigrateProviderModels(key);
                }

                // Azure 语音密钥同样走 DPAPI 解密（SEC-005）。旧版本明文存储时，
                // TryDecrypt 解密失败会原样返回，保持向后兼容。
                settings.AzureSpeechKey = TryDecrypt(settings.AzureSpeechKey ?? string.Empty);

                // 视觉模型 key 走同样的 DPAPI 解密
                settings.VisionApiKey = string.IsNullOrWhiteSpace(settings.VisionApiKey)
                    ? null
                    : TryDecrypt(settings.VisionApiKey);

                settings.ApiKeys = settings.ApiKeys
                    .GroupBy(kvp => NormalizeProvider(kvp.Key))
                    .ToDictionary(g => g.Key, g => g.Last().Value);

                return settings;
            }
            catch
            {
                return new Settings();
            }
        }
        
        return new Settings();
    }

    public void Save()
    {
        var snapshot = new Settings
        {
            CurrentProvider = _settings.CurrentProvider,
            DefaultModel = _settings.DefaultModel,
            EnableAlwaysOnTop = _settings.EnableAlwaysOnTop,
            MinimizeToTray = _settings.MinimizeToTray,
            EnableAutoStart = _settings.EnableAutoStart,
            IsPetFollowing = _settings.IsPetFollowing,
            PetWidth = _settings.PetWidth,
            PetHeight = _settings.PetHeight,
            PetSizePreset = _settings.PetSizePreset,
            PetOpacity = _settings.PetOpacity,
            EnablePetBubbles = _settings.EnablePetBubbles,
            EnablePetIdleGreeting = _settings.EnablePetIdleGreeting,
            EnablePetEdgeSnap = _settings.EnablePetEdgeSnap,
            SystemPrompt = _settings.SystemPrompt,
            EnableParticleEffects = _settings.EnableParticleEffects,
            ReduceMotion = _settings.ReduceMotion,
            IsChatSidebarOpen = _settings.IsChatSidebarOpen,
            EnableVoiceInput = _settings.EnableVoiceInput,
            AzureSpeechKey = TryEncrypt(_settings.AzureSpeechKey ?? string.Empty),
            AzureSpeechRegion = _settings.AzureSpeechRegion,
            UserAvatarType = _settings.UserAvatarType,
            CustomUserAvatarPath = _settings.CustomUserAvatarPath,
            ChatBackgroundImagePath = _settings.ChatBackgroundImagePath,
            UvExecutablePath = _settings.UvExecutablePath,
            BunExecutablePath = _settings.BunExecutablePath,
            McpServersConfigPath = _settings.McpServersConfigPath,
            Mem0PythonPath = _settings.Mem0PythonPath,
            Mem0Enabled = _settings.Mem0Enabled,
            VisionModel = _settings.VisionModel,
            VisionEndpoint = _settings.VisionEndpoint,
            VisionProvider = _settings.VisionProvider,
            VisionApiKey = TryEncrypt(_settings.VisionApiKey ?? string.Empty),
            Mem0EmbedModel = _settings.Mem0EmbedModel,
            Mem0EmbedDims = _settings.Mem0EmbedDims,
            ComputerControlBackend = _settings.ComputerControlBackend,
            UfoPythonPath = _settings.UfoPythonPath,
            ApiKeys = _settings.ApiKeys.ToDictionary(
                x => x.Key,
                x => new ApiKey
                {
                    Key = TryEncrypt(x.Value.Key),
                    Endpoint = x.Value.Endpoint,
                    ModelId = x.Value.ModelId,
                    Models = x.Value.Models.Select(CloneModel).ToList(),
                    LastModelRefreshAt = x.Value.LastModelRefreshAt,
                    LastConnectionTestAt = x.Value.LastConnectionTestAt,
                    LastConnectionStatus = x.Value.LastConnectionStatus,
                    LastConnectionMessage = x.Value.LastConnectionMessage
                })
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        
        var json = JsonSerializer.Serialize(snapshot, options);
        File.WriteAllText(_settingsPath, json);
        SettingsChanged?.Invoke();
    }

    public void UpdateApiKey(string provider, string key, string? endpoint = null, string? modelId = null)
    {
        var normalizedProvider = NormalizeProvider(provider);
        if (!_settings.ApiKeys.ContainsKey(normalizedProvider))
        {
            _settings.ApiKeys[normalizedProvider] = new ApiKey();
        }

        _settings.ApiKeys[normalizedProvider].Key = key;
        _settings.ApiKeys[normalizedProvider].Endpoint = endpoint;
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            _settings.ApiKeys[normalizedProvider].ModelId = modelId.Trim();
            EnsureModelExists(_settings.ApiKeys[normalizedProvider], modelId.Trim(), enabled: true);
        }

        Save();
        RaiseProvidersChanged();
    }

    public void SaveProviderModels(string provider, IEnumerable<ProviderModel> models, string? defaultModelId = null)
    {
        var normalizedProvider = NormalizeProvider(provider);
        if (!_settings.ApiKeys.TryGetValue(normalizedProvider, out var apiKey))
        {
            return;
        }

        var merged = models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .GroupBy(m => m.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var model = g.Last();
                return new ProviderModel
                {
                    Id = model.Id.Trim(),
                    OwnedBy = model.OwnedBy,
                    IsEnabled = model.IsEnabled,
                    LastSeenAt = model.LastSeenAt ?? DateTimeOffset.UtcNow,
                    ContextLength = model.ContextLength,
                    SupportsImageInput = model.SupportsImageInput,
                    SupportsVideoInput = model.SupportsVideoInput,
                    SupportsReasoning = model.SupportsReasoning
                };
            })
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        apiKey.Models = merged;
        apiKey.LastModelRefreshAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(defaultModelId))
        {
            apiKey.ModelId = defaultModelId.Trim();
            if (string.Equals(NormalizeProvider(_settings.CurrentProvider), normalizedProvider, StringComparison.OrdinalIgnoreCase))
            {
                _settings.DefaultModel = apiKey.ModelId;
            }
            // 不再调用 EnsureModelExists 强制启用默认模型——默认模型的启用状态由 UI 采集决定。
        }

        Save();
        RaiseProvidersChanged();
    }

    public IReadOnlyList<ProviderModel> GetProviderModels(string provider, bool enabledOnly = false)
    {
        if (!_settings.ApiKeys.TryGetValue(NormalizeProvider(provider), out var apiKey))
        {
            return Array.Empty<ProviderModel>();
        }

        MigrateProviderModels(apiKey);
        var models = enabledOnly
            ? apiKey.Models.Where(m => m.IsEnabled)
            : apiKey.Models;

        return models
            .Select(CloneModel)
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool SwitchCurrentModel(string provider, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var normalizedProvider = NormalizeProvider(provider);
        if (!_settings.ApiKeys.TryGetValue(normalizedProvider, out var apiKey))
        {
            return false;
        }

        var model = modelId.Trim();
        apiKey.ModelId = model;
        _settings.CurrentProvider = normalizedProvider;
        _settings.DefaultModel = model;
        EnsureModelExists(apiKey, model, enabled: true);
        Save();
        RaiseProvidersChanged();
        return true;
    }

    public void UpdateProviderConnectionStatus(string provider, string status, string message)
    {
        var normalizedProvider = NormalizeProvider(provider);
        if (!_settings.ApiKeys.TryGetValue(normalizedProvider, out var apiKey))
        {
            return;
        }

        apiKey.LastConnectionStatus = status;
        apiKey.LastConnectionMessage = message;
        apiKey.LastConnectionTestAt = DateTimeOffset.UtcNow;
        Save();
    }

    public IReadOnlyList<string> ListProviders()
    {
        return _settings.ApiKeys.Keys
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool SwitchCurrentProvider(string provider)
    {
        var normalizedProvider = NormalizeProvider(provider);
        if (!_settings.ApiKeys.ContainsKey(normalizedProvider))
        {
            return false;
        }

        _settings.CurrentProvider = normalizedProvider;
        var apiKey = _settings.ApiKeys[normalizedProvider];
        MigrateProviderModels(apiKey);
        var modelId = apiKey.ModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = apiKey.Models.FirstOrDefault(m => m.IsEnabled)?.Id;
        }
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            _settings.DefaultModel = modelId.Trim();
        }

        Save();
        RaiseProvidersChanged();
        return true;
    }

    public bool DeleteProvider(string provider)
    {
        var normalizedProvider = NormalizeProvider(provider);
        if (!_settings.ApiKeys.Remove(normalizedProvider))
        {
            return false;
        }

        if (string.Equals(NormalizeProvider(_settings.CurrentProvider), normalizedProvider, StringComparison.OrdinalIgnoreCase))
        {
            var fallback = _settings.ApiKeys.Keys
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            _settings.CurrentProvider = fallback ?? "openai";
            if (fallback is not null &&
                _settings.ApiKeys.TryGetValue(fallback, out var apiKey) &&
                !string.IsNullOrWhiteSpace(apiKey.ModelId))
            {
                _settings.DefaultModel = apiKey.ModelId.Trim();
            }
        }

        Save();
        RaiseProvidersChanged();
        return true;
    }

    public string? GetApiKey(string provider)
    {
        return _settings.ApiKeys.TryGetValue(NormalizeProvider(provider), out var apiKey)
            ? apiKey.Key 
            : null;
    }

    public ApiKey? GetApiKeyInfo(string provider)
    {
        return _settings.ApiKeys.TryGetValue(NormalizeProvider(provider), out var apiKey) ? apiKey : null;
    }

    public static string NormalizeProviderName(string provider) => NormalizeProvider(provider);

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

    private static void MigrateProviderModels(ApiKey apiKey)
    {
        apiKey.Models ??= new List<ProviderModel>();
        // 仅迁移该提供商自己的 ModelId，不使用全局 DefaultModel（可能来自其他提供商）。
        var model = apiKey.ModelId?.Trim();
        if (!string.IsNullOrWhiteSpace(model) &&
            apiKey.Models.All(m => !string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase)))
        {
            apiKey.Models.Add(new ProviderModel
            {
                Id = model,
                IsEnabled = true,
                LastSeenAt = DateTimeOffset.UtcNow
            });
        }
    }

    private static void EnsureModelExists(ApiKey apiKey, string modelId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        apiKey.Models ??= new List<ProviderModel>();
        var existing = apiKey.Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            apiKey.Models.Add(new ProviderModel
            {
                Id = modelId,
                IsEnabled = enabled,
                LastSeenAt = DateTimeOffset.UtcNow
            });
            return;
        }

        // 不修改已存在模型的 IsEnabled 状态——尊重用户在 UI 中的显式启用/禁用操作。
        existing.LastSeenAt ??= DateTimeOffset.UtcNow;
    }

    private static string NormalizeProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "openai";
        }

        return provider.Trim().ToLowerInvariant();
    }

    private static string TryEncrypt(string plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return plain;
        }
        try
        {
            var data = Encoding.UTF8.GetBytes(plain);
            var encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            // DPAPI 加密失败时不再静默降级为明文（SEC-006）：留可观测痕迹，
            // 便于排查。仍返回原值以避免设置整体无法保存。
            System.Diagnostics.Debug.WriteLine($"[settings] DPAPI 加密失败，凭据将以明文落盘：{ex.Message}");
            return plain;
        }
    }

    private static string TryDecrypt(string cipher)
    {
        if (string.IsNullOrEmpty(cipher))
        {
            return cipher;
        }
        try
        {
            var data = Convert.FromBase64String(cipher);
            var decrypted = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            // 解密失败：通常是旧版本明文存储或换用户/换机器导致，原样返回保持可用。
            return cipher;
        }
    }
}
