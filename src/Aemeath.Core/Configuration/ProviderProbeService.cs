using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Aemeath.Core.Configuration;

public sealed class ProviderProbeService
{
    private readonly HttpClient _httpClient;

    public ProviderProbeService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<ProviderProbeResult> FetchModelsAsync(
        string provider,
        string apiKey,
        string? endpoint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ProviderProbeResult.Failed("missing_key", "请先填写 API Key。");
        }

        var baseUrl = ResolveEndpoint(provider, endpoint);
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate($"{baseUrl.TrimEnd('/')}/models", UriKind.Absolute, out var uri))
        {
            return ProviderProbeResult.Failed("invalid_endpoint", "Endpoint 不正确，无法拼接 /models。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ProviderProbeResult.Failed("auth_failed", "API Key 无法通过验证，请检查 Key 或账号权限。", (int)response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ProviderProbeResult.Failed("models_unsupported", "该 Endpoint 没有可用的 /models 接口，可手动填写模型。", (int)response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProviderProbeResult.Failed("request_failed", $"模型列表获取失败：HTTP {(int)response.StatusCode}", (int)response.StatusCode);
            }

            var models = ParseModels(body);
            if (models.Count == 0)
            {
                return ProviderProbeResult.Failed("empty_models", "连接成功，但没有解析到模型列表。", (int)response.StatusCode);
            }

            return ProviderProbeResult.Succeeded(models, $"连接成功，获取到 {models.Count} 个模型。", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderProbeResult.Failed("timeout", "连接超时，请稍后重试。");
        }
        catch (Exception ex)
        {
            return ProviderProbeResult.Failed("request_error", $"连接失败：{ex.Message}");
        }
    }

    public static string ResolveEndpoint(string provider, string? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            return endpoint.Trim();
        }

        return SettingsService.NormalizeProviderName(provider) switch
        {
            "deepseek" => "https://api.deepseek.com",
            "moonshot" or "kimi" => "https://api.moonshot.ai/v1",
            _ => "https://api.openai.com/v1"
        };
    }

    private static List<ProviderModel> ParseModels(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return new List<ProviderModel>();
        }

        var now = DateTimeOffset.UtcNow;
        var models = new List<ProviderModel>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement))
            {
                continue;
            }

            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            models.Add(new ProviderModel
            {
                Id = id.Trim(),
                OwnedBy = item.TryGetProperty("owned_by", out var ownedBy) ? ownedBy.GetString() : null,
                IsEnabled = true,
                LastSeenAt = now,
                ContextLength = TryGetInt(item, "context_length"),
                SupportsImageInput = TryGetBool(item, "supports_image_in"),
                SupportsVideoInput = TryGetBool(item, "supports_video_in"),
                SupportsReasoning = TryGetBool(item, "supports_reasoning")
            });
        }

        return models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int? TryGetInt(JsonElement item, string propertyName)
    {
        if (item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return null;
    }

    private static bool? TryGetBool(JsonElement item, string propertyName)
    {
        if (item.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        return null;
    }
}

public sealed record ProviderProbeResult(
    bool Success,
    string Status,
    string Message,
    IReadOnlyList<ProviderModel> Models,
    int? StatusCode)
{
    public static ProviderProbeResult Succeeded(IReadOnlyList<ProviderModel> models, string message, int? statusCode)
        => new(true, "ok", message, models, statusCode);

    public static ProviderProbeResult Failed(string status, string message, int? statusCode = null)
        => new(false, status, message, Array.Empty<ProviderModel>(), statusCode);
}
