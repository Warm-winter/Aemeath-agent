using Aemeath.Core.AI;
using Aemeath.Core.Configuration;
using Aemeath.Core.Tools;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text;

namespace Aemeath.Core.ComputerControl;

/// <summary>
/// 电脑控制插件：把电脑控制能力暴露为 KernelFunction。
///
/// 支持两个后端（由 Settings.ComputerControlBackend 决定）：
/// - 轨 A（默认）：C# 原生 UIAutomation + 视觉 LLM（<see cref="ComputerControlAgent"/>），零外部依赖
/// - 轨 B（可选）：UFO（Microsoft），通过 <see cref="UfoRunner"/> 子进程调用，需用户安装
/// - auto：优先用 UFO（若已安装），否则回退轨 A
///
/// 无论哪个后端都走 ToolConfirmationService 的任务级前置确认（会真实操控用户电脑）。
/// </summary>
public class ComputerControlPlugin
{
    private readonly ToolConfirmationService? _confirmation;
    private readonly Func<(string Model, string Endpoint, string ApiKey)?> _visionConfig;
    private readonly Func<string?> _backendSelector;
    private readonly Func<string?> _ufoPythonSelector;

    /// <summary>记录已提交且仍在等待确认的任务，防止 SK AutoInvokeKernelFunctions 第二轮重复创建确认卡片。</summary>
    private static readonly HashSet<string> _pendingTasks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _pendingGuard = new();

    public ComputerControlPlugin(
        Func<(string Model, string Endpoint, string ApiKey)?> visionConfig,
        ToolConfirmationService? confirmation = null,
        Func<string?>? backendSelector = null,
        Func<string?>? ufoPythonSelector = null)
    {
        _visionConfig = visionConfig;
        _confirmation = confirmation;
        _backendSelector = backendSelector ?? (() => "auto");
        _ufoPythonSelector = ufoPythonSelector ?? (() => null);
    }

    [KernelFunction("computer_control")]
    [Description(
        "Control the user's Windows computer to complete a multi-step desktop task — opening apps, clicking UI, typing text, sending messages, etc. " +
        "Examples: '打开微信，给张三发消息：你好', 'open WeChat and send 你好 to File Transfer Helper', 'open Notepad and type ...', '打开计算器算一下...'. " +
        "IMPORTANT: for ANY task that involves operating a desktop application (including just opening an app and doing something inside it), use THIS tool and pass the FULL natural-language task — do NOT call open_app_or_web/open_application/search_web yourself. " +
        "This tool actually moves the mouse, clicks and types on the real desktop, so the user must confirm first. Returns a result summary when done. " +
        "用于任何需要在桌面应用里操作的任务（含「打开某应用再做某事」）：直接把完整任务交给本工具，不要自己用 open_app_or_web 等去打开应用。调用后用户确认才会执行。")]
    public async Task<string> ControlComputerAsync(
        [Description("用自然语言描述要让电脑完成的完整任务，如：打开微信，找到文件传输助手，发送消息：你好")] string task)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return "电脑控制任务为空。";
        }

        var vision = _visionConfig();
        if (vision is null)
        {
            return "电脑控制功能需要辅助视觉模型。请在「设置 → 电脑控制」里配置一个支持图片输入的模型（如 gpt-4o），否则无法操作。";
        }

        // 走任务级前置确认：高风险操作（会真实点击/输入用户电脑）。
        // 用异步闭包重载：ToolConfirmationService.ConfirmAsync 会在后台线程 await 它，
        // 既不阻塞 UI 线程，也避免 sync-over-async。
        if (_confirmation is not null)
        {
            // 防止 SK AutoInvokeKernelFunctions 第二轮重复创建确认卡片：
            // 如果同一任务已在等待确认，直接返回提示信息，不创建新的确认。
            var taskKey = task.Trim().ToLowerInvariant();
            lock (_pendingGuard)
            {
                if (_pendingTasks.Contains(taskKey))
                {
                    return "电脑控制任务已提交，正在等待用户确认。请告知用户点击确认卡片即可执行，无需重复发起。";
                }
                _pendingTasks.Add(taskKey);
            }

            var marker = _confirmation.RequestConfirmation(
                "电脑控制任务",
                $"小爱即将在你的电脑上自动操作以完成：\n{task}\n\n这会真实点击界面、输入文字，可能耗时数十秒。请确认后再执行。",
                async () =>
                {
                    try
                    {
                        return await RunAgentAsync(task);
                    }
                    finally
                    {
                        lock (_pendingGuard)
                        {
                            _pendingTasks.Remove(taskKey);
                        }
                    }
                },
                isLongRunning: true);

            // marker 后附加 LLM 友好说明，让 LLM 理解任务已提交、不要再次调用 computer_control。
            // ShouldSuppressConfirmationReply 检测 marker 前缀，附加文字不影响检测。
            return marker + " 电脑控制任务已提交，正在等待用户点击确认卡片后执行。请用简短中文告知用户：任务已准备就绪，需要确认后开始。不要再次调用 computer_control。";
        }

        return await RunAgentAsync(task);
    }

    private async Task<string> RunAgentAsync(string task)
    {
        try
        {
            var backend = (_backendSelector() ?? "auto").ToLowerInvariant();
            // auto：若 UFO 可用则用 UFO，否则轨 A
            if (backend == "ufo" || (backend == "auto" && await IsUfoAvailableAsync()))
            {
                var ufoResult = await RunUfoAsync(task);
                if (ufoResult is not null)
                {
                    return ufoResult;
                }
                // UFO 配置了但不可用 → 回退轨 A
            }

            var agent = new ComputerControlAgent(_visionConfig);
            var result = await agent.RunAsync(task, new Progress<string>(msg =>
            {
                System.Diagnostics.Debug.WriteLine($"[computer_control] {msg}");
            }));
            return result;
        }
        catch (Exception ex)
        {
            return $"电脑控制任务执行失败：{ex.Message}";
        }
    }

    private async Task<bool> IsUfoAvailableAsync()
    {
        try
        {
            var python = _ufoPythonSelector();
            if (string.IsNullOrWhiteSpace(python)) return false;
            var installer = new UfoInstaller();
            var status = await installer.CheckAsync(python);
            return status.Installed;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> RunUfoAsync(string task)
    {
        try
        {
            var python = _ufoPythonSelector();
            if (string.IsNullOrWhiteSpace(python)) return null;
            var installer = new UfoInstaller();
            var status = await installer.CheckAsync(python);
            if (!status.Installed || status.RunnerScript is null) return null;

            var runner = new UfoRunner(python, status.RunnerScript, status.UfoConfigDir ?? string.Empty);
            using var __ = runner;
            return await runner.RunAsync(task);
        }
        catch
        {
            return null;
        }
    }
}
