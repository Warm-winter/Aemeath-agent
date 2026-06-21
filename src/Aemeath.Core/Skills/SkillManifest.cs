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

    /// <summary>是否为内置 skill（内置的不可删除、随程序分发、永远启用）。</summary>
    public bool IsBuiltin { get; set; }

    /// <summary>
    /// 是否启用。内置 skill 恒为 true（锁定）；用户 skill 可在面板切换。
    /// 持久化在 skills_state.json。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// skill 所在目录绝对路径（用户 skill 才有；内置为 null，因为从内嵌资源加载）。
    /// 用于删除/导入操作。
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>触发词，用户说出后激活该 skill 的人格。</summary>
    public List<string> TriggerWords { get; set; } = new();

    /// <summary>来源标识："builtin:&lt;name&gt;" 或 "user:&lt;name&gt;"。</summary>
    public string SourceId => IsBuiltin ? $"builtin:{Name}" : $"user:{Name}";
}
