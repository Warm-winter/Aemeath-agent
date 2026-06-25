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

    /// <summary>
    /// 获取当前已加载的 MCP 工具摘要列表，供 Prompt 构建时动态注入工具清单。
    /// 返回 (functionName, description) 元组列表，只包含已成功加载的工具。
    /// </summary>
    public IReadOnlyList<(string FunctionName, string Description)> GetLoadedToolSummary()
    {
        var summary = new List<(string, string)>();
        foreach (var kvp in _tools)
        {
            var functionName = kvp.Key;
            var tool = kvp.Value;
            var description = string.IsNullOrWhiteSpace(tool.Description)
                ? tool.Name
                : tool.Description;
            summary.Add((functionName, description));
        }
        return summary;
    }

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
            // 主动关闭本机不支持的 odr 服务，避免它加载失败拖累其他工具。
            // odr.exe 默认不随程序分发，用户未单独安装时加载必失败。
            await DisableUnsupportedOdrIfNeeded();

            // 受保护的内置服务（filesystem）强制纳入加载，即使 Enabled 字段为 false。
            // 普通服务仍然只加载 Enabled 的。
            // 已废弃的旧内置服务（memory——长期记忆改由 Mem0 提供）永远跳过，即使配置里还留着旧条目。
            var skipLegacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "memory" };
            var enabledServers = _store.ListServers()
                .Where(s => !skipLegacy.Contains(s.Id) && (s.Enabled || McpBuiltinRegistry.IsProtected(s.Id)))
                .ToList();
            if (enabledServers.Count == 0)
            {
                return null;
            }

            // 每个服务使用独立的超时 token，不共享外部 cancellationToken 的取消压力。
            // 这样单个服务超时失败时，只会导致它自己被标记为 error 并跳过，
            // 不会因为某个慢服务拖累整体而丢弃已经成功的工具。
            var loadTasks = enabledServers.Select(async server =>
            {
                var perServerTimeout = GetTimeout(server.Transport, manualTest: false);
                using var perServerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                perServerCts.CancelAfter(perServerTimeout);
                return await LoadServerAsync(server, manualTest: false, perServerCts.Token);
            }).ToList();

            // 等待所有任务完成（失败的服务已被 LoadServerAsync 内部 catch 成 Fail，不会抛出）
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

            // 自动禁用持续失败的服务（仅后台加载，非手动测试）。
            // 使用 LastError 中的失败次数追踪：连续失败达到阈值时禁用，避免拖累整体加载。
            // 手动测试不禁用（用户主动点「测试连接」时应该保留配置）。
            if (!manualTest)
            {
                TryAutoDisableOnPersistentFailure(server);
            }

            _store.SaveServer(server);
            return McpServerLoadResult.Fail(server, server.LastError);
        }
    }

    /// <summary>
    /// 当服务连续失败达到阈值时自动禁用，保障其他工具正常加载。
    /// 通过 UpdatedAt 时间戳与 LastError 中的失败计数追踪连续失败次数。
    /// 避免偶发网络抖动导致误禁用。
    /// </summary>
    private void TryAutoDisableOnPersistentFailure(McpServerConfig server)
    {
        try
        {
            const int MaxConsecutiveFailures = 3;
            const string FailCountPrefix = "[连续失败 ";

            var currentCount = 1;
            if (!string.IsNullOrEmpty(server.LastError) &&
                server.LastError.StartsWith(FailCountPrefix, StringComparison.Ordinal))
            {
                // 解析已有的失败计数
                var rest = server.LastError.Substring(FailCountPrefix.Length);
                var endIndex = rest.IndexOf(']', StringComparison.Ordinal);
                if (endIndex > 0 && int.TryParse(rest.AsSpan(0, endIndex), out var prev))
                {
                    currentCount = prev + 1;
                }
            }

            if (currentCount >= MaxConsecutiveFailures)
            {
                server.Enabled = false;
                // 清除失败计数前缀，记录禁用原因
                var cleanError = server.LastError == null
                    ? string.Empty
                    : (server.LastError.StartsWith(FailCountPrefix, StringComparison.Ordinal)
                        ? server.LastError.Substring(server.LastError.IndexOf(']') + 1).TrimStart()
                        : server.LastError);
                server.LastError = $"已自动禁用（连续失败 {currentCount} 次）。{cleanError}";
            }
            else
            {
                // 在错误信息前加上连续失败计数
                var cleanError = server.LastError == null
                    ? string.Empty
                    : (server.LastError.StartsWith(FailCountPrefix, StringComparison.Ordinal)
                        ? server.LastError.Substring(server.LastError.IndexOf(']') + 1).TrimStart()
                        : server.LastError);
                server.LastError = $"{FailCountPrefix}{currentCount}/{MaxConsecutiveFailures}] {cleanError}";
            }
        }
        catch
        {
            // 自动禁用逻辑失败不影响主流程
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
            using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
            // SSE 分支：根据 URL 特征决定传输模式。
            // - 如果 URL 明确包含 /sse 路径（如 https://xxx.net/sse），说明是 legacy SSE 端点，用 Sse 模式。
            // - 否则用 AutoDetect 让 SDK 自动协商（先试 Streamable HTTP，失败再退回 SSE），兼容性最好。
            McpTransportType.Sse => new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.DisplayName,
                Endpoint = new Uri(server.Url ?? throw new InvalidOperationException("缺少 SSE URL。")),
                TransportMode = DetermineHttpTransportMode(server.Url ?? string.Empty),
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
    /// 根据 URL 特征决定 SSE 传输的 HttpTransportMode。
    /// 如果 URL 明确包含 /sse 路径，说明是 legacy SSE 端点，直接用 Sse 模式。
    /// 否则用 AutoDetect 让 SDK 自动协商（先试 Streamable HTTP，失败再退回 SSE）。
    /// </summary>
    private static HttpTransportMode DetermineHttpTransportMode(string url)
    {
        return url.Contains("/sse", StringComparison.OrdinalIgnoreCase)
            ? HttpTransportMode.Sse
            : HttpTransportMode.AutoDetect;
    }

    /// <summary>
    /// 本机不支持 odr.exe 时，主动停用 windows_odr 服务。
    /// odr.exe 不随程序分发，用户未单独安装时该服务加载必失败。
    /// 虽然独立超时机制已能让它的失败不影响其他工具，但主动停用可以避免无谓的超时等待和错误状态。
    /// </summary>
    private async Task DisableUnsupportedOdrIfNeeded()
    {
        try
        {
            var odrServer = _store.ListServers().FirstOrDefault(s =>
                string.Equals(s.Id, "windows_odr", StringComparison.OrdinalIgnoreCase) && s.Enabled);
            if (odrServer is null)
            {
                return;
            }

            if (!IsCommandAvailable(odrServer.Command))
            {
                odrServer.LastStatus = "error";
                odrServer.LastError = "本机未检测到 odr.exe，已自动停用以避免影响其他工具。如需启用，请先安装 odr。";
                _store.SetEnabled(odrServer.Id, false);
                _store.SaveServer(odrServer);
                await Task.CompletedTask;
            }
        }
        catch
        {
            // odr 检测失败不阻断主加载流程
        }
    }

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
            : RedactUrlSecrets(server.Url) ?? string.Empty;
        var message = exception is TimeoutException or OperationCanceledException
            ? $"{phase}超时（{timeout.TotalSeconds:0} 秒）"
            : $"{phase}失败：{RedactText(exception.Message)}";

        if (!string.IsNullOrWhiteSpace(target))
        {
            message += $"；目标：{target}";
        }

        return message + FormatStdio(stderr);
    }

    /// <summary>抹掉 URL 查询串中的敏感参数（token/key/secret 等），避免凭据被写进错误信息落盘（SEC-008）。</summary>
    private static string? RedactUrlSecrets(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var q = url.IndexOf('?');
        if (q < 0)
        {
            return url;
        }

        var path = url[..q];
        var query = url[(q + 1)..];
        var sensitiveKeys = new[] { "token", "key", "secret", "auth", "password", "apikey", "api_key", "access_token" };
        var pairs = query.Split('&');
        var redacted = false;
        for (int i = 0; i < pairs.Length; i++)
        {
            var eq = pairs[i].IndexOf('=');
            var k = eq >= 0 ? pairs[i][..eq] : pairs[i];
            if (sensitiveKeys.Any(s => k.Contains(s, StringComparison.OrdinalIgnoreCase)))
            {
                pairs[i] = $"{k}=***";
                redacted = true;
            }
        }

        return redacted ? $"{path}?{string.Join('&', pairs)}" : url;
    }

    /// <summary>抹掉异常文本里常见的凭据模式（Bearer xxx、api-key=xxx 等），减少敏感信息泄漏（SEC-015/SEC-008）。</summary>
    private static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        return Regex.Replace(text, "(?i)(bearer|token|api[_-]?key|secret|password)[\\s=:]+[^\\s,;]+", "$1=***");
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
