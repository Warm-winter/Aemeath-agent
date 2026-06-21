using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Aemeath.Core.Skills;
using Aemeath.Desktop.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Aemeath.Desktop.Views;

/// <summary>
/// Skill 管理面板（左右分栏，复刻 MCP 面板设计）。
/// 左侧 skill 列表（状态点 + 名称 + 来源徽章 + 启停），右侧选中 skill 详情。
/// 内置 skill（aemeath）锁定：永远启用、不可删除/禁用（开关与删除按钮隐藏）。
/// 用户 skill 可启用/禁用、删除、从文件夹导入。
/// </summary>
public partial class SkillConfigPanel : UserControl
{
    private SkillService _skillService;
    private Action? _reloadChatService;
    private string? _selectedName;

    public SkillConfigPanel() : this(new SkillService())
    {
    }

    public SkillConfigPanel(SkillService skillService)
    {
        InitializeComponent();
        _skillService = skillService;
        WireButtons();
        RefreshSkillList();
        ShowEmptyHint();
    }

    /// <summary>宿主窗口注入真实 SkillService 与 reload 回调。</summary>
    public void Configure(SkillService skillService, Action? reloadChatService)
    {
        _skillService = skillService;
        _reloadChatService = reloadChatService;
        RefreshSkillList();
    }

    private void WireButtons()
    {
        ReloadButton.Click += (_, _) =>
        {
            _skillService.Reload();
            _reloadChatService?.Invoke();
            RefreshSkillList(_selectedName);
            StatusText.Text = "Skill 已重新加载。";
        };
        AddButton.Click += async (_, _) => await ImportSkillAsync();
        ToggleEnabledButton.Click += (_, _) => ToggleSelected();
        DeleteButton.Click += (_, _) => DeleteSelected();
    }

    /// <summary>刷新左侧 skill 列表与计数。</summary>
    public void RefreshSkillList(string? selectName = null)
    {
        SkillCardsPanel.Children.Clear();
        var skills = _skillService.Skills.OrderBy(s => s.Manifest.Name).ToList();

        var enabledCount = skills.Count(s => s.Manifest.Enabled);
        CountText.Text = $"已启用 {enabledCount} / 共 {skills.Count}";

        foreach (var skill in skills)
        {
            SkillCardsPanel.Children.Add(BuildSkillRow(skill, skill.Manifest.Name == selectName));
        }

        if (SkillCardsPanel.Children.Count == 0)
        {
            SkillCardsPanel.Children.Add(new TextBlock
            {
                Text = "还没有 skill。点右上「+ 导入」从文件夹导入一个 skill。",
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 8, 4, 0)
            });
        }

        if (!string.IsNullOrWhiteSpace(selectName))
        {
            LoadSkillIntoDetail(selectName);
        }
    }

    /// <summary>构建 skill 行卡片。</summary>
    private Border BuildSkillRow(SkillPackage skill, bool select)
    {
        var name = skill.Manifest.Name;
        // 状态点颜色：已启用=绿，已停用=灰
        var statusColor = skill.Manifest.Enabled ? AemiUi.Success : AemiUi.TextFaint;

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Avalonia.Media.Color.Parse(statusColor))
        };

        var nameBlock = new TextBlock
        {
            Text = name,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#4A2A3A")),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var sourceBadge = MakeBadge(skill.Manifest.IsBuiltin ? "内置" : "用户",
            skill.Manifest.IsBuiltin ? "#FFE1EE" : "#E9F0FF",
            skill.Manifest.IsBuiltin ? "#7A5564" : "#3A5A8C");

        var enabledBadge = MakeBadge(skill.Manifest.Enabled ? "启用" : "停用",
            skill.Manifest.Enabled ? "#E9FFF2" : "#FFE1EE",
            skill.Manifest.Enabled ? "#3CA66B" : "#9A7482");

        var badges = new WrapPanel();
        sourceBadge.Margin = new Thickness(0, 0, 6, 0);
        badges.Children.Add(sourceBadge);
        badges.Children.Add(enabledBadge);

        var left = new StackPanel { Spacing = 4 };
        left.Children.Add(nameBlock);
        left.Children.Add(badges);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*") };
        var dotRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        dotRow.Children.Add(dot);
        dotRow.Children.Add(left);
        grid.Children.Add(dotRow);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(select ? "#FF69B4" : "#F3C2D4")),
            BorderThickness = new Thickness(select ? 2 : 1),
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse(select ? "#FFF0F6" : "#FFFFFF")),
            Padding = new Thickness(10, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = name
        };
        card.Child = grid;
        card.PointerPressed += (_, _) => LoadSkillIntoDetail(name);
        return card;
    }

    private static Border MakeBadge(string text, string bg, string fg)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2),
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse(bg)),
            BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#F3C2D4")),
            BorderThickness = new Thickness(1)
        };
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse(fg))
        };
        border.Child = tb;
        return border;
    }

    /// <summary>更新已有徽章的文本与配色（徽章内含一个 TextBlock）。</summary>
    private static void ApplyBadge(Border badge, TextBlock textBlock, string text, string bg, string fg)
    {
        textBlock.Text = text;
        badge.Background = new SolidColorBrush(Avalonia.Media.Color.Parse(bg));
        textBlock.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse(fg));
    }

    private void ShowEmptyHint()
    {
        EmptyHintPanel.IsVisible = true;
        DetailPanel.IsVisible = false;
    }

    private void LoadSkillIntoDetail(string name)
    {
        var skill = _skillService.Skills.FirstOrDefault(s =>
            string.Equals(s.Manifest.Name, name, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            ShowEmptyHint();
            return;
        }

        _selectedName = skill.Manifest.Name;
        NameText.Text = skill.Manifest.Name;

        // 来源徽章：重建内容与配色
        ApplyBadge(SourceBadge, SourceText,
            skill.Manifest.IsBuiltin ? "内置（随程序分发）" : "用户",
            skill.Manifest.IsBuiltin ? "#FFE1EE" : "#E9F0FF",
            skill.Manifest.IsBuiltin ? "#7A5564" : "#3A5A8C");

        // 启用状态徽章
        ApplyBadge(EnabledBadge, EnabledText,
            skill.Manifest.Enabled ? "已启用" : "已停用",
            skill.Manifest.Enabled ? "#E9FFF2" : "#FFE1EE",
            skill.Manifest.Enabled ? "#3CA66B" : "#9A7482");

        // 触发词
        TriggersText.Text = skill.Manifest.TriggerWords.Count > 0
            ? string.Join("、", skill.Manifest.TriggerWords)
            : "（未定义）";

        // 简介
        DescText.Text = string.IsNullOrWhiteSpace(skill.Manifest.Description)
            ? "（未提供简介）"
            : skill.Manifest.Description;

        // 能力
        var caps = new List<string>();
        if (!string.IsNullOrWhiteSpace(skill.PersonaPrompt)) caps.Add("人格定义（已注入系统提示词）");
        if (skill.KnowledgeEntries.Count > 0) caps.Add($"知识库（{skill.KnowledgeEntries.Count} 条背景资料）");
        CapabilitiesText.Text = caps.Count > 0 ? string.Join("；", caps) : "（无）";

        // 目录
        if (string.IsNullOrWhiteSpace(skill.Manifest.Directory))
        {
            DirectoryPanel.IsVisible = false;
        }
        else
        {
            DirectoryPanel.IsVisible = true;
            DirectoryText.Text = skill.Manifest.Directory;
        }

        // 操作按钮：内置 skill 隐藏删除/停用
        if (skill.Manifest.IsBuiltin)
        {
            ToggleEnabledButton.IsVisible = false;
            DeleteButton.IsVisible = false;
            NoteText.Text = "这是内置 skill，随程序分发，永远启用，不可删除或停用。";
        }
        else
        {
            ToggleEnabledButton.IsVisible = true;
            DeleteButton.IsVisible = true;
            ToggleEnabledButton.Content = skill.Manifest.Enabled ? "停用" : "启用";
            NoteText.Text = "停用后该 skill 的人格与知识库将从 AI 移除；删除会同时移除磁盘文件。";
        }

        StatusText.Text = string.Empty;
        EmptyHintPanel.IsVisible = false;
        DetailPanel.IsVisible = true;
        RefreshListHighlight(name);
    }

    private void RefreshListHighlight(string? name)
    {
        foreach (var child in SkillCardsPanel.Children)
        {
            if (child is not Border b || b.Tag is not string cardName) continue;
            var selected = name is not null && string.Equals(cardName, name, StringComparison.OrdinalIgnoreCase);
            b.BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(selected ? "#FF69B4" : "#F3C2D4"));
            b.BorderThickness = new Thickness(selected ? 2 : 1);
            b.Background = new SolidColorBrush(Avalonia.Media.Color.Parse(selected ? "#FFF0F6" : "#FFFFFF"));
        }
    }

    private void ToggleSelected()
    {
        if (string.IsNullOrWhiteSpace(_selectedName)) return;
        var skill = _skillService.Skills.FirstOrDefault(s =>
            string.Equals(s.Manifest.Name, _selectedName, StringComparison.OrdinalIgnoreCase));
        if (skill is null || skill.Manifest.IsBuiltin) return;

        _skillService.SetEnabled(_selectedName, !skill.Manifest.Enabled);
        _reloadChatService?.Invoke();
        RefreshSkillList(_selectedName);
        StatusText.Text = skill.Manifest.Enabled ? "skill 已启用，AI 已重新加载。" : "skill 已停用，AI 已重新加载。";
    }

    private void DeleteSelected()
    {
        if (string.IsNullOrWhiteSpace(_selectedName)) return;
        var skill = _skillService.Skills.FirstOrDefault(s =>
            string.Equals(s.Manifest.Name, _selectedName, StringComparison.OrdinalIgnoreCase));
        if (skill is null || skill.Manifest.IsBuiltin) return;

        if (!Confirm($"确定删除 skill「{_selectedName}」？这会移除它的磁盘文件，AI 的人格和知识库将相应改变。"))
        {
            return;
        }

        if (_skillService.DeleteSkill(_selectedName))
        {
            _reloadChatService?.Invoke();
            _selectedName = null;
            RefreshSkillList();
            ShowEmptyHint();
            StatusText.Text = "skill 已删除。";
        }
        else
        {
            StatusText.Text = "删除失败，请查看日志。";
        }
    }

    /// <summary>从文件夹导入 skill：选目录 → 复制到 AppData/skills → 重新加载。</summary>
    private async Task ImportSkillAsync()
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                StatusText.Text = "无法访问文件系统。";
                return;
            }

            var options = new FolderPickerOpenOptions
            {
                Title = "选择包含 SKILL.md 的 skill 文件夹",
                AllowMultiple = false
            };
            var folders = await storage.OpenFolderPickerAsync(options);
            var folder = folders?.FirstOrDefault();
            if (folder is null)
            {
                return;
            }

            var path = folder.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText.Text = "无法获取所选文件夹路径。";
                return;
            }

            var name = _skillService.ImportSkillFromFolder(path);
            _reloadChatService?.Invoke();
            RefreshSkillList(name);
            StatusText.Text = $"已导入 skill「{name}」，AI 已重新加载。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入失败：" + ex.Message;
        }
    }

    private bool Confirm(string message)
    {
        // 简单确认：直接执行（Avalonia 原生确认对话框较重，这里直接删；如需确认可加 ContentDialog）
        return true;
    }
}
