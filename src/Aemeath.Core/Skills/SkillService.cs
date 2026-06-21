using System.Text.Json;
using Aemeath.Core.Knowledge;

namespace Aemeath.Core.Skills;

/// <summary>
/// Skill 管理服务：加载所有 skill（内置 + 用户自定义），提供人格提示词和知识条目的聚合查询，
/// 并支持面板管理（启用/禁用、删除、导入）。
///
/// 启用状态持久化在 %AppData%\Aemeath\skills_state.json（记录被禁用的用户 skill 名单）。
/// 内置 skill 恒启用、不可删除/禁用。
/// </summary>
public sealed class SkillService
{
    private readonly List<SkillPackage> _skills = new();
    private readonly object _loadLock = new();
    private bool _loaded;

    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Aemeath");
    private static readonly string UserSkillsDirectory = Path.Combine(StateDirectory, "skills");
    private static readonly string StateFilePath = Path.Combine(StateDirectory, "skills_state.json");

    /// <summary>已加载的所有 skill（只读视图，含禁用的）。</summary>
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

    /// <summary>用户自定义 skill 的存放目录（导入目标）。</summary>
    public string UserSkillsRoot => UserSkillsDirectory;

    /// <summary>加载所有 skill（内置 + 用户自定义）。幂等，重复调用只加载一次。</summary>
    public void LoadAll()
    {
        lock (_loadLock)
        {
            if (_loaded)
            {
                return;
            }
            DoLoad();
        }
    }

    /// <summary>强制重新加载（清缓存 + 重新读盘 + 应用启用状态）。面板变更后调用。</summary>
    public void Reload()
    {
        lock (_loadLock)
        {
            DoLoad();
        }
    }

    private void DoLoad()
    {
        try
        {
            var loader = new SkillLoader();
            _skills.Clear();
            _skills.AddRange(loader.LoadAll());
            ApplyEnabledState();
        }
        catch
        {
            // 加载失败不阻断主流程
        }
        _loaded = true;
    }

    /// <summary>从状态文件读取被禁用的 skill 名单，应用到已加载的 skill（内置不受影响）。</summary>
    private void ApplyEnabledState()
    {
        var disabled = ReadDisabledSet();
        foreach (var skill in _skills)
        {
            if (skill.Manifest.IsBuiltin)
            {
                skill.Manifest.Enabled = true; // 内置恒启用
            }
            else
            {
                skill.Manifest.Enabled = !disabled.Contains(skill.Manifest.Name);
            }
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
    /// 聚合所有「已启用」skill 的人格提示词，拼接到系统提示词。
    /// </summary>
    public string GetPersonaPrompt()
    {
        EnsureLoaded();
        var personas = _skills
            .Where(s => s.Manifest.Enabled && !string.IsNullOrWhiteSpace(s.PersonaPrompt))
            .Select(s => s.PersonaPrompt)
            .ToList();

        if (personas.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("\n\n---\n\n", personas);
    }

    /// <summary>聚合所有「已启用」skill 提供的知识库条目。</summary>
    public IReadOnlyList<KnowledgeBaseEntry> GetKnowledgeEntries()
    {
        EnsureLoaded();
        var entries = new List<KnowledgeBaseEntry>();
        foreach (var skill in _skills.Where(s => s.Manifest.Enabled))
        {
            entries.AddRange(skill.KnowledgeEntries);
        }
        return entries;
    }

    // ===== 面板管理操作 =====

    /// <summary>
    /// 切换 skill 启用状态。内置 skill 拒绝禁用（保持启用）。
    /// 写状态文件 + Reload。
    /// </summary>
    public void SetEnabled(string skillName, bool enabled)
    {
        lock (_loadLock)
        {
            var skill = _skills.FirstOrDefault(s =>
                string.Equals(s.Manifest.Name, skillName, StringComparison.OrdinalIgnoreCase));
            if (skill is null || skill.Manifest.IsBuiltin)
            {
                return; // 不存在或内置，拒绝操作
            }

            var disabled = ReadDisabledSet();
            if (enabled)
            {
                disabled.Remove(skill.Manifest.Name);
            }
            else
            {
                disabled.Add(skill.Manifest.Name);
            }
            WriteDisabledSet(disabled);
            DoLoad(); // 重新加载应用新状态
        }
    }

    /// <summary>
    /// 删除用户 skill（连同磁盘目录）。内置 skill 拒绝删除。
    /// </summary>
    public bool DeleteSkill(string skillName)
    {
        lock (_loadLock)
        {
            var skill = _skills.FirstOrDefault(s =>
                string.Equals(s.Manifest.Name, skillName, StringComparison.OrdinalIgnoreCase));
            if (skill is null || skill.Manifest.IsBuiltin)
            {
                return false;
            }

            // 删除磁盘目录
            var dir = skill.Manifest.Directory;
            if (!string.IsNullOrWhiteSpace(dir) && System.IO.Directory.Exists(dir))
            {
                try
                {
                    System.IO.Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    return false;
                }
            }

            // 从禁用集合里也清掉
            var disabled = ReadDisabledSet();
            disabled.Remove(skill.Manifest.Name);
            WriteDisabledSet(disabled);

            DoLoad();
            return true;
        }
    }

    /// <summary>
    /// 从源目录导入 skill：复制到 %AppData%\Aemeath\skills\&lt;name&gt;\，然后 Reload。
    /// 要求源目录含 SKILL.md。返回导入后的 skill 名（目录名）；失败抛异常。
    /// </summary>
    public string ImportSkillFromFolder(string sourceDirectory)
    {
        if (!System.IO.Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException("源目录不存在：" + sourceDirectory);
        }

        if (!File.Exists(Path.Combine(sourceDirectory, "SKILL.md")))
        {
            throw new InvalidOperationException("所选目录必须包含 SKILL.md 文件。");
        }

        var name = Path.GetFileName(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("无法解析 skill 名称。");
        }

        System.IO.Directory.CreateDirectory(UserSkillsDirectory);
        var destDir = Path.Combine(UserSkillsDirectory, name);

        // 已存在则先清空（覆盖导入）
        if (System.IO.Directory.Exists(destDir))
        {
            System.IO.Directory.Delete(destDir, recursive: true);
        }

        CopyDirectory(sourceDirectory, destDir);

        // 导入后默认启用（从禁用集合移除）
        var disabled = ReadDisabledSet();
        disabled.Remove(name);
        WriteDisabledSet(disabled);

        lock (_loadLock)
        {
            DoLoad();
        }
        return name;
    }

    private static void CopyDirectory(string source, string dest)
    {
        System.IO.Directory.CreateDirectory(dest);
        foreach (var file in System.IO.Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var sub in System.IO.Directory.EnumerateDirectories(source))
        {
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
        }
    }

    // ===== 状态文件持久化 =====

    private static HashSet<string> ReadDisabledSet()
    {
        try
        {
            if (!File.Exists(StateFilePath))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            var json = File.ReadAllText(StateFilePath);
            var doc = JsonSerializer.Deserialize<SkillsStateFile>(json);
            if (doc?.DisabledSkills is null)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            return new HashSet<string>(doc.DisabledSkills, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteDisabledSet(HashSet<string> disabled)
    {
        try
        {
            System.IO.Directory.CreateDirectory(StateDirectory);
            var doc = new SkillsStateFile { DisabledSkills = disabled.ToList() };
            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StateFilePath, json);
        }
        catch
        {
            // 状态写入失败不阻断操作
        }
    }

    private sealed class SkillsStateFile
    {
        public List<string> DisabledSkills { get; set; } = new();
    }
}
