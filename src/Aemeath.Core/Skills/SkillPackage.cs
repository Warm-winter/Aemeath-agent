using Aemeath.Core.Knowledge;

namespace Aemeath.Core.Skills;

/// <summary>
/// 一个已加载的 Skill 完整数据。
/// Skill 可以提供三类能力：人格定义（拼到系统提示词）、知识库条目（检索注入）、（未来）工具。
/// </summary>
public sealed class SkillPackage
{
    /// <summary>Skill 元数据。</summary>
    public SkillManifest Manifest { get; set; } = new();

    /// <summary>
    /// 人格定义文本，会拼到系统提示词里（作为 system message 的一部分）。
    /// 通常包含 SKILL.md 正文 + interaction.md 等角色扮演规则。
    /// </summary>
    public string PersonaPrompt { get; set; } = string.Empty;

    /// <summary>
    /// 知识库条目（来自 memory.md 等背景资料），会并入 KnowledgeBaseService 参与检索。
    /// </summary>
    public List<KnowledgeBaseEntry> KnowledgeEntries { get; set; } = new();
}
