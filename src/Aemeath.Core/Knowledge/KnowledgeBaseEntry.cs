namespace Aemeath.Core.Knowledge;

public sealed class KnowledgeBaseEntry
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = new();
    public string Content { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
}
