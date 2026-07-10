using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Aemeath.Core.Memory;

/// <summary>
/// Mem0 长期记忆客户端：通过一个常驻的 Python 子进程（mem0_bridge.py）以
/// stdin/stdout JSON-RPC 协议调用 Mem0。子进程在首次使用时启动，
/// <see cref="Dispose"/> 时发送 shutdown 并回收。
///
/// 这个类只负责「与桥接进程通信」，记忆的业务编排（每轮 add、发送前 search）
/// 由 <see cref="AemiChatService"/> / 桌面层负责。详见 CLAUDE.md「记忆系统」一节。
/// </summary>
public sealed class Mem0Client : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SemaphoreSlim _procLock = new(1, 1);
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _pendingLock = new();
    private Task? _readerTask;
    private CancellationTokenSource? _readerCts;
    private volatile bool _disposed;
    private string? _bridgeScriptPath;
    private readonly string _pythonExe;
    private readonly string _dataDir;
    private readonly Mem0ConnectionConfig _config;

    public bool IsRunning => _process is not null && !_process.HasExited;

    /// <summary>
    /// 可选的诊断日志回调（由桌面层注入 AppLogger）。Core 层不直接依赖日志实现。
    /// 未设置时落到 Debug.WriteLine。
    /// </summary>
    public Action<string, string, Exception?>? Diagnostics { get; set; }

    private void Log(string level, string message, Exception? ex = null)
    {
        if (Diagnostics is not null)
        {
            Diagnostics(level, message, ex);
        }
        else if (ex is null)
        {
            Debug.WriteLine($"[mem0:{level}] {message}");
        }
        else
        {
            Debug.WriteLine($"[mem0:{level}] {message} | {ex}");
        }
    }

    /// <summary>构造。pythonExe 为 python 解释器绝对路径；dataDir 为 Mem0 数据目录。</summary>
    public Mem0Client(string pythonExe, string dataDir, Mem0ConnectionConfig config)
    {
        if (string.IsNullOrWhiteSpace(pythonExe))
        {
            throw new ArgumentException("pythonExe 不能为空", nameof(pythonExe));
        }

        _pythonExe = pythonExe;
        _dataDir = dataDir;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        Directory.CreateDirectory(_dataDir);
    }

    /// <summary>健康检查：mem0ai 是否已安装（不构造 Memory，轻量）。</summary>
    public async Task<Mem0HealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var resp = await SendAsync("health", null, cancellationToken);
            if (resp.TryGetProperty("mem0_importable", out var imp))
            {
                var ok = imp.GetBoolean();
                var err = resp.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : null;
                return new Mem0HealthResult(ok, ok ? null : (err ?? "mem0ai 未安装"));
            }

            return new Mem0HealthResult(false, "桥接进程返回异常");
        }
        catch (Exception ex)
        {
            return new Mem0HealthResult(false, ex.Message);
        }
    }

    /// <summary>写入记忆。messages 可为单条消息字符串或 OpenAI chat 格式的消息列表 JSON。</summary>
    public Task<JsonElement> AddAsync(string messagesJson, Mem0Scope scope, bool infer = true, CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["messages"] = ParseRawMessages(messagesJson),
            ["infer"] = infer
        };
        ApplyScope(args, scope);
        return SendAsync("add", args, cancellationToken);
    }

    /// <summary>检索记忆。返回 {"results":[{memory, score, ...}]}。</summary>
    public Task<JsonElement> SearchAsync(string query, Mem0Scope scope, int topK = 6, CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["top_k"] = topK
        };
        ApplyScope(args, scope);
        return SendAsync("search", args, cancellationToken);
    }

    /// <summary>列出该作用域下所有记忆。</summary>
    public Task<JsonElement> GetAllAsync(Mem0Scope scope, int topK = 50, CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["top_k"] = topK
        };
        ApplyScope(args, scope);
        return SendAsync("get_all", args, cancellationToken);
    }

    public Task<JsonElement> DeleteAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["memory_id"] = memoryId };
        return SendAsync("delete", args, cancellationToken);
    }

    public Task<JsonElement> DeleteAllAsync(Mem0Scope scope, CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>();
        ApplyScope(args, scope);
        return SendAsync("delete_all", args, cancellationToken);
    }

    private static void ApplyScope(Dictionary<string, object?> args, Mem0Scope scope)
    {
        // 全局用户记忆用固定 user_id；会话记忆用 run_id 隔离；agent 记忆用 agent_id。
        // Mem0 要求至少一个 id，这里保证 scope 总会写入对应字段。
        if (!string.IsNullOrWhiteSpace(scope.UserId))
        {
            args["user_id"] = scope.UserId;
        }

        if (!string.IsNullOrWhiteSpace(scope.AgentId))
        {
            args["agent_id"] = scope.AgentId;
        }

        if (!string.IsNullOrWhiteSpace(scope.RunId))
        {
            args["run_id"] = scope.RunId;
        }
    }

    private static object ParseRawMessages(string messagesJson)
    {
        if (string.IsNullOrWhiteSpace(messagesJson))
        {
            return string.Empty;
        }

        messagesJson = messagesJson.Trim();
        // 纯字符串直接传
        if (!messagesJson.StartsWith('{') && !messagesJson.StartsWith('['))
        {
            return messagesJson;
        }

        try
        {
            using var doc = JsonDocument.Parse(messagesJson);
            return JsonSerializer.Deserialize<object>(messagesJson, JsonOptions) ?? messagesJson;
        }
        catch
        {
            return messagesJson;
        }
    }

    /// <summary>发送一个请求并等待对应 id 的响应。</summary>
    private async Task<JsonElement> SendAsync(string op, object? args, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(Mem0Client));
        }

        await EnsureProcessAsync(cancellationToken);

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pending[id] = tcs;
        }

        var request = new Dictionary<string, object?> { ["id"] = id, ["op"] = op };
        if (args is not null)
        {
            request["args"] = args;
        }

        var line = JsonSerializer.Serialize(request, JsonOptions);

        try
        {
            await _procLock.WaitAsync(cancellationToken);
            try
            {
                if (_stdin is null)
                {
                    throw new InvalidOperationException("Mem0 桥接进程未启动");
                }

                await _stdin.WriteLineAsync(line);
                await _stdin.FlushAsync(cancellationToken);
            }
            finally
            {
                _procLock.Release();
            }

            // 读循环会填充 tcs
            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            // 超时兜底（避免桥接卡死）：add 走 LLM 可能较慢，给宽裕上限
            var timeout = op == "add" ? TimeSpan.FromSeconds(_config.AddTimeoutSeconds) : TimeSpan.FromSeconds(_config.RpcTimeoutSeconds);
            var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            delayCts.CancelAfter(timeout);
            try
            {
                using var delayReg = delayCts.Token.Register(() => tcs.TrySetException(new TimeoutException($"Mem0 {op} 超时（{timeout.TotalSeconds:0}s）")));
                var resp = await tcs.Task.ConfigureAwait(false);
                return resp;
            }
            catch (OperationCanceledException) when (delayCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Mem0 {op} 超时（{timeout.TotalSeconds:0}s）");
            }
            finally
            {
                delayCts.Dispose();
            }
        }
        finally
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }
        }
    }

    /// <summary>首次使用时拉起桥接进程并等待握手。</summary>
    private async Task EnsureProcessAsync(CancellationToken cancellationToken)
    {
        if (IsRunning && _stdin is not null && _stdout is not null)
        {
            return;
        }

        await _procLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning && _stdin is not null && _stdout is not null)
            {
                return;
            }

            // 清理旧进程残留
            await KillProcessCoreAsync();

            _bridgeScriptPath ??= await DeployBridgeAsync(cancellationToken);

            var psi = new ProcessStartInfo
            {
                FileName = _pythonExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(_bridgeScriptPath) ?? _dataDir
            };
            // -u：无缓冲，保证 stdin/stdout 行协议及时
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(_bridgeScriptPath);

            ApplyEnv(psi);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!proc.Start())
            {
                throw new InvalidOperationException("无法启动 Mem0 桥接进程");
            }

            _process = proc;
            _stdin = proc.StandardInput;
            _stdout = proc.StandardOutput;
            _stdin.AutoFlush = false;

            _readerCts = new CancellationTokenSource();
            _readerTask = Task.Run(() => ReaderLoop(_readerCts.Token), _readerCts.Token);

            proc.Exited += (_, _) =>
            {
                Log("warn", $"桥接进程退出，ExitCode={proc.ExitCode}");
                FailAllPending("Mem0 桥接进程意外退出");
            };

            // 等待握手 __hello__（由读循环解析后丢弃）
            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            helloCts.CancelAfter(TimeSpan.FromSeconds(_config.StartTimeoutSeconds));
            await WaitForHelloAsync(helloCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _procLock.Release();
        }
    }

    private async Task WaitForHelloAsync(CancellationToken cancellationToken)
    {
        // hello 通过 _helloTcs 被读循环填充
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _helloTcs = tcs;
        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        await tcs.Task.ConfigureAwait(false);
    }

    private TaskCompletionSource<bool>? _helloTcs;

    private async Task ReaderLoop(CancellationToken cancellationToken)
    {
        var reader = _stdout;
        if (reader is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                line = line.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch
                {
                    continue;
                }

                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = idEl.GetString()!;
                if (id == "__hello__")
                {
                    _helloTcs?.TrySetResult(true);
                    continue;
                }

                TaskCompletionSource<JsonElement>? tcs = null;
                lock (_pendingLock)
                {
                    if (_pending.TryGetValue(id, out tcs) && root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
                    {
                        // 成功响应：把 result 交给调用方
                    }
                }

                if (tcs is null)
                {
                    continue;
                }

                if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    var result = root.TryGetProperty("result", out var r) ? r : default;
                    tcs.TrySetResult(result);
                }
                else
                {
                    var err = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                        ? e.GetString()
                        : "未知错误";
                    tcs.TrySetException(new Mem0Exception(err ?? "未知错误"));
                }
            }
        }
        catch (Exception ex)
        {
            Log("error", "读循环异常", ex);
            FailAllPending("读循环异常：" + ex.Message);
        }
    }

    private void FailAllPending(string reason)
    {
        lock (_pendingLock)
        {
            foreach (var kvp in _pending)
            {
                kvp.Value.TrySetException(new Mem0Exception(reason));
            }

            _pending.Clear();
            _helloTcs?.TrySetException(new Mem0Exception(reason));
        }
    }

    private void ApplyEnv(ProcessStartInfo psi)
    {
        psi.EnvironmentVariables["AEMEATH_MEM0_DIR"] = _dataDir;
        psi.EnvironmentVariables["AEMEATH_MEM0_LLM_MODEL"] = _config.LlmModel ?? "";
        psi.EnvironmentVariables["AEMEATH_MEM0_LLM_BASE_URL"] = _config.LlmBaseUrl ?? "";
        psi.EnvironmentVariables["AEMEATH_MEM0_LLM_API_KEY"] = _config.LlmApiKey ?? "";
        psi.EnvironmentVariables["AEMEATH_MEM0_EMBED_MODEL"] = _config.EmbedModel ?? "";
        psi.EnvironmentVariables["AEMEATH_MEM0_EMBED_BASE_URL"] = _config.EmbedBaseUrl ?? "";
        psi.EnvironmentVariables["AEMEATH_MEM0_EMBED_API_KEY"] = _config.EmbedApiKey ?? "";
        psi.EnvironmentVariables["AEMEATH_MEM0_EMBED_DIMS"] = _config.EmbedDims.ToString(CultureInfo.InvariantCulture);
        psi.EnvironmentVariables["AEMEATH_MEM0_VECTOR_PROVIDER"] = _config.VectorProvider ?? "qdrant";
        psi.EnvironmentVariables["AEMEATH_MEM0_VECTOR_PATH"] = Path.Combine(_dataDir, "qdrant");
        // 默认追踪会更安全，但 qdrant-client 依赖也跑得起来
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        }
    }

    /// <summary>把内嵌的 mem0_bridge.py 释放到数据目录（避免每次从程序集读流）。</summary>
    private async Task<string> DeployBridgeAsync(CancellationToken cancellationToken)
    {
        var dir = Path.Combine(_dataDir, "bridge");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "mem0_bridge.py");

        var assembly = typeof(Mem0Client).Assembly;
        var resourceName = "Aemeath.Core.Memory.mem0_bridge.py";
        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"找不到内嵌资源 {resourceName}。请确认 csproj 已配置 EmbeddedResource。");
        }

        // 原子写：先写临时文件再覆盖
        var tmp = path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.CopyToAsync(fs, cancellationToken);
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
        File.Move(tmp, path);
        return path;
    }

    private async Task KillProcessCoreAsync()
    {
        var proc = _process;
        _stdin?.Dispose();
        _stdin = null;
        _stdout = null;
        _readerCts?.Cancel();
        _readerCts = null;
        _process = null;

        if (proc is not null && !proc.HasExited)
        {
            try
            {
                // 优雅退出
                if (_stdin is not null && _stdin.BaseStream.CanWrite)
                {
                    await _stdin.WriteLineAsync("{\"id\":\"shutdown\",\"op\":\"shutdown\"}");
                    await _stdin.FlushAsync();
                    proc.WaitForExit(2000);
                }
            }
            catch
            {
                // 忽略
            }

            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 忽略
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _procLock.WaitAsync();
        try
        {
            FailAllPending("Mem0Client disposed");
            await KillProcessCoreAsync();
        }
        finally
        {
            _procLock.Release();
            _procLock.Dispose();
        }
    }
}

/// <summary>Mem0 调用异常（桥接返回 ok=false 或进程错误）。</summary>
public sealed class Mem0Exception : Exception
{
    public Mem0Exception(string message) : base(message) { }
}

/// <summary>Mem0 健康检查结果。</summary>
public sealed record Mem0HealthResult(bool Installed, string? Error);

/// <summary>Mem0 记忆作用域：user_id（全局用户记忆）/ agent_id / run_id（会话）。</summary>
public sealed record Mem0Scope(string? UserId = null, string? AgentId = null, string? RunId = null)
{
    /// <summary>全局用户记忆：所有会话共享的用户档案。</summary>
    public static Mem0Scope GlobalUser => new(UserId: "drifter", AgentId: "aemi");

    /// <summary>会话记忆：run_id = 会话 id。</summary>
    public static Mem0Scope ForSession(string sessionId) => new(UserId: "drifter", AgentId: "aemi", RunId: sessionId);
}

/// <summary>Mem0 连接配置（指向 OpenAI 兼容的 LLM + embedding endpoint）。</summary>
public sealed record Mem0ConnectionConfig(
    string LlmModel,
    string LlmBaseUrl,
    string LlmApiKey,
    string EmbedModel,
    string? EmbedBaseUrl = null,
    string? EmbedApiKey = null,
    int EmbedDims = 1536,
    string VectorProvider = "qdrant",
    int AddTimeoutSeconds = 120,
    int RpcTimeoutSeconds = 40,
    int StartTimeoutSeconds = 30);
