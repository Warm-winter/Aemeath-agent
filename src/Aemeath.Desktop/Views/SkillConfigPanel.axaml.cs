using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Aemeath.Core.Skills;
using Aemeath.Desktop.Services;

namespace Aemeath.Desktop.Views;

public partial class SkillConfigPanel : UserControl
{
    private SkillService _skillService;
    private Action? _reloadChatService;
    private string? _selectedName;
    private bool _isBusy;
    private bool _suppressSelectionChange;

    public SkillConfigPanel() : this(new SkillService())
    {
    }

    public SkillConfigPanel(SkillService skillService)
    {
        InitializeComponent();
        _skillService = skillService;
        WireEvents();
        RefreshSkillList();
        ShowEmptyHint();
    }

    public void Configure(SkillService skillService, Action? reloadChatService)
    {
        _skillService = skillService;
        _reloadChatService = reloadChatService;
        RefreshSkillList();
    }

    private void WireEvents()
    {
        ReloadButton.Click += (_, _) => ReloadSkills();
        AddButton.Click += async (_, _) => await ImportSkillAsync();
        ToggleEnabledButton.Click += (_, _) => ToggleSelected();
        DeleteButton.Click += async (_, _) => await DeleteSelectedAsync();
        SkillListBox.SelectionChanged += (_, _) => OnSkillSelectionChanged();
        SizeChanged += (_, e) => UpdateResponsiveLayout(e.NewSize.Width);
    }

    public void RefreshSkillList(string? selectName = null)
    {
        var skills = _skillService.Skills.OrderBy(skill => skill.Manifest.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _suppressSelectionChange = true;
        try
        {
            SkillListBox.Items.Clear();
            foreach (var skill in skills)
            {
                SkillListBox.Items.Add(BuildSkillItem(skill));
            }

            var enabledCount = skills.Count(skill => skill.Manifest.Enabled);
            CountText.Text = $"已启用 {enabledCount} / 共 {skills.Count}";
            if (skills.Count == 0)
            {
                SkillListBox.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock
                    {
                        Text = "还没有用户 Skill。使用“导入”选择包含 SKILL.md 的文件夹。",
                        Classes = { "muted" },
                        TextWrapping = TextWrapping.Wrap
                    },
                    IsEnabled = false
                });
            }

            var target = selectName ?? _selectedName;
            SkillListBox.SelectedItem = string.IsNullOrWhiteSpace(target)
                ? null
                : SkillListBox.Items.OfType<ListBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, target, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressSelectionChange = false;
        }

        if (!string.IsNullOrWhiteSpace(selectName))
        {
            LoadSkillIntoDetail(selectName);
        }
        else if (_selectedName is not null && skills.All(skill => !string.Equals(skill.Manifest.Name, _selectedName, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedName = null;
            ShowEmptyHint();
        }
    }

    private ListBoxItem BuildSkillItem(SkillPackage skill)
    {
        var statusColor = skill.Manifest.Enabled ? AemiUi.Success : AemiUi.TextFaint;
        var dot = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(5),
            Background = AemiUi.Brush(statusColor),
            VerticalAlignment = VerticalAlignment.Center
        };

        var badges = new WrapPanel();
        var sourceBadge = MakeBadge(
            skill.Manifest.IsBuiltin ? "内置" : "用户",
            skill.Manifest.IsBuiltin ? AemiUi.HaloSoft : AemiUi.InfoSurface,
            skill.Manifest.IsBuiltin ? AemiUi.TextSecondary : AemiUi.InfoForeground);
        sourceBadge.Margin = new Thickness(0, 0, 6, 0);
        badges.Children.Add(sourceBadge);
        badges.Children.Add(MakeBadge(
            skill.Manifest.Enabled ? "启用" : "停用",
            skill.Manifest.Enabled ? AemiUi.SuccessSurface : AemiUi.HaloSoft,
            skill.Manifest.Enabled ? AemiUi.Success : AemiUi.TextMuted));

        var text = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = skill.Manifest.Name,
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = AemiUi.Brush(AemiUi.Ghost),
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                badges
            }
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { dot, text }
        };
        var item = new ListBoxItem { Content = row, Tag = skill.Manifest.Name };
        AutomationProperties.SetName(item, $"Skill {skill.Manifest.Name}，{(skill.Manifest.IsBuiltin ? "内置" : "用户")}，{(skill.Manifest.Enabled ? "已启用" : "已停用")}");
        return item;
    }

    private void OnSkillSelectionChanged()
    {
        if (_suppressSelectionChange || SkillListBox.SelectedItem is not ListBoxItem { Tag: string name })
        {
            return;
        }
        LoadSkillIntoDetail(name);
    }

    private void LoadSkillIntoDetail(string name)
    {
        var skill = _skillService.Skills.FirstOrDefault(candidate =>
            string.Equals(candidate.Manifest.Name, name, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            ShowEmptyHint();
            return;
        }

        _selectedName = skill.Manifest.Name;
        NameText.Text = skill.Manifest.Name;
        ApplyBadge(
            SourceBadge,
            SourceText,
            skill.Manifest.IsBuiltin ? "内置（随程序分发）" : "用户",
            skill.Manifest.IsBuiltin ? AemiUi.HaloSoft : AemiUi.InfoSurface,
            skill.Manifest.IsBuiltin ? AemiUi.TextSecondary : AemiUi.InfoForeground);
        ApplyBadge(
            EnabledBadge,
            EnabledText,
            skill.Manifest.Enabled ? "已启用" : "已停用",
            skill.Manifest.Enabled ? AemiUi.SuccessSurface : AemiUi.HaloSoft,
            skill.Manifest.Enabled ? AemiUi.Success : AemiUi.TextMuted);

        TriggersText.Text = skill.Manifest.TriggerWords.Count > 0
            ? string.Join("、", skill.Manifest.TriggerWords)
            : "（未定义）";
        DescText.Text = string.IsNullOrWhiteSpace(skill.Manifest.Description)
            ? "（未提供简介）"
            : skill.Manifest.Description;

        var capabilities = new List<string>();
        if (!string.IsNullOrWhiteSpace(skill.PersonaPrompt))
        {
            capabilities.Add("人格定义（已注入系统提示词）");
        }
        if (skill.KnowledgeEntries.Count > 0)
        {
            capabilities.Add($"知识库（{skill.KnowledgeEntries.Count} 条背景资料）");
        }
        CapabilitiesText.Text = capabilities.Count > 0 ? string.Join("；", capabilities) : "（无）";

        DirectoryPanel.IsVisible = !string.IsNullOrWhiteSpace(skill.Manifest.Directory);
        DirectoryText.Text = skill.Manifest.Directory ?? string.Empty;
        BuiltinLockPanel.IsVisible = skill.Manifest.IsBuiltin;
        ToggleEnabledButton.IsVisible = !skill.Manifest.IsBuiltin;
        DeleteButton.IsVisible = !skill.Manifest.IsBuiltin;
        ToggleEnabledButton.Content = skill.Manifest.Enabled ? "停用" : "启用";
        NoteText.Text = skill.Manifest.IsBuiltin
            ? "这是爱弥斯的内置角色 Skill，更新程序时会随版本同步。"
            : "停用会从 AI 上下文移除人格与知识；删除还会移除磁盘文件。";

        StatusText.Text = string.Empty;
        EmptyHintPanel.IsVisible = false;
        DetailPanel.IsVisible = true;
    }

    private void ReloadSkills()
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true, "正在重新加载 Skill…");
        try
        {
            _skillService.Reload();
            _reloadChatService?.Invoke();
            RefreshSkillList(_selectedName);
            SetStatus("Skill 已重新加载，AI 人格已刷新。", false);
        }
        catch (Exception ex)
        {
            SetStatus("重新加载失败：" + ex.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ToggleSelected()
    {
        if (_isBusy || string.IsNullOrWhiteSpace(_selectedName))
        {
            return;
        }

        var skill = _skillService.Skills.FirstOrDefault(candidate =>
            string.Equals(candidate.Manifest.Name, _selectedName, StringComparison.OrdinalIgnoreCase));
        if (skill is null || skill.Manifest.IsBuiltin)
        {
            return;
        }

        var enable = !skill.Manifest.Enabled;
        SetBusy(true, enable ? "正在启用 Skill…" : "正在停用 Skill…");
        try
        {
            _skillService.SetEnabled(_selectedName, enable);
            _reloadChatService?.Invoke();
            RefreshSkillList(_selectedName);
            SetStatus(enable ? "Skill 已启用，AI 已重新加载。" : "Skill 已停用，AI 已重新加载。", false);
        }
        catch (Exception ex)
        {
            SetStatus("更新 Skill 状态失败：" + ex.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (_isBusy || string.IsNullOrWhiteSpace(_selectedName) || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var skill = _skillService.Skills.FirstOrDefault(candidate =>
            string.Equals(candidate.Manifest.Name, _selectedName, StringComparison.OrdinalIgnoreCase));
        if (skill is null || skill.Manifest.IsBuiltin)
        {
            return;
        }

        if (!await ConfirmUserSkillDeletionAsync(owner, _selectedName))
        {
            return;
        }

        SetBusy(true, "正在删除 Skill…");
        try
        {
            if (!_skillService.DeleteSkill(_selectedName))
            {
                SetStatus("删除失败，请查看日志。", true);
                return;
            }

            _reloadChatService?.Invoke();
            _selectedName = null;
            RefreshSkillList();
            ShowEmptyHint();
            SetStatus("Skill 已删除。", false);
        }
        catch (Exception ex)
        {
            SetStatus("删除失败：" + ex.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    internal static async Task<bool> ConfirmUserSkillDeletionAsync(
        Window owner,
        string skillName,
        Func<Window, string, string, string, Task<bool>>? confirmationHandler = null)
    {
        confirmationHandler ??= static (window, title, message, confirmText) =>
            DialogService.ConfirmAsync(window, title, message, confirmText);

        if (!await confirmationHandler(
                owner,
                "删除用户 Skill",
                $"删除“{skillName}”会同时移除它的磁盘文件、人格定义和知识库。是否继续？",
                "继续"))
        {
            return false;
        }

        return await confirmationHandler(
            owner,
            "最后确认",
            $"这是最后一次确认：确定永久删除 Skill“{skillName}”吗？",
            "永久删除");
    }

    private async Task ImportSkillAsync()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                SetStatus("无法访问文件系统。", true);
                return;
            }

            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择包含 SKILL.md 的 Skill 文件夹",
                AllowMultiple = false
            });
            var folder = folders.FirstOrDefault();
            if (folder is null)
            {
                return;
            }

            var path = folder.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                SetStatus("无法获取所选文件夹路径。", true);
                return;
            }

            SetBusy(true, "正在导入 Skill…");
            var name = _skillService.ImportSkillFromFolder(path);
            _reloadChatService?.Invoke();
            RefreshSkillList(name);
            SetStatus($"已导入 Skill“{name}”，AI 已重新加载。", false);
        }
        catch (Exception ex)
        {
            SetStatus("导入失败：" + ex.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        _isBusy = isBusy;
        BusyProgress.IsVisible = isBusy;
        ReloadButton.IsEnabled = !isBusy;
        AddButton.IsEnabled = !isBusy;
        ToggleEnabledButton.IsEnabled = !isBusy;
        DeleteButton.IsEnabled = !isBusy;
        if (!string.IsNullOrWhiteSpace(message))
        {
            SetStatus(message, false);
        }
    }

    private void SetStatus(string message, bool isError)
    {
        var brush = AemiUi.Brush(isError ? AemiUi.Error : AemiUi.TextSecondary);
        ActionStatusText.Text = message;
        ActionStatusText.Foreground = brush;
        StatusText.Text = message;
        StatusText.Foreground = brush;
    }

    private void ShowEmptyHint()
    {
        EmptyHintPanel.IsVisible = true;
        DetailPanel.IsVisible = false;
    }

    internal void UpdateResponsiveLayout(double width)
    {
        var narrow = width < 720;
        if (narrow)
        {
            SkillLayoutGrid.ColumnDefinitions = new ColumnDefinitions("*");
            SkillLayoutGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
            SkillListPane.Height = 252;
            SkillListPane.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(SkillListPane, 0);
            Grid.SetRow(SkillListPane, 0);
            Grid.SetColumn(SkillDetailPane, 0);
            Grid.SetRow(SkillDetailPane, 1);
            SkillDetailPane.Margin = new Thickness(0, 12, 0, 0);
            SkillListBox.MaxHeight = double.PositiveInfinity;
        }
        else
        {
            SkillLayoutGrid.ColumnDefinitions = new ColumnDefinitions("288,*");
            SkillLayoutGrid.RowDefinitions = new RowDefinitions("*");
            SkillListPane.Height = double.NaN;
            SkillListPane.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetColumn(SkillListPane, 0);
            Grid.SetRow(SkillListPane, 0);
            Grid.SetColumn(SkillDetailPane, 1);
            Grid.SetRow(SkillDetailPane, 0);
            SkillDetailPane.Margin = new Thickness(12, 0, 0, 0);
            SkillListBox.MaxHeight = double.PositiveInfinity;
        }
    }

    private static Border MakeBadge(string text, string background, string foreground)
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2),
            Background = AemiUi.Brush(background),
            BorderBrush = AemiUi.Brush(AemiUi.Border),
            BorderThickness = new Thickness(1)
        };
        badge.Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = AemiUi.Brush(foreground)
        };
        return badge;
    }

    private static void ApplyBadge(Border badge, TextBlock textBlock, string text, string background, string foreground)
    {
        textBlock.Text = text;
        badge.Background = AemiUi.Brush(background);
        badge.BorderBrush = AemiUi.Brush(AemiUi.Border);
        badge.BorderThickness = new Thickness(1);
        textBlock.Foreground = AemiUi.Brush(foreground);
    }
}
