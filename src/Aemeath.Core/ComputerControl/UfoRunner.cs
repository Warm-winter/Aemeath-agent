using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Aemeath.Core.AI;
using Aemeath.Core.Tools;

namespace Aemeath.Core.ComputerControl;

/// <summary>
/// UFO 桥接运行器（轨 B）：通过子进程调用 UFO（Microsoft）完成复杂电脑操作。
///
/// 与轨 A（C# UIA）互补：
/// - 轨 A：纯 C#，零外部依赖，随程序即用，能力覆盖常见任务
/// - 轨 B：UFO 是微软的成熟 agent，视觉 grounding + ReAct 规划更强，但需要 Python + 较重依赖，
///   作为用户可选安装的高阶后端
///
/// UFO 不是 PyPI 包（只能 git clone + pip install -r requirements.txt），所以本类只负责
/// 「检测 UFO 是否可用 → 启动子进程跑 ufo_runner.py → 解析 JSON 结果」。
/// 确认走任务级前置确认卡片（关掉 UFO 内部逐步骤 SAFE_GUARD，避免子进程死锁）。
/// </summary>
public sealed class UfoRunner : IDisposable
{
    private readonly string _pythonExe;
    private readonly string _runnerScript;
    private readonly string _ufoConfigDir;

    public UfoRunner(string pythonExe, string runnerScript, string ufoConfigDir)
    {
        _pythonExe = pythonExe;
        _runnerScript = runnerScript;
        _ufoConfigDir = ufoConfigDir;
    }

    /// <summary>执行 UFO 任务。返回结果摘要。timeout 默认 5 分钟（受 UFO MAX_STEP 约束）。</summary>
    public async Task<string> RunAsync(string request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_pythonExe))
        {
            return "UFO 不可用：未找到 Python 解释器。请在设置中安装 UFO 依赖。";
        }

        if (!File.Exists(_runnerScript))
        {
            return "UFO 不可用：桥接脚本缺失。";
        }

        progress?.Report("UFO 任务开始执行（可能耗时数十秒到数分钟）…");

        var taskName = $"aemeath_{Guid.NewGuid():N}".Substring(0, 24);
        var psi = new ProcessStartInfo
        {
            FileName = _pythonExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_runnerScript) ?? Environment.CurrentDirectory
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(_runnerScript);
        psi.ArgumentList.Add(request);
        psi.ArgumentList.Add("--task-name");
        psi.ArgumentList.Add(taskName);
        if (!string.IsNullOrWhiteSpace(_ufoConfigDir))
        {
            psi.ArgumentList.Add("--config-dir");
            psi.ArgumentList.Add(_ufoConfigDir);
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine("[stderr] " + e.Data); };

        if (!proc.Start())
        {
            return "UFO 启动失败：无法创建子进程。";
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // UFO 单任务最长 5 分钟（受 MAX_STEP×每步 LLM 耗时约束）
        var exited = proc.WaitForExit(300_000);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            return "UFO 任务超时（5 分钟）。";
        }

        var output = stdout.ToString().Trim();
        // 最后一行非空作为 JSON 结果
        var lastLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? output;
        try
        {
            using var doc = JsonDocument.Parse(lastLine);
            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : lastLine;
            return message ?? lastLine;
        }
        catch
        {
            return string.IsNullOrWhiteSpace(output) ? "UFO 无输出。" : ("UFO 输出：" + output);
        }
    }

    public void Dispose()
    {
        // 无长驻资源
    }
}

/// <summary>UFO 安装/检测服务。</summary>
public sealed class UfoInstaller
{
    public static string DefaultBridgeDirectory => RuntimePaths.UfoBridgeDirectory;

    /// <summary>UFO 源码克隆目标目录（用户可选安装后存在）。</summary>
    public static string DefaultUfoSourceDirectory => RuntimePaths.UfoSourceDirectory;

    /// <summary>检测 UFO 是否可用：venv + UFO 源码 + 桥接脚本三者齐备。</summary>
    public async Task<UfoInstallStatus> CheckAsync(string? ufoPythonPath, CancellationToken cancellationToken = default)
    {
        var runner = Path.Combine(DefaultBridgeDirectory, "ufo_runner.py");
        if (!File.Exists(runner))
        {
            return new UfoInstallStatus(false, null, null, null, "桥接脚本未部署。");
        }

        if (string.IsNullOrWhiteSpace(ufoPythonPath) || !File.Exists(ufoPythonPath))
        {
            return new UfoInstallStatus(false, null, runner, DefaultUfoSourceDirectory, "未配置 UFO 专用 Python 解释器。");
        }

        var hasUfo = await CanImportUfoAsync(ufoPythonPath, cancellationToken);
        var configDir = Directory.Exists(DefaultUfoSourceDirectory) ? Path.Combine(DefaultUfoSourceDirectory, "config", "ufo") : null;
        return new UfoInstallStatus(hasUfo, ufoPythonPath, runner, DefaultUfoSourceDirectory, hasUfo ? null : "UFO 未在该 Python 环境中安装。", configDir);
    }

    private static async Task<bool> CanImportUfoAsync(string python, CancellationToken cancellationToken)
    {
        try
        {
            var (code, stdout, _) = await Mem0RunProcessAsync(python, "-c \"import ufo; print('ok')\"", Path.GetDirectoryName(python) ?? ".", cancellationToken);
            return code == 0 && stdout.Contains("ok");
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> Mem0RunProcessAsync(
        string fileName, string arguments, string workingDir, CancellationToken cancellationToken)
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
        using var proc = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        var err = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) err.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit(60_000);
        await Task.Yield();
        return (proc.ExitCode, sb.ToString(), err.ToString());
    }
}

public sealed record UfoInstallStatus(
    bool Installed,
    string? PythonPath,
    string? RunnerScript,
    string? UfoSourceDir,
    string? Error,
    string? UfoConfigDir = null);
