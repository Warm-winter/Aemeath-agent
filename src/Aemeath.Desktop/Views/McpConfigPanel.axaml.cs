using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Aemeath.Core.MCP;
using Aemeath.Desktop.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Aemeath.Desktop.Views;

/// <summary>
/// MCP 配置面板（左右分栏，参考 cherry-studio 设计）。
/// 左侧服务列表（紧凑行卡片），右侧选中服务详情表单。
/// 顶部工具栏含环境依赖、重新加载、添加菜单（手动创建 / JSON 导入弹窗）。
/// 受保护的内置服务（memory/filesystem）在列表中隐藏。
/// </summary>
public partial class McpConfigPanel : UserControl
{
    private McpServerStore _store;
    private McpRuntimeService _runtime;
    private Action? _reloadChatService;
    private string? _selectedId;

    /// <summary>点击「检测/下载依赖」时触发，由 ConfigWindow 处理实际下载。</summary>
    public event EventHandler? DownloadDependenciesRequested;

    /// <summary>点击「一键配置内置服务」时触发，由 ConfigWindow 处理。</summary>
    public event EventHandler? SetupBuiltinRequested;

    /// <summary>点击「重新加载」时触发，让 ChatService 重新拉起 MCP 工具。</summary>
    public event EventHandler? ReloadRequested;

    public McpConfigPanel() : this(new McpServerStore(), null)
    {
    }

    public McpConfigPanel(McpServerStore store, Action? reloadChatService)
    {
        InitializeComponent();
        _store = store;
        _runtime = new McpRuntimeService(_store);
        _reloadChatService = reloadChatService;

        InitTransportOptions();
        WireButtons();
        RefreshServerList();
        ShowEmptyHint();
    }

    /// <summary>
    /// 在 XAML 声明场景下，面板默认用空 store 构造；宿主窗口可在加载后调用本方法
    /// 注入真实的 store 与 reload 回调，并刷新列表。重复调用会先 Dispose 旧的 runtime。
    /// </summary>
    public void Configure(McpServerStore store, Action? reloadChatService)
    {
        try
        {
            _ = _runtime.DisposeAsync();
        }
        catch
        {
            // 忽略旧的 runtime 释放失败
        }

        _store = store;
        _runtime = new McpRuntimeService(_store);
        _reloadChatService = reloadChatService;
        RefreshServerList();
    }

    private void InitTransportOptions()
    {
        TransportBox.Items.Clear();
        TransportBox.Items.Add(new ComboBoxItem { Content = "stdio（本地命令）", Tag = McpTransportType.Stdio });
        // SSE 选项暂时隐藏：SSE 连接方式存在超时问题，待修复后再恢复。
        // TransportBox.Items.Add(new ComboBoxItem { Content = "sse（流式 HTTP）", Tag = McpTransportType.Sse });
        TransportBox.Items.Add(new ComboBoxItem { Content = "http（可流式 HTTP）", Tag = McpTransportType.Http });
        TransportBox.SelectedIndex = 0;
    }

    private void WireButtons()
    {
        RefreshMcpButton.Click += (_, _) => ReloadRequested?.Invoke(this, EventArgs.Empty);
        DownloadMcpDependenciesButton.Click += (_, _) => DownloadDependenciesRequested?.Invoke(this, EventArgs.Empty);
        AddButton.Click += (_, _) => ShowAddMenu();
        SaveButton.Click += (_, _) => SaveCurrentServer();
        DeleteButton.Click += (_, _) => DeleteCurrentServer();
        TestButton.Click += async (_, _) => await TestCurrentServerAsync();
    }

    /// <summary>顶部「+ 添加」按钮的下拉菜单：手动创建 / 从 JSON 导入 / 配置内置服务。</summary>
    private void ShowAddMenu()
    {
        var menu = new ContextMenu();

        var manual = new MenuItem { Header = "手动创建" };
        manual.Click += (_, _) => StartNewServer();
        menu.Items.Add(manual);

        var import = new MenuItem { Header = "从 JSON 导入…" };
        import.Click += (_, _) => OpenImportDialog();
        menu.Items.Add(import);

        menu.Items.Add(new Separator());

        var builtin = new MenuItem { Header = "一键配置内置服务（记忆/文件系统）" };
        builtin.Click += (_, _) => SetupBuiltinRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(builtin);

        menu.Open(AddButton);
    }

    /// <summary>弹出 JSON 导入窗口（粘贴 { "mcpServers": {...} } 配置）。</summary>
    private void OpenImportDialog()
    {
        var dialog = new McpImportWindow();
        dialog.ImportRequested += (sender, json) =>
        {
            try
            {
                var imported = _store.ImportJson(json ?? string.Empty);
                _reloadChatService?.Invoke();
                RefreshServerList(imported.FirstOrDefault()?.Id);
                StatusText.Text = $"已导入 {imported.Count} 个 MCP 服务。";
                ShowStatusMessage();
                dialog.Close();
            }
            catch (Exception ex)
            {
                dialog.SetError("导入失败：" + ex.Message);
            }
        };
        dialog.Show();
    }

    /// <summary>更新顶部整体状态条文字（由 ConfigWindow 根据实时 McpStatus 喂入）。</summary>
    public void UpdateOverallStatus(string text)
    {
        // McpStatusChanged 可能在后台线程触发，UI 文本必须切回 UI 线程设置（CON-009）。
        Dispatcher.UIThread.Post(() => OverallStatusText.Text = text);
    }

    /// <summary>更新依赖检测状态文字（空串则隐藏）。</summary>
    public void UpdateDependencyStatus(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                McpDependencyStatusText.IsVisible = false;
                return;
            }
            McpDependencyStatusText.Text = text;
            McpDependencyStatusText.IsVisible = true;
        });
    }

    /// <summary>重新拉取服务卡片列表并刷新计数与状态徽章。</summary>
    public void RefreshServerList(string? selectId = null)
    {
        ServerCardsPanel.Children.Clear();
        // 受保护的内置服务（filesystem）对用户隐藏，避免误删核心功能。
        // 同时隐藏已废弃的旧内置服务（memory——长期记忆改由 Mem0 提供），避免用户看到残留配置误删/困惑。
        var hiddenLegacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "memory" };
        var visible = _store.ListServers()
            .Where(s => !McpBuiltinRegistry.IsProtected(s.Id) && !hiddenLegacy.Contains(s.Id))
            .ToList();

        var enabledCount = visible.Count(s => s.Enabled);
        CountText.Text = $"已启用 {enabledCount} / 共 {visible.Count}";

        foreach (var server in visible)
        {
            ServerCardsPanel.Children.Add(BuildServerRow(server, server.Id == selectId));
        }

        if (ServerCardsPanel.Children.Count == 0)
        {
            ServerCardsPanel.Children.Add(new TextBlock
            {
                Text = "还没有 MCP 服务。点击右上「+ 添加」手动创建，或用「从 JSON 导入」快速开始。",
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 8, 4, 0)
            });
        }

        if (!string.IsNullOrWhiteSpace(selectId))
        {
            LoadServerIntoForm(selectId);
        }
    }

    /// <summary>构建紧凑的服务行（左侧状态点 + 名称 + 类型徽章，右侧启停开关）。</summary>
    private Border BuildServerRow(McpServerConfig server, bool select)
    {
        var id = server.Id;
        var statusColor = AemiUi.StatusColor(!server.Enabled ? null : server.LastStatus);
        var (statusText, statusBg, statusFg) = BuildStatusVisual(server);

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Avalonia.Media.Color.Parse(statusColor))
        };

        var nameBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(server.DisplayName) ? id : server.DisplayName,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#4A2A3A")),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var transportBadge = MakeBadge(server.Transport.ToString().ToUpperInvariant(), "#FFE1EE", "#7A5564");
        var statusBadge = MakeBadge(statusText, statusBg, statusFg);

        var badges = new WrapPanel();
        transportBadge.Margin = new Thickness(0, 0, 6, 0);
        badges.Children.Add(transportBadge);
        badges.Children.Add(statusBadge);

        var left = new StackPanel { Spacing = 4 };
        left.Children.Add(nameBlock);
        left.Children.Add(badges);

        // 启停开关
        var toggle = new Button
        {
            Content = server.Enabled ? "开" : "关",
            Classes = { server.Enabled ? "primary" : "ghost" },
            MinWidth = 44,
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Click += (_, _) =>
        {
            _store.SetEnabled(id, !server.Enabled);
            _reloadChatService?.Invoke();
            RefreshServerList(id);
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var dotRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        dotRow.Children.Add(dot);
        dotRow.Children.Add(left);
        grid.Children.Add(dotRow);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(select ? "#FF69B4" : "#F3C2D4")),
            BorderThickness = new Thickness(select ? 2 : 1),
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse(select ? "#FFF0F6" : "#FFFFFF")),
            Padding = new Thickness(10, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = id
        };
        card.Child = grid;
        card.PointerPressed += (_, _) => LoadServerIntoForm(id);
        return card;
    }

    private static Border MakeBadge(string text, string bg, string fg)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2),
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse(bg)),
            BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#F3C2D4")),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse(fg))
            }
        };
    }

    private static (string text, string bg, string fg) BuildStatusVisual(McpServerConfig server)
    {
        if (!server.Enabled)
        {
            return ("已停用", "#FFE1EE", "#9A7482");
        }

        if (string.Equals(server.LastStatus, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return ("正常", "#E9FFF2", "#3CA66B");
        }

        if (!string.IsNullOrWhiteSpace(server.LastError) && server.LastError.Contains("超时", StringComparison.OrdinalIgnoreCase))
        {
            return ("超时", "#FFF3E0", "#C77B12");
        }

        if (!string.IsNullOrWhiteSpace(server.LastError))
        {
            return ("失败", "#FFEAF0", "#D94A62");
        }

        return ("未加载", "#FFE1EE", "#7A5564");
    }

    // ===== 表单操作（保留原有逻辑） =====

    private void ShowEmptyHint()
    {
        EmptyHintPanel.IsVisible = true;
        EditFormPanel.IsVisible = false;
    }

    private void ShowEditForm()
    {
        EmptyHintPanel.IsVisible = false;
        EditFormPanel.IsVisible = true;
    }

    private void ShowStatusMessage()
    {
        // 状态文字在表单的 StatusText，确保表单可见
        if (!EditFormPanel.IsVisible)
        {
            ShowEditForm();
        }
    }

    private void StartNewServer()
    {
        _selectedId = null;
        EnabledBox.IsChecked = true;
        ServerIdBox.Text = string.Empty;
        ServerNameBox.Text = string.Empty;
        TransportBox.SelectedIndex = 0;
        UrlBox.Text = string.Empty;
        CommandBox.Text = string.Empty;
        ArgsBox.Text = string.Empty;
        WorkingDirectoryBox.Text = string.Empty;
        EnvBox.Text = string.Empty;
        HeadersBox.Text = string.Empty;
        StatusText.Text = "正在创建新的 MCP 服务，填写后点「保存」。";
        ShowEditForm();
        RefreshServerListHighlight(null);
    }

    private void LoadServerIntoForm(string id)
    {
        var server = _store.GetServer(id);
        if (server is null)
        {
            return;
        }

        _selectedId = server.Id;
        EnabledBox.IsChecked = server.Enabled;
        ServerIdBox.Text = server.Id;
        ServerNameBox.Text = server.Name;
        SelectTransport(server.Transport);
        UrlBox.Text = server.Url ?? string.Empty;
        CommandBox.Text = server.Command ?? string.Empty;
        ArgsBox.Text = string.Join(Environment.NewLine, server.Args);
        WorkingDirectoryBox.Text = server.WorkingDirectory ?? string.Empty;
        EnvBox.Text = FormatMap(server.Env);
        HeadersBox.Text = FormatMap(server.Headers);
        StatusText.Text = string.IsNullOrWhiteSpace(server.LastError)
            ? (server.LastStatus ?? "已加载服务。")
            : $"{server.LastStatus}：{server.LastError}";

        ShowEditForm();
        RefreshServerListHighlight(id);
    }

    private void RefreshServerListHighlight(string? id)
    {
        foreach (var child in ServerCardsPanel.Children)
        {
            if (child is not Border b || b.Tag is not string cardId) continue;
            var selected = id is not null && string.Equals(cardId, id, StringComparison.OrdinalIgnoreCase);
            b.BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(selected ? "#FF69B4" : "#F3C2D4"));
            b.BorderThickness = new Thickness(selected ? 2 : 1);
            b.Background = new SolidColorBrush(Avalonia.Media.Color.Parse(selected ? "#FFF0F6" : "#FFFFFF"));
        }
    }

    private void SelectTransport(McpTransportType transport)
    {
        foreach (var item in TransportBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is McpTransportType value && value == transport)
            {
                TransportBox.SelectedItem = item;
                return;
            }
        }
        // SSE 选项已隐藏：已有 SSE 配置在 UI 中找不到对应项时，回退到 HTTP（而非 Stdio），
        // 因为 HTTP 在传输模式上与 SSE 更接近，避免静默改变连接方式。
        if (transport == McpTransportType.Sse)
        {
            foreach (var item in TransportBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is McpTransportType value && value == McpTransportType.Http)
                {
                    TransportBox.SelectedItem = item;
                    return;
                }
            }
        }
        TransportBox.SelectedIndex = 0;
    }

    private McpServerConfig ReadForm()
    {
        var id = string.IsNullOrWhiteSpace(ServerIdBox.Text)
            ? ServerNameBox.Text ?? "mcp-server"
            : ServerIdBox.Text;
        return new McpServerConfig
        {
            Id = McpServerStore.NormalizeId(id),
            Name = string.IsNullOrWhiteSpace(ServerNameBox.Text) ? id.Trim() : ServerNameBox.Text.Trim(),
            Enabled = EnabledBox.IsChecked == true,
            Transport = TransportBox.SelectedItem is ComboBoxItem { Tag: McpTransportType transport } ? transport : McpTransportType.Stdio,
            Url = string.IsNullOrWhiteSpace(UrlBox.Text) ? null : UrlBox.Text.Trim(),
            Command = string.IsNullOrWhiteSpace(CommandBox.Text) ? null : CommandBox.Text.Trim(),
            Args = ReadLines(ArgsBox.Text),
            WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text) ? null : WorkingDirectoryBox.Text.Trim(),
            Env = ReadMap(EnvBox.Text),
            Headers = ReadMap(HeadersBox.Text)
        };
    }

    private void SaveCurrentServer()
    {
        try
        {
            var server = ReadForm();

            if (!string.IsNullOrWhiteSpace(_selectedId) && !string.Equals(_selectedId, server.Id, StringComparison.OrdinalIgnoreCase))
            {
                var existingTarget = _store.GetServer(server.Id);
                if (existingTarget is not null)
                {
                    StatusText.Text = $"保存失败：ID「{server.Id}」已被其他服务占用。";
                    return;
                }

                _store.SaveServer(server);
                _store.DeleteServer(_selectedId);
            }
            else
            {
                _store.SaveServer(server);
            }

            _reloadChatService?.Invoke();
            RefreshServerList(server.Id);
            StatusText.Text = "MCP 服务已保存，正在后台刷新。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存失败：" + ex.Message;
        }
    }

    private void DeleteCurrentServer()
    {
        var id = _selectedId ?? ServerIdBox.Text;
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        _store.DeleteServer(id);
        _reloadChatService?.Invoke();
        RefreshServerList();
        ShowEmptyHint();
        StatusText.Text = "MCP 服务已删除。";
    }

    private async Task TestCurrentServerAsync()
    {
        try
        {
            TestButton.IsEnabled = false;
            StatusText.Text = "正在测试 MCP 连接…";
            var server = ReadForm();
            var result = await _runtime.TestConnectionAsync(server);
            StatusText.Text = result.Message + (result.Tools.Count > 0
                ? " " + string.Join("、", result.Tools.Take(6).Select(t => t.ToolName))
                : string.Empty);
        }
        catch (Exception ex)
        {
            StatusText.Text = "测试失败：" + ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private static List<string> ReadLines(string? text)
        => (text ?? string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static Dictionary<string, string> ReadMap(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ReadLines(text))
        {
            var index = line.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            map[line[..index].Trim()] = line[(index + 1)..].Trim();
        }

        return map;
    }

    private static string FormatMap(IReadOnlyDictionary<string, string> map)
    {
        var sb = new StringBuilder();
        foreach (var kvp in map)
        {
            sb.Append(kvp.Key).Append('=').AppendLine(kvp.Value);
        }
        return sb.ToString().TrimEnd();
    }
}
