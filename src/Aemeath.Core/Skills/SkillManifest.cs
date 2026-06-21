namespace Aemeath.Core.Skills;

/// <summary>
/// Skill 元数据，来自 SKILL.md 顶部的 YAML frontmatter。
/// </summary>
public sealed class SkillManifest
{
    /// <summary>Skill 唯一标识（如 "aemeath"）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Skill 简介，说明这个 skill 做什么。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>是否为内置 skill（内置的不可删除、随程序分发）。</summary>
    public bool IsBuiltin { get; set; }

    /// <summary>触发词，用户说出后激活该 skill 的人格。</summary>
    public List<string> TriggerWords { get; set; } = new();

    /// <summary>来源标识："builtin:&lt;name&gt;" 或 "user:&lt;name&gt;"。</summary>
    public string SourceId => IsBuiltin ? $"builtin:{Name}" : $"user:{Name}";
}
