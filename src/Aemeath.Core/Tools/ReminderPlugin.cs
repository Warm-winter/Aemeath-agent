using Microsoft.SemanticKernel;
using System.ComponentModel;
using Timer = System.Timers.Timer;

namespace Aemeath.Core.Tools;

public class ReminderPlugin
{
    private readonly List<Timer> _activeTimers = new();
    private readonly object _timerLock = new();

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
