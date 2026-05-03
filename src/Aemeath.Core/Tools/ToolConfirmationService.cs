namespace Aemeath.Core.Tools;

public sealed class ToolConfirmationService
{
    public const string PendingMarkerPrefix = "AEMEATH_PENDING_CONFIRMATION:";

    private readonly object _sync = new();
    private readonly Dictionary<string, PendingToolAction> _pendingActions = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<PendingToolAction>? PendingActionCreated;

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

    public string RequestConfirmation(string title, string description, Func<string> execute)
    {
        var action = new PendingToolAction(Guid.NewGuid().ToString("N"), title, description, execute);
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

    public string Confirm(string id)
    {
        PendingToolAction? action;
        lock (_sync)
        {
            if (!_pendingActions.Remove(id, out action))
            {
                return "确认失败：该操作已不存在或已处理";
            }
        }

        try
        {
            return action.Execute();
        }
        catch (Exception ex)
        {
            return $"确认执行失败：{ex.Message}";
        }
    }

    public string Cancel(string id)
    {
        lock (_sync)
        {
            return _pendingActions.Remove(id)
                ? "已取消高风险操作"
                : "取消失败：该操作已不存在或已处理";
        }
    }
}
