namespace Aemeath.Core.Tools;

public sealed class PendingToolAction
{
    private readonly Func<string>? _execute;
    private readonly Func<Task<string>>? _executeAsync;

    /// <summary>构造一个同步执行的操作（用于快速操作：删文件等）。</summary>
    public PendingToolAction(string id, string title, string description, Func<string> execute)
    {
        Id = id;
        Title = title;
        Description = description;
        CreatedAt = DateTimeOffset.UtcNow;
        _execute = execute;
    }

    /// <summary>构造一个异步执行的长任务（如电脑控制）。确认后由 UI 层在后台线程 await。</summary>
    public PendingToolAction(string id, string title, string description, Func<Task<string>> executeAsync, bool isLongRunning)
    {
        Id = id;
        Title = title;
        Description = description;
        CreatedAt = DateTimeOffset.UtcNow;
        _executeAsync = executeAsync;
        IsLongRunning = isLongRunning;
    }

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// 是否为长任务（如电脑控制）。长任务必须异步确认，避免阻塞 UI 线程。
    /// 由 RequestConfirmation 时指定，标记给 UI 层做不同的确认反馈。
    /// </summary>
    public bool IsLongRunning { get; init; }

    /// <summary>同步执行闭包（仅快速操作）。长任务请用 <see cref="ExecuteAsync"/>。</summary>
    public string Execute() => _execute?.Invoke() ?? "该操作无同步执行闭包";

    /// <summary>
    /// 异步执行闭包。若提供的是同步闭包，包一层返回；否则直接 await 异步闭包。
    /// 调用方应在后台线程 await，避免阻塞 UI。
    /// </summary>
    public Task<string> ExecuteAsync()
    {
        if (_executeAsync is not null)
        {
            return _executeAsync();
        }

        if (_execute is not null)
        {
            return Task.FromResult(_execute());
        }

        return Task.FromResult("该操作无可执行闭包");
    }
}
