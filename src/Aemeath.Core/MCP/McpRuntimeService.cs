using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aemeath.Core.MCP;

public sealed class McpRuntimeService : IAsyncDisposable
{
    private static readonly TimeSpan BackgroundStdioTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BackgroundHttpTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan ManualStdioTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ManualHttpTimeout = TimeSpan.FromSeconds(180);

    private readonly McpServerStore _store;
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, McpClientTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _clientsLock = new(1, 1);

    public McpRuntimeService(McpServerStore? store = null)
    {
        _store = store ?? new McpServerStore();
    }

    public IReadOnlyList<McpServerConfig> ListServers() => _store.ListServers();

    public IReadOnlyList<McpServerConfig> ImportJson(string json) => _store.ImportJson(json);

    public void SaveServer(McpServerConfig server) => _store.SaveServer(server);

    public bool DeleteServer(string id) => _store.DeleteServer(id);

    public void SetEnabled(string id, bool enabled) => _store.SetEnabled(id, enabled);

    public async Task<McpConnectionTestResult> TestConnectionAsync(McpServerConfig server, CancellationToken cancellationToken = default)
    {
        var result = await LoadServerAsync(server, manualTest: true, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error ?? "MCP 连接失败。");
        }

        await result.Client!.DisposeAsync();
        var descriptors = result.Tools
            .Select(t => new McpToolDescriptor(server.Id, server.DisplayName, t.Name, t.Description ?? string.Empty))
            .ToList();
        return new McpConnectionTestResult(true, $"连接成功，发现 {descriptors.Count} 个工具。", descriptors);
    }

    public async Task<KernelPlugin?> BuildEnabledPluginAsync(CancellationToken cancellationToken = default)
    {
        await _clientsLock.WaitAsync(cancellationToken);
        try
        {
            await DisposeClientsAsync();
            var enabledServers = _store.ListServers().Where(s => s.Enabled).ToList();
            if (enabledServers.Count == 0)
            {
                return null;
            }

            var loadTasks = enabledServers.Select(server => LoadServerAsync(server, manualTest: false, cancellationToken)).ToList();
            var results = await Task.WhenAll(loadTasks);
            var functions = new List<KernelFunction>();
            var functionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var result in results)
            {
                if (!result.Success)
                {
                    continue;
                }

                var client = result.Client!;
                _clients[result.Server.Id] = client;
                foreach (var tool in result.Tools)
                {
                    var functionName = NormalizeFunctionName($"{result.Server.Id}_{tool.Name}");

                    if (functionNames.Contains(functionName))
                    {
                        System.Diagnostics.Debug.WriteLine($"MCP function name collision: {functionName} from {result.Server.Id}/{tool.Name} - skipping duplicate");
                        continue;
                    }

                    functionNames.Add(functionName);
                    _tools[functionName] = tool;
                    functions.Add(KernelFunctionFactory.CreateFromMethod(
                        (Func<string?, CancellationToken, Task<string>>)((argumentsJson, ct) => InvokeToolAsync(functionName, argumentsJson, ct)),
                        functionName,
                        BuildDescription(result.Server, tool),
                        [new KernelParameterMetadata("argumentsJson")
                        {
                            Description = "JSON object containing arguments for this MCP tool. Use {} when no arguments are required.",
                            ParameterType = typeof(string),
                            IsRequired = false
                        }],
                        new KernelReturnParameterMetadata { ParameterType = typeof(string) }));
                }
            }

            return functions.Count == 0 ? null : KernelPluginFactory.CreateFromFunctions("mcp", "External MCP server tools", functions);
        }
        finally
        {
            _clientsLock.Release();
        }
    }

    private async Task<McpServerLoadResult> LoadServerAsync(
        McpServerConfig server,
        bool manualTest,
        CancellationToken cancellationToken)
    {
        var timeout = GetTimeout(server.Transport, manualTest);
        var stderr = new StdioErrorBuffer();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var client = await CreateClientAsync(server, timeout, stderr, timeoutCts.Token);
            try
            {
                var tools = await ListToolsAsync(client, server, stderr, timeoutCts.Token);
                server.LastStatus = "ok";
                server.LastError = null;
                _store.SaveServer(server);
                return McpServerLoadResult.Ok(server, client, tools);
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            server.LastStatus = "error";
            server.LastError = BuildFailureMessage(server, ex, stderr, timeout, manualTest ? "测试连接" : "后台加载");
            _store.SaveServer(server);
            return McpServerLoadResult.Fail(server, server.LastError);
        }
    }

    private async Task<string> InvokeToolAsync(string functionName, string? argumentsJson, CancellationToken cancellationToken)
    {
        await _clientsLock.WaitAsync(cancellationToken);
        try
        {
            if (!_tools.TryGetValue(functionName, out var tool))
            {
                return $"MCP 工具不可用：{functionName}";
            }

            var args = ParseArguments(argumentsJson);
            var result = await tool.CallAsync(args, cancellationToken: cancellationToken);
            return FormatToolResult(result);
        }
        finally
        {
            _clientsLock.Release();
        }
    }

    private static IReadOnlyDictionary<string, object?> ParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            var node = JsonNode.Parse(argumentsJson);
            if (node is not JsonObject obj)
            {
                return new Dictionary<string, object?>();
            }

            return obj.ToDictionary(kvp => kvp.Key, kvp => ConvertJsonNode(kvp.Value), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    private static object? ConvertJsonNode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            var element = value.GetValue<JsonElement>();
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number when element.TryGetInt64(out var l) => l,
                JsonValueKind.Number when element.TryGetDouble(out var d) => d,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => element.ToString()
            };
        }

        return node.ToJsonString(McpConfigJson.Options);
    }

    private static string FormatToolResult(CallToolResult result)
    {
        if (result.IsError == true)
        {
            return "MCP 工具执行失败：" + FormatContent(result.Content);
        }

        return FormatContent(result.Content);
    }

    private static string FormatContent(IList<ContentBlock> content)
    {
        if (content.Count == 0)
        {
            return "MCP 工具执行完成，无返回内容。";
        }

        var sb = new StringBuilder();
        foreach (var block in content)
        {
            if (block is TextContentBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text))
            {
                sb.AppendLine(textBlock.Text);
                continue;
            }

            sb.AppendLine(JsonSerializer.Serialize(block, McpConfigJson.Options));
        }

        return sb.ToString().Trim();
    }

    private static string BuildDescription(McpServerConfig server, McpClientTool tool)
    {
        var schema = tool.JsonSchema.ValueKind == JsonValueKind.Undefined ? "{}" : tool.JsonSchema.GetRawText();
        return $"MCP service '{server.DisplayName}' tool '{tool.Name}'. {tool.Description}\nPass argumentsJson as a JSON object matching this schema: {schema}";
    }

    private static async Task<IList<McpClientTool>> ListToolsAsync(
        McpClient client,
        McpServerConfig server,
        StdioErrorBuffer stderr,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.ListToolsAsync(cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException($"读取 MCP 工具列表超时：{server.DisplayName}{FormatStdio(stderr)}", ex);
        }
    }

    private static async Task<McpClient> CreateClientAsync(
        McpServerConfig server,
        TimeSpan timeout,
        StdioErrorBuffer stderr,
        CancellationToken cancellationToken)
    {
        IClientTransport transport = server.Transport switch
        {
            McpTransportType.Sse => new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.DisplayName,
                Endpoint = new Uri(server.Url ?? throw new InvalidOperationException("缺少 SSE URL。")),
                TransportMode = HttpTransportMode.Sse,
                ConnectionTimeout = timeout,
                AdditionalHeaders = server.Headers
            }, loggerFactory: null),
            McpTransportType.Http => new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.DisplayName,
                Endpoint = new Uri(server.Url ?? throw new InvalidOperationException("缺少 HTTP URL。")),
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = timeout,
                AdditionalHeaders = server.Headers
            }, loggerFactory: null),
            _ => new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.DisplayName,
                Command = server.Command ?? throw new InvalidOperationException("缺少 command。"),
                Arguments = server.Args,
                EnvironmentVariables = server.Env.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value, StringComparer.OrdinalIgnoreCase),
                WorkingDirectory = server.WorkingDirectory ?? string.Empty,
                ShutdownTimeout = TimeSpan.FromSeconds(2),
                StandardErrorLines = line =>
                {
                    stderr.Add(line);
                    System.Diagnostics.Debug.WriteLine($"MCP[{server.Id}] {line}");
                }
            }, loggerFactory: null)
        };

        try
        {
            return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException($"建立 MCP 连接超时：{server.DisplayName}{FormatStdio(stderr)}", ex);
        }
    }

    private static TimeSpan GetTimeout(McpTransportType transport, bool manualTest)
        => transport == McpTransportType.Stdio
            ? manualTest ? ManualStdioTimeout : BackgroundStdioTimeout
            : manualTest ? ManualHttpTimeout : BackgroundHttpTimeout;

    private static string BuildFailureMessage(
        McpServerConfig server,
        Exception exception,
        StdioErrorBuffer stderr,
        TimeSpan timeout,
        string phase)
    {
        var target = server.Transport == McpTransportType.Stdio
            ? $"{server.Command} {string.Join(' ', server.Args)}".Trim()
            : server.Url ?? string.Empty;
        var message = exception is TimeoutException or OperationCanceledException
            ? $"{phase}超时（{timeout.TotalSeconds:0} 秒）"
            : $"{phase}失败：{exception.Message}";

        if (!string.IsNullOrWhiteSpace(target))
        {
            message += $"；目标：{target}";
        }

        return message + FormatStdio(stderr);
    }

    private static string FormatStdio(StdioErrorBuffer stderr)
    {
        var text = stderr.ToString();
        return string.IsNullOrWhiteSpace(text) ? string.Empty : $"；stderr：{text}";
    }

    private static string NormalizeFunctionName(string name)
    {
        var normalized = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9_]+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "tool";
        }

        return normalized.StartsWith("mcp_", StringComparison.Ordinal) ? normalized : "mcp_" + normalized;
    }

    private async Task DisposeClientsAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync();
        }

        _clients.Clear();
        _tools.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _clientsLock.WaitAsync();
        try
        {
            await DisposeClientsAsync();
        }
        finally
        {
            _clientsLock.Release();
            _clientsLock.Dispose();
        }
    }

    private sealed record McpServerLoadResult(
        McpServerConfig Server,
        bool Success,
        McpClient? Client,
        IList<McpClientTool> Tools,
        string? Error)
    {
        public static McpServerLoadResult Ok(McpServerConfig server, McpClient client, IList<McpClientTool> tools)
            => new(server, true, client, tools, null);

        public static McpServerLoadResult Fail(McpServerConfig server, string? error)
            => new(server, false, null, Array.Empty<McpClientTool>(), error);
    }

    private sealed class StdioErrorBuffer
    {
        private const int MaxLines = 8;
        private readonly Queue<string> _lines = new();

        public void Add(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            if (_lines.Count >= MaxLines)
            {
                _lines.Dequeue();
            }

            _lines.Enqueue(line.Trim());
        }

        public override string ToString() => string.Join(" | ", _lines);
    }
}
