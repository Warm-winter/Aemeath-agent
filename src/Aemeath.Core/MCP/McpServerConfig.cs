using System.Text.Json;

namespace Aemeath.Core.MCP;

public enum McpTransportType
{
    Stdio,
    Sse,
    Http
}

public sealed class McpServerConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public McpTransportType Transport { get; set; } = McpTransportType.Stdio;
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? WorkingDirectory { get; set; }
    public string? Url { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

public sealed record McpToolDescriptor(string ServiceId, string ServiceName, string ToolName, string Description);

public sealed record McpConnectionTestResult(bool Success, string Message, IReadOnlyList<McpToolDescriptor> Tools);

public static class McpConfigJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}
