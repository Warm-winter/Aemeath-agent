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
        // stdio：在真正启动子进程之前，先探测 command 是否真实存在。
        // 这样缺失的命令（例如本机未安装的 odr.exe）不会走到「进程退出码 1 + GBK 乱码 stderr」
        // 的崩溃路径，而是给出一条清晰、可读的中文提示，且不占用 30s 超时。
        if (server.Transport == McpTransportType.Stdio && !IsCommandAvailable(server.Command))
        {
            var commandDisplay = string.IsNullOrWhiteSpace(server.Command) ? "(空)" : server.Command;
            server.LastStatus = "error";
            server.LastError = $"找不到命令对应的可执行文件：{commandDisplay}。" +
                               $"该 MCP 服务需要先安装 {commandDisplay}，否则请在配置中关闭此服务。";
            _store.SaveServer(server);
            return McpServerLoadResult.Fail(server, server.LastError);
        }

        var timeout = GetTimeout(server.Transport, manualTest);
        var stderr = new StdioErrorBuffer();

        // 分级超时：把总预算拆成「建立连接」和「列举工具」两段。
        // 这样 SSE/HTTP 首次握手偶发慢不会把全部时间都耗在 CreateAsync 上，
        // 导致后续 ListToolsAsync 没机会执行就整体超时。
        var connectTimeout = TimeSpan.FromTicks(timeout.Ticks * 40 / 100);
        var listTimeout = TimeSpan.FromTicks(timeout.Ticks * 60 / 100);

        try
        {
            // 连接级重试：HTTP/SSE 首次 initialize 握手偶发失败时重试 1 次
            var client = await CreateClientWithRetryAsync(server, connectTimeout, stderr, cancellationToken);
            try
            {
                using var listCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                listCts.CancelAfter(listTimeout);
                var tools = await ListToolsAsync(client, server, stderr, listCts.Token);
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

    /// <summary>
    /// 建立 MCP 客户端，HTTP/SSE 传输在首次握手超时/取消时重试 1 次。
    /// stdio 传输不重试（子进程失败通常是配置问题，重试无意义）。
    /// </summary>
    private async Task<McpClient> CreateClientWithRetryAsync(
        McpServerConfig server,
        TimeSpan connectTimeout,
        StdioErrorBuffer stderr,
        CancellationToken cancellationToken)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(connectTimeout);

        try
        {
            return await CreateClientAsync(server, connectTimeout, stderr, connectCts.Token);
        }
        catch (Exception ex) when (server.Transport != McpTransportType.Stdio &&
                                   (ex is TimeoutException or OperationCanceledException) &&
                                   !cancellationToken.IsCancellationRequested)
        {
            // 短暂等待后重试一次，针对 SSE 首次握手偶发超时
            await Task.Delay(1500, cancellationToken);
            var retryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            retryCts.CancelAfter(connectTimeout);
            return await CreateClientAsync(server, connectTimeout, stderr, retryCts.Token);
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

    /// <summary>
    /// 判断 stdio 命令对应的可执行文件是否真实存在。
    /// 支持绝对路径、相对路径，以及在 PATH 中按 PATHEXT 匹配（Windows 上 odr.exe 这类裸命令名）。
    /// 无法确认存在时返回 false，让调用方给出清晰提示而非走子进程崩溃路径。
    /// </summary>
    private static bool IsCommandAvailable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        // 绝对路径或带目录的相对路径：直接看文件在不在
        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command);
        }

        // 裸命令名：在 PATH 各目录里按 PATHEXT 找
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = string.IsNullOrWhiteSpace(pathExt)
            ? new[] { ".exe", ".bat", ".cmd", ".com" }
            : pathExt.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var name = command.Trim();
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            foreach (var ext in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, name + ext)) || File.Exists(Path.Combine(dir, name)))
                    {
                        return true;
                    }
                }
                catch
                {
                    // 非法路径段忽略，继续探测下一段
                }
            }
        }

        return false;
    }

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
