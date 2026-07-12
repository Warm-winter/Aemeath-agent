using System.Reflection;
using System.Text.RegularExpressions;
using Aemeath.Core.Knowledge;

namespace Aemeath.Core.Skills;

/// <summary>
/// Skill 加载器：从内嵌资源（内置 skill）和 %AppData%\Aemeath\skills（用户 skill）加载。
///
/// Skill 格式遵循 Agent Skill 约定：
/// - 每个 skill 是一个目录，含 SKILL.md 入口
/// - SKILL.md 顶部是 YAML frontmatter（--- 包裹），含 name / description / 触发词
/// - 同目录的其他 .md 文件作为人格/知识素材
///
/// 资源命名约定（内嵌）：Aemeath.Skills.&lt;name&gt;.&lt;file&gt;.md
/// </summary>
public sealed class SkillLoader
{
    private const string BuiltinResourcePrefix = "Aemeath.Skills.";
    private const string SkillFileName = "SKILL.md";
    private const string PersonaEndMarker = "<!-- persona-end -->";

    // 内置 skill 目录名 → 是否内置。新增内置 skill 时在这里登记。
    private static readonly string[] BuiltinSkillNames = { "aemeath" };

    private readonly string _userSkillsDirectory;

    public SkillLoader()
    {
        _userSkillsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Aemeath",
            "skills");
    }

    /// <summary>加载所有 skill（内置 + 用户自定义）。</summary>
    public IReadOnlyList<SkillPackage> LoadAll()
    {
        var packages = new List<SkillPackage>();
        packages.AddRange(LoadBuiltinSkills());
        packages.AddRange(LoadUserSkills());
        return packages;
    }

    /// <summary>加载内置 skill（从程序集内嵌资源）。</summary>
    private IReadOnlyList<SkillPackage> LoadBuiltinSkills()
    {
        var packages = new List<SkillPackage>();
        var assembly = Assembly.GetExecutingAssembly();
        var allResources = assembly.GetManifestResourceNames();

        foreach (var skillName in BuiltinSkillNames)
        {
            var skillResourcePrefix = BuiltinResourcePrefix + skillName + ".";
            var skillResourceSuffix = "." + SkillFileName;
            // 找 SKILL.md 入口：Aemeath.Skills.<name>.SKILL.md
            var entryResource = allResources.FirstOrDefault(r =>
                r.StartsWith(skillResourcePrefix, StringComparison.Ordinal) &&
                r.EndsWith(skillResourceSuffix, StringComparison.OrdinalIgnoreCase));

            if (entryResource is null)
            {
                continue;
            }

            var files = ReadBuiltinSkillFiles(allResources, skillResourcePrefix, entryResource);
            if (files.Count == 0)
            {
                continue;
            }

            var pkg = BuildPackage(files, isBuiltin: true, skillName);
            if (pkg is not null)
            {
                packages.Add(pkg);
            }
        }

        return packages;
    }

    /// <summary>读取内置 skill 的所有 .md 资源文件，返回 文件名(不含.md) → 内容。</summary>
    private static Dictionary<string, string> ReadBuiltinSkillFiles(
        string[] allResources,
        string skillResourcePrefix,
        string entryResource)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assembly = Assembly.GetExecutingAssembly();

        foreach (var resource in allResources)
        {
            if (!resource.StartsWith(skillResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // 资源名形如 Aemeath.Skills.aemeath.SKILL.md → 去掉前缀和 .md 得 "SKILL"
            var rest = resource[skillResourcePrefix.Length..];
            if (!rest.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileKey = rest[..^3]; // 去掉 ".md"

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            files[fileKey] = reader.ReadToEnd();
        }

        return files;
    }

    /// <summary>加载用户自定义 skill（从 %AppData%\Aemeath\skills\&lt;name&gt;\）。</summary>
    private IReadOnlyList<SkillPackage> LoadUserSkills()
    {
        var packages = new List<SkillPackage>();
        if (!Directory.Exists(_userSkillsDirectory))
        {
            return packages;
        }

        foreach (var dir in Directory.EnumerateDirectories(_userSkillsDirectory))
        {
            var entryPath = Path.Combine(dir, SkillFileName);
            if (!File.Exists(entryPath))
            {
                continue;
            }

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mdFile in Directory.EnumerateFiles(dir, "*.md"))
            {
                var fileKey = Path.GetFileNameWithoutExtension(mdFile);
                try
                {
                    files[fileKey] = File.ReadAllText(mdFile);
                }
                catch
                {
                    // 单个文件读失败跳过
                }
            }

            if (files.Count == 0)
            {
                continue;
            }

            var skillName = Path.GetFileName(dir);
            var pkg = BuildPackage(files, isBuiltin: false, skillName, directory: dir);
            if (pkg is not null)
            {
                packages.Add(pkg);
            }
        }

        return packages;
    }

    /// <summary>
    /// 把 skill 的文件集合组装成 SkillPackage。
    /// files 键为文件名（不含扩展名），SKILL 是入口。
    /// directory：用户 skill 的目录绝对路径（内置传 null）。
    /// </summary>
    private static SkillPackage? BuildPackage(
        Dictionary<string, string> files,
        bool isBuiltin,
        string fallbackName,
        string? directory = null)
    {
        if (!files.TryGetValue("SKILL", out var skillContent) || string.IsNullOrWhiteSpace(skillContent))
        {
            return null;
        }

        var (frontMatter, body) = SplitFrontMatter(skillContent);
        var manifest = ParseManifest(frontMatter, fallbackName);
        manifest.IsBuiltin = isBuiltin;
        manifest.Directory = directory;
        // 内置 skill 恒启用（锁定）；用户 skill 的 Enabled 由 SkillService 根据状态文件决定
        manifest.Enabled = isBuiltin;

        // 人格提示词：SKILL.md 正文 + interaction.md（互动风格、典型回复示例、禁区）
        var persona = ExtractPersonaBody(body);
        if (files.TryGetValue("interaction", out var interaction) && !string.IsNullOrWhiteSpace(interaction))
        {
            persona += "\n\n---\n\n" + interaction;
        }

        // 知识库条目：把背景类文件转成 KnowledgeBaseEntry
        var knowledge = new List<KnowledgeBaseEntry>();
        foreach (var (fileKey, content) in files)
        {
            if (fileKey.Equals("SKILL", StringComparison.OrdinalIgnoreCase) ||
                fileKey.Equals("interaction", StringComparison.OrdinalIgnoreCase))
            {
                continue; // SKILL/interaction 已进人格层
            }

            var entry = ToKnowledgeEntry(manifest.Name, fileKey, content);
            if (entry is not null)
            {
                knowledge.Add(entry);
            }
        }

        return new SkillPackage
        {
            Manifest = manifest,
            PersonaPrompt = persona,
            KnowledgeEntries = knowledge
        };
    }

    /// <summary>分离 YAML frontmatter（--- 包裹）和正文。</summary>
    internal static string ExtractPersonaBody(string body)
    {
        var markerIndex = body.IndexOf(PersonaEndMarker, StringComparison.OrdinalIgnoreCase);
        return (markerIndex >= 0 ? body[..markerIndex] : body).Trim();
    }

    /// <summary>?? YAML frontmatter?--- ???????</summary>
    private static (string frontMatter, string body) SplitFrontMatter(string content)
    {
        // 匹配开头的 ---\n...\n---
        var match = Regex.Match(content, @"^\s*---\s*\r?\n(.*?)\r?\n---\s*\r?\n", RegexOptions.Singleline);
        if (match.Success)
        {
            return (match.Groups[1].Value, content[match.Length..].TrimStart());
        }
        return (string.Empty, content.Trim());
    }

    /// <summary>从 frontmatter 解析 name / description / 触发词。</summary>
    private static SkillManifest ParseManifest(string frontMatter, string fallbackName)
    {
        var manifest = new SkillManifest { Name = fallbackName };
        if (string.IsNullOrWhiteSpace(frontMatter))
        {
            return manifest;
        }

        // 简易 YAML 解析（skill 的 frontmatter 很简单，不引入完整 YAML 库）
        var nameMatch = Regex.Match(frontMatter, @"^name:\s*(.+)$", RegexOptions.Multiline);
        if (nameMatch.Success)
        {
            var n = nameMatch.Groups[1].Value.Trim().Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(n))
            {
                manifest.Name = n;
            }
        }

        var descMatch = Regex.Match(frontMatter, @"^description:\s*\|?\s*\r?\n([\s\S]*?)(?=^\S|\Z)", RegexOptions.Multiline);
        if (descMatch.Success)
        {
            // 多行描述：去换行和前后空白
            manifest.Description = descMatch.Groups[1].Value
                .Replace("\n", " ")
                .Replace("\r", " ")
                .Trim();
        }
        else
        {
            var descSingle = Regex.Match(frontMatter, @"^description:\s*(.+)$", RegexOptions.Multiline);
            if (descSingle.Success)
            {
                manifest.Description = descSingle.Groups[1].Value.Trim().Trim('"', '\'');
            }
        }

        // 触发词：从 description 里提取「」内的词
        if (!string.IsNullOrWhiteSpace(manifest.Description))
        {
            foreach (Match m in Regex.Matches(manifest.Description, @"「([^」]+)」"))
            {
                var word = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(word))
                {
                    manifest.TriggerWords.Add(word);
                }
            }
        }

        return manifest;
    }

    /// <summary>把一个背景 .md 文件转成知识库条目。</summary>
    private static KnowledgeBaseEntry? ToKnowledgeEntry(string skillName, string fileKey, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var title = fileKey switch
        {
            "memory" => $"{skillName} 背景故事与剧情",
            "profile" => $"{skillName} 角色档案",
            "personality" => $"{skillName} 性格与价值观",
            "relations" => $"{skillName} 关系网络",
            "conflicts" => $"{skillName} 设定冲突与保守表述",
            _ => $"{skillName} {fileKey}"
        };

        var aliases = fileKey switch
        {
            "memory" => new List<string> { "剧情", "背景", "故事", "记忆", "3.0", "3.1", "3.2", "3.3", "远航星", "电子幽灵", "星海", skillName },
            "profile" => new List<string> { "档案", "身份", "基本信息", "设定", skillName },
            "personality" => new List<string> { "性格", "人格", "价值观", "动机", skillName },
            "relations" => new List<string> { "关系", "漂泊者", "家人", "朋友", "养父", skillName },
            "conflicts" => new List<string> { "冲突", "矛盾", "争议", "保守", skillName },
            _ => new List<string> { fileKey, skillName }
        };

        return new KnowledgeBaseEntry
        {
            Id = $"skill-{skillName}-{fileKey}",
            Title = title,
            Category = "Skill扩展",
            Aliases = aliases,
            // 截断到合理长度，避免单条知识库过长（12万字符上限由附件逻辑管，这里给知识库一个保守上限）
            Content = content.Length > 8000 ? content[..8000] + "\n...(资料已截断)" : content,
            SourceUrl = $"skill://{skillName}/{fileKey}"
        };
    }
}
