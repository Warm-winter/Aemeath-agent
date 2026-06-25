namespace Aemeath.Core.Tools;

/// <summary>
/// 高风险工具操作的确认中枢。
///
/// 工作流：
/// 1. 工具插件遇到高风险操作时调 <see cref="RequestConfirmation"/>（或异步版），传入待执行闭包，拿到一个 pending marker
/// 2. marker 作为「工具结果」回到模型回复里，UI 订阅 <see cref="PendingActionCreated"/> 弹确认卡片
/// 3. 用户点确认 → <see cref="ConfirmAsync"/>：闭包在**后台线程**执行（绝不阻塞 UI 线程，否则长任务如电脑控制会冻死界面）
/// 4. 执行完成（成功/失败/取消）触发 <see cref="PendingActionCompleted"/>，UI 把结果回填到聊天
///
/// 关键约束：闭包绝不在 UI 线程上同步执行。即使文件删除这类快操作也走后台，保持一致与安全。
/// </summary>
public sealed class ToolConfirmationService
{
    public const string PendingMarkerPrefix = "AEMEATH_PENDING_CONFIRMATION:";

    private readonly object _sync = new();
    private readonly Dictionary<string, PendingToolAction> _pendingActions = new(StringComparer.OrdinalIgnoreCase);
    // 正在后台执行中的 id，避免重复执行
    private readonly HashSet<string> _executing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>有新的待确认动作产生（UI 据此弹卡片）。</summary>
    public event EventHandler<PendingToolAction>? PendingActionCreated;

    /// <summary>动作执行完成（确认执行成功/失败/取消）。UI 据此把结果回填到聊天。</summary>
    public event EventHandler<PendingActionResultEventArgs>? PendingActionCompleted;

    public IReadOnlyList<PendingToolAction> PendingActions
    {
        get
        {
            lock (_sync)
            {
                return _pendingActions.Values
                    .OrderBy(x => x.CreatedAt)
                    .ToList();
            }
        }
    }

    /// <summary>
    /// 请求确认（同步闭包，快速操作用）。返回 pending marker（会作为工具结果回到模型回复里）。
    /// isLongRunning=true 时 UI 会给不同的等待反馈（如电脑控制的逐步进度）。
    /// </summary>
    public string RequestConfirmation(string title, string description, Func<string> execute, bool isLongRunning = false)
    {
        var action = new PendingToolAction(Guid.NewGuid().ToString("N"), title, description, execute)
        {
            IsLongRunning = isLongRunning
        };
        lock (_sync)
        {
            _pendingActions[action.Id] = action;
        }

        PendingActionCreated?.Invoke(this, action);
        return $"{PendingMarkerPrefix}{action.Id}";
    }

    /// <summary>
    /// 请求确认（异步长任务闭包，电脑控制用）。executeAsync 应是可在后台线程跑的长任务。
    /// 确认后由 <see cref="ConfirmAsync"/> 在后台线程 await 执行，不阻塞 UI。
    /// </summary>
    public string RequestConfirmation(string title, string description, Func<Task<string>> executeAsync, bool isLongRunning)
    {
        var action = new PendingToolAction(Guid.NewGuid().ToString("N"), title, description, executeAsync, isLongRunning);
        lock (_sync)
        {
            _pendingActions[action.Id] = action;
        }

        PendingActionCreated?.Invoke(this, action);
        return $"{PendingMarkerPrefix}{action.Id}";
    }

    public PendingToolAction? GetPendingAction(string id)
    {
        lock (_sync)
        {
            return _pendingActions.TryGetValue(id, out var action) ? action : null;
        }
    }

    /// <summary>
    /// 确认并执行：把闭包放到**后台线程**跑（绝不阻塞调用线程）。
    /// 立即返回 true 表示已派发执行；执行结果通过 <see cref="PendingActionCompleted"/> 事件异步上报。
    /// </summary>
    public bool ConfirmAsync(string id)
    {
        PendingToolAction? action;
        lock (_sync)
        {
            if (!_pendingActions.Remove(id, out action))
            {
                return false;
            }

            if (!_executing.Add(id))
            {
                // 已在执行中，不重复
                return false;
            }
        }

        var executingId = id;
        // 在后台线程执行闭包。无论闭包多慢，UI 线程都不会被阻塞。
        _ = Task.Run(async () =>
        {
            string result;
            bool success;
            try
            {
                // 用 ExecuteAsync：长任务（电脑控制）直接 await 异步闭包，避免 sync-over-async
                result = await action.ExecuteAsync().ConfigureAwait(false);
                success = true;
            }
            catch (Exception ex)
            {
                result = $"确认执行失败：{ex.Message}";
                success = false;
            }
            finally
            {
                lock (_sync)
                {
                    _executing.Remove(executingId);
                }
            }

            PendingActionCompleted?.Invoke(this, new PendingActionResultEventArgs(executingId, success, result));
        });

        return true;
    }

    /// <summary>取消（不执行）。立即触发完成事件，结果为「已取消」。</summary>
    public string Cancel(string id)
    {
        bool removed;
        lock (_sync)
        {
            removed = _pendingActions.Remove(id);
        }

        if (removed)
        {
            PendingActionCompleted?.Invoke(this, new PendingActionResultEventArgs(id, false, "已取消高风险操作"));
            return "已取消高风险操作";
        }

        return "取消失败：该操作已不存在或已处理";
    }
}

/// <summary>待确认动作执行完成的事件参数。</summary>
public sealed class PendingActionResultEventArgs : EventArgs
{
    public string Id { get; }
    public bool Success { get; }
    public string Result { get; }

    public PendingActionResultEventArgs(string id, bool success, string result)
    {
        Id = id;
        Success = success;
        Result = result;
    }
}
