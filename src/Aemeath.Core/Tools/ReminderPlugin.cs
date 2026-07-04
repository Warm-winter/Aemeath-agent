using Microsoft.SemanticKernel;
using System.ComponentModel;
using Timer = System.Timers.Timer;

namespace Aemeath.Core.Tools;

public class ReminderPlugin
{
    private readonly List<Timer> _activeTimers = new();
    private readonly object _timerLock = new();

    /// <summary>
    /// 提醒触发时触发。上层（AemiChatService → App）订阅后转交给 UI 层（如桌宠气泡）。
    /// 注意：Timer.Elapsed 回调在 ThreadPool 线程触发，订阅者需自行切换到 UI 线程。
    /// </summary>
    public event EventHandler<string>? ReminderTriggered;

    [KernelFunction("set_reminder")]
    [Description("设置定时提醒")]
    public string SetReminder(
        [Description("提醒内容")] string message,
        [Description("延迟时间（分钟）")] int delayMinutes)
    {
        try
        {
            var timer = new Timer(delayMinutes * 60 * 1000);
            timer.Elapsed += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine($"⏰ 提醒：{message}");
                try
                {
                    ReminderTriggered?.Invoke(this, message);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"提醒事件订阅者异常：{ex.Message}");
                }
                timer.Dispose();
                lock (_timerLock)
                {
                    _activeTimers.Remove(timer);
                }
            };
            
            timer.Start();
            lock (_timerLock)
            {
                _activeTimers.Add(timer);
            }
            
            return $"已设置提醒：{delayMinutes}分钟后提醒 \"{message}\"";
        }
        catch (Exception ex)
        {
            return $"设置提醒失败：{ex.Message}";
        }
    }
}
