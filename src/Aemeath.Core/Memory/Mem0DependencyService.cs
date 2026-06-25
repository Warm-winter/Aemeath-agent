using System.Diagnostics;
using System.Text;

namespace Aemeath.Core.Memory;

/// <summary>
/// Mem0 依赖安装/检测：用一个独立的 venv（由 uv 管理）安装 mem0ai 及其依赖，
/// 避免污染系统 Python。venv 落在 %AppData%\Aemeath\tools\mem0-venv。
///
/// 需要 uv.exe（项目已有 McpDependencyService 负责下载）。若没有 uv，
/// 会回退尝试系统 python -m venv + pip。
/// </summary>
public sealed class Mem0DependencyService
{
    public static string DefaultVenvDirectory => RuntimePaths.Mem0VenvDirectory;

    public static string DefaultDataDirectory => RuntimePaths.Mem0DataDirectory;

    private const string Mem0Package = "mem0ai";
    // qdrant 自带本地模式所需；faiss 是更轻备选。默认走 qdrant（Mem0 默认且支持 BM25 混合检索）
    private const string ExtraDeps = "qdrant-client>=1.9";

    private readonly string _uvExe;
    private readonly Func<IProgress<string>, CancellationToken, Task<(bool Ok, string? UvPath)>>? _uvEnsurer;

    public Mem0DependencyService(string uvExe,
        Func<IProgress<string>, CancellationToken, Task<(bool Ok, string? UvPath)>>? uvEnsurer = null)
    {
        _uvExe = uvExe;
        _uvEnsurer = uvEnsurer;
    }

    /// <summary>解析 venv 里的 python 解释器绝对路径。</summary>
    public static string ResolveVenvPython(string venvDir)
    {
        if (string.IsNullOrWhiteSpace(venvDir))
        {
            return string.Empty;
        }

        // Windows: <venv>/Scripts/python.exe；跨平台兼容：bin/python
        var win = Path.Combine(venvDir, "Scripts", "python.exe");
        if (File.Exists(win))
        {
            return win;
        }

        var unix = Path.Combine(venvDir, "bin", "python");
        return File.Exists(unix) ? unix : string.Empty;
    }

    public string ResolveVenvPython() => ResolveVenvPython(DefaultVenvDirectory);

    /// <summary>检测 venv 是否就绪、mem0ai 是否可导入。</summary>
    public async Task<Mem0DependencyStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var python = ResolveVenvPython();
        if (string.IsNullOrWhiteSpace(python))
        {
            return new Mem0DependencyStatus(false, python, "尚未创建 Mem0 运行环境（venv）");
        }

        var ok = await CanImportMem0Async(python, cancellationToken);
        return new Mem0DependencyStatus(ok, python, ok ? null : "venv 存在但 mem0ai 未安装或损坏");
    }

    /// <summary>安装/修复 Mem0 依赖。若已有则跳过；缺失则用 uv 创建 venv 并安装。</summary>
    public async Task<Mem0InstallResult> InstallAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await CheckAsync(cancellationToken);
            if (existing.Installed)
            {
                return new Mem0InstallResult(true, existing.PythonPath, "Mem0 运行环境已就绪。", false);
            }

            // uv 缺失时，尝试自动安装（借鉴 OneDragon 的环境自检+一键安装思路）。
            if (string.IsNullOrWhiteSpace(_uvExe) || !File.Exists(_uvExe))
            {
                if (_uvEnsurer is null)
                {
                    return new Mem0InstallResult(false, null,
                        "未找到 uv.exe，且无法自动下载。请在「MCP 配置」中先下载 uv.exe。", false);
                }

                progress?.Report("未检测到 uv，正在自动下载 uv……");
                // ensurer 签名要求非空 IProgress；progress 可能为空，包一层 no-op
                var uvProgress = progress ?? new Progress<string>(_ => { });
                var ensureUv = await _uvEnsurer(uvProgress, cancellationToken);
                if (!ensureUv.Ok || string.IsNullOrWhiteSpace(ensureUv.UvPath) || !File.Exists(ensureUv.UvPath))
                {
                    return new Mem0InstallResult(false, null,
                        "uv 自动下载失败，请手动在「MCP 配置」中下载 uv.exe 后重试。", false);
                }
            }

            Directory.CreateDirectory(DefaultVenvDirectory);
            progress?.Report("正在创建独立 Python 环境（venv）……");

            // 用 uv 创建 venv：uv venv <path>
            var venvResult = await RunProcessAsync(_uvExe, $"venv \"{DefaultVenvDirectory}\" --python 3.11",
                DefaultVenvDirectory, cancellationToken);
            if (venvResult.ExitCode != 0)
            {
                return new Mem0InstallResult(false, null,
                    $"创建 venv 失败：{venvResult.Stderr.Trim()}", false);
            }

            var python = ResolveVenvPython();
            if (string.IsNullOrWhiteSpace(python))
            {
                return new Mem0InstallResult(false, null, "venv 创建后未找到 python.exe", false);
            }

            progress?.Report("正在安装 mem0ai（首次约需 1~3 分钟）……");

            // uv pip install --python <py> mem0ai qdrant-client
            // 用 uv 而不是 pip：快得多，且不要求系统有 pip
            var pkgs = $"{Mem0Package} {ExtraDeps}";
            var installResult = await RunProcessAsync(_uvExe,
                $"pip install --python \"{python}\" {pkgs}",
                DefaultVenvDirectory, cancellationToken, longRunning: true);
            if (installResult.ExitCode != 0)
            {
                return new Mem0InstallResult(false, python,
                    $"安装 mem0ai 失败：{installResult.Stderr.Trim()}", false);
            }

            progress?.Report("正在校验安装……");
            var verify = await CanImportMem0Async(python, cancellationToken);
            return new Mem0InstallResult(verify, python,
                verify ? "Mem0 依赖安装完成。" : "安装完成但校验失败，请重试或检查网络。", true);
        }
        catch (Exception ex)
        {
            return new Mem0InstallResult(false, null, $"安装异常：{ex.Message}", false);
        }
    }

    private static async Task<bool> CanImportMem0Async(string python, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python))
        {
            return false;
        }

        var result = await RunProcessAsync(python, "-c \"import mem0; print('ok')\"",
            Path.GetDirectoryName(python) ?? Environment.CurrentDirectory, cancellationToken);
        return result.ExitCode == 0 && result.Stdout.Contains("ok");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments, string workingDir, CancellationToken cancellationToken, bool longRunning = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDir
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!proc.Start())
        {
            return (-1, string.Empty, "无法启动进程");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // 安装 mem0ai 依赖较慢，给宽裕超时
        var timeoutMs = longRunning ? 300_000 : 60_000;
        var exited = proc.WaitForExit(timeoutMs);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            return (-1, stdout.ToString(), stderr.ToString() + "\n[超时]");
        }

        await Task.Yield(); // 让异步读缓冲收尾
        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

public sealed record Mem0DependencyStatus(bool Installed, string? PythonPath, string? Error);

public sealed record Mem0InstallResult(bool Success, string? PythonPath, string Message, bool FreshInstall);
