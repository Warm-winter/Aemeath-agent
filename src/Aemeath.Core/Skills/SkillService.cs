using Aemeath.Core.Knowledge;

namespace Aemeath.Core.Skills;

/// <summary>
/// Skill 管理服务：启动时加载所有 skill，提供人格提示词和知识条目的聚合查询。
/// </summary>
public sealed class SkillService
{
    private readonly List<SkillPackage> _skills = new();
    private readonly object _loadLock = new();
    private bool _loaded;

    /// <summary>已加载的所有 skill（只读视图）。</summary>
    public IReadOnlyList<SkillPackage> Skills
    {
        get
        {
            EnsureLoaded();
            return _skills;
        }
    }

    /// <summary>是否已加载过 skill。</summary>
    public bool HasSkills => Skills.Count > 0;

    /// <summary>加载所有 skill（内置 + 用户自定义）。幂等，重复调用只加载一次。</summary>
    public void LoadAll()
    {
        lock (_loadLock)
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                var loader = new SkillLoader();
                _skills.Clear();
                _skills.AddRange(loader.LoadAll());
            }
            catch
            {
                // 加载失败不阻断主流程，按空 skill 集处理
            }
            _loaded = true;
        }
    }

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            LoadAll();
        }
    }

    /// <summary>
    /// 聚合所有 skill 的人格提示词，拼接到系统提示词。
    /// 如果没有加载任何 skill，返回空字符串（调用方应提供降级人格）。
    /// </summary>
    public string GetPersonaPrompt()
    {
        EnsureLoaded();
        if (_skills.Count == 0)
        {
            return string.Empty;
        }

        // 当前只有一个 skill（aemeath）时直接返回其 PersonaPrompt；
        // 多 skill 场景下按加载顺序拼接（带分隔）。
        var personas = _skills
            .Where(s => !string.IsNullOrWhiteSpace(s.PersonaPrompt))
            .Select(s => s.PersonaPrompt)
            .ToList();

        return string.Join("\n\n---\n\n", personas);
    }

    /// <summary>聚合所有 skill 提供的知识库条目。</summary>
    public IReadOnlyList<KnowledgeBaseEntry> GetKnowledgeEntries()
    {
        EnsureLoaded();
        var entries = new List<KnowledgeBaseEntry>();
        foreach (var skill in _skills)
        {
            entries.AddRange(skill.KnowledgeEntries);
        }
        return entries;
    }
}
