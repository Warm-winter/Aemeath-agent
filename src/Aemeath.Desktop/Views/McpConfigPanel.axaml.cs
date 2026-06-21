using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Aemeath.Core.MCP;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Aemeath.Desktop.Views;

/// <summary>
/// MCP 配置面板（嵌入 ConfigWindow 的「MCP 配置」Tab）。
/// 合并了原独立 McpConfigWindow 的全部服务管理能力（列表/增删改/测试/导入），
/// 并以小白友好的卡片式呈现：整体状态 → 环境准备 → 服务卡片 → 高级导入。
/// 依赖下载与内置服务配置的执行逻辑仍由 ConfigWindow 负责（通过事件回调），
/// 本面板只负责 MCP 服务本身的持久化与运行时测试。
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
        StartNewServer();
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
        TransportBox.Items.Add(new ComboBoxItem { Content = "sse（流式 HTTP）", Tag = McpTransportType.Sse });
        TransportBox.Items.Add(new ComboBoxItem { Content = "http（可流式 HTTP）", Tag = McpTransportType.Http });
        TransportBox.SelectedIndex = 0;
    }

    private void WireButtons()
    {
        NewServerButton.Click += (_, _) => StartNewServer();
        SaveButton.Click += (_, _) => SaveCurrentServer();
        DeleteButton.Click += (_, _) => DeleteCurrentServer();
        TestButton.Click += async (_, _) => await TestCurrentServerAsync();
        ImportJsonButton.Click += (_, _) => ImportJson();
        RefreshMcpButton.Click += (_, _) => ReloadRequested?.Invoke(this, EventArgs.Empty);
        DownloadMcpDependenciesButton.Click += (_, _) => DownloadDependenciesRequested?.Invoke(this, EventArgs.Empty);
        SetupBuiltinMcpButton.Click += (_, _) => SetupBuiltinRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>更新顶部整体状态条文字（由 ConfigWindow 根据实时 McpStatus 喂入）。</summary>
    public void UpdateOverallStatus(string text)
    {
        // McpStatusChanged 可能在后台线程触发，UI 文本必须切回 UI 线程设置（CON-009）。
        Dispatcher.UIThread.Post(() => OverallStatusText.Text = string.IsNullOrWhiteSpace(text) ? "未加载" : text);
    }

    /// <summary>刷新依赖状态文字（由 ConfigWindow 喂入）。</summary>
    public void UpdateDependencyStatus(string text)
    {
        Dispatcher.UIThread.Post(() => McpDependencyStatusText.Text = string.IsNullOrWhiteSpace(text) ? "尚未检测" : text);
    }

    /// <summary>重新拉取服务卡片列表并刷新状态徽章。</summary>
    public void RefreshServerList(string? selectId = null)
    {
        ServerCardsPanel.Children.Clear();
        // 受保护的内置服务（memory/filesystem）对用户隐藏，避免误删核心功能。
        // 它们在后台仍由 McpRuntimeService 强制启用。
        foreach (var server in _store.ListServers().Where(s => !McpBuiltinRegistry.IsProtected(s.Id)))
        {
            ServerCardsPanel.Children.Add(BuildServerCard(server, server.Id == selectId));
        }

        // 没有任何服务时给一个空状态提示
        if (ServerCardsPanel.Children.Count == 0)
        {
            ServerCardsPanel.Children.Add(new TextBlock
            {
                Text = "还没有 MCP 服务。点击右上「＋ 新增」手动添加，或用「一键配置内置服务」快速开始。",
                Classes = { "muted" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 8, 0, 0)
            });
        }

        // 若指定了要选中的服务，加载进表单
        if (!string.IsNullOrWhiteSpace(selectId))
        {
            LoadServerIntoForm(selectId);
        }
    }

    private Border BuildServerCard(McpServerConfig server, bool select)
    {
        var id = server.Id;

        // 状态徽章
        var (statusText, statusBg, statusFg) = BuildStatusVisual(server);

        var nameBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(server.DisplayName) ? id : server.DisplayName,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#4A2A3A"))
        };

        var transportBadge = MakeBadge(server.Transport.ToString().ToUpperInvariant(), "#FFE1EE", "#7A5564");
        var statusBadge = MakeBadge(statusText, statusBg, statusFg);

        var badges = new WrapPanel();
        transportBadge.Margin = new Avalonia.Thickness(0, 0, 6, 0);
        badges.Children.Add(transportBadge);
        badges.Children.Add(statusBadge);

        // 启停开关（ToggleSwitch 不可用时用按钮文字代替）
        var toggle = new Button
        {
            Content = server.Enabled ? "已启用" : "已停用",
            Classes = { server.Enabled ? "primary" : "ghost" },
            MinWidth = 76,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Click += (_, _) =>
        {
            _store.SetEnabled(id, !server.Enabled);
            _reloadChatService?.Invoke();
            RefreshServerList(id);
        };

        var card = new Border
        {
            CornerRadius = new Avalonia.CornerRadius(12),
            BorderBrush = select ? new SolidColorBrush(Avalonia.Media.Color.Parse("#FF69B4")) : new SolidColorBrush(Avalonia.Media.Color.Parse("#F3C2D4")),
            BorderThickness = new Avalonia.Thickness(select ? 2 : 1),
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse(select ? "#FFF0F6" : "#FFFFFF")),
            Padding = new Avalonia.Thickness(12, 10),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Tag = id
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var left = new StackPanel { Spacing = 6 };
        left.Children.Add(nameBlock);
        left.Children.Add(badges);
        grid.Children.Add(left);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        card.Child = grid;

        // 点卡片（非按钮区域）加载到表单
        card.PointerPressed += (_, _) => LoadServerIntoForm(id);
        return card;
    }

    private static Border MakeBadge(string text, string bg, string fg)
    {
        return new Border
        {
            CornerRadius = new Avalonia.CornerRadius(999),
            Padding = new Avalonia.Thickness(8, 2),
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse(bg)),
            BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#F3C2D4")),
            BorderThickness = new Avalonia.Thickness(1),
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
        // 取消卡片高亮
        foreach (var child in ServerCardsPanel.Children)
        {
            if (child is Border b) b.BorderThickness = new Avalonia.Thickness(1);
        }
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

        RefreshServerListHighlight(id);
    }

    private void RefreshServerListHighlight(string id)
    {
        foreach (var child in ServerCardsPanel.Children)
        {
            if (child is not Border b || b.Tag is not string cardId) continue;
            var selected = string.Equals(cardId, id, StringComparison.OrdinalIgnoreCase);
            b.BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(selected ? "#FF69B4" : "#F3C2D4"));
            b.BorderThickness = new Avalonia.Thickness(selected ? 2 : 1);
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
        StartNewServer();
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

    private void ImportJson()
    {
        try
        {
            var imported = _store.ImportJson(ImportJsonBox.Text ?? string.Empty);
            _reloadChatService?.Invoke();
            RefreshServerList(imported.FirstOrDefault()?.Id);
            StatusText.Text = $"已导入 {imported.Count} 个 MCP 服务。";
            ImportJsonBox.Text = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入失败：" + ex.Message;
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

    private static string FormatMap(Dictionary<string, string> map)
    {
        var sb = new StringBuilder();
        foreach (var kvp in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"{kvp.Key}={kvp.Value}");
        }

        return sb.ToString().TrimEnd();
    }
}
