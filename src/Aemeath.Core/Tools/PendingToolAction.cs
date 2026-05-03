namespace Aemeath.Core.Tools;

public sealed class PendingToolAction
{
    private readonly Func<string> _execute;

    public PendingToolAction(string id, string title, string description, Func<string> execute)
    {
        Id = id;
        Title = title;
        Description = description;
        CreatedAt = DateTimeOffset.UtcNow;
        _execute = execute;
    }

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public DateTimeOffset CreatedAt { get; }

    public string Execute() => _execute();
}
