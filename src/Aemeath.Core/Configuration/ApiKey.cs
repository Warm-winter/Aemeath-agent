namespace Aemeath.Core.Configuration;

public class ApiKey
{
    public string Key { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public string? ModelId { get; set; }
    public List<ProviderModel> Models { get; set; } = new();
    public DateTimeOffset? LastModelRefreshAt { get; set; }
    public DateTimeOffset? LastConnectionTestAt { get; set; }
    public string? LastConnectionStatus { get; set; }
    public string? LastConnectionMessage { get; set; }
}

public class ProviderModel
{
    public string Id { get; set; } = string.Empty;
    public string? OwnedBy { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset? LastSeenAt { get; set; }
    public int? ContextLength { get; set; }
    public bool? SupportsImageInput { get; set; }
    public bool? SupportsVideoInput { get; set; }
    public bool? SupportsReasoning { get; set; }
}
