using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Aemeath.Core.MCP;
using Aemeath.Desktop.Services;
using System.Text;

namespace Aemeath.Desktop.Views;

public partial class McpConfigPanel : UserControl
{
    private sealed record McpFormSnapshot(
        string SelectedId,
        bool Enabled,
        McpTransportType Transport,
        string ServerId,
        string ServerName,
        string Command,
        string Args,
        string WorkingDirectory,
        string Environment,
        string Url,
        string Headers);

    private McpServerStore _store;
    private McpRuntimeService _runtime;
    private Action? _reloadChatService;
    private string? _selectedId;
    private bool _isLoadingForm;
    private bool _isBusy;
    private bool _isDirty;
    private bool _suppressSelectionChange;
    private McpFormSnapshot? _baselineSnapshot;

    internal Func<Window, string, string, string, Task<bool>> ConfirmationHandler { get; set; }
        = static (owner, title, message, confirmText) =>
            DialogService.ConfirmAsync(owner, title, message, confirmText);

    public event EventHandler? DownloadDependenciesRequested;
    public event EventHandler? SetupBuiltinRequested;
    public event EventHandler? ReloadRequested;

    public bool HasUnsavedChanges => _isDirty;

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
        WireEvents();
        RefreshServerList();
        ShowEmptyHint();
    }

    public void Configure(McpServerStore store, Action? reloadChatService)
    {
        try
        {
            _ = _runtime.DisposeAsync();
        }
        catch
        {
            // 旧运行时释放失败不应阻止设置页继续使用。
        }

        _store = store;
        _runtime = new McpRuntimeService(_store);
        _reloadChatService = reloadChatService;
        RefreshServerList();
    }

    public void DiscardUnsavedChanges()
    {
        if (!string.IsNullOrWhiteSpace(_selectedId) && _store.GetServer(_selectedId) is not null)
        {
            LoadServerIntoForm(_selectedId);
        }
        else
        {
            _baselineSnapshot = null;
            SetDirty(false);
            ShowEmptyHint();
            SelectListItem(null);
        }
    }

    private void InitTransportOptions()
    {
        TransportBox.Items.Clear();
        TransportBox.Items.Add(new ComboBoxItem { Content = "stdio（本地命令）", Tag = McpTransportType.Stdio });
        TransportBox.Items.Add(new ComboBoxItem { Content = "http（可流式 HTTP）", Tag = McpTransportType.Http });
        TransportBox.SelectedIndex = 0;
        UpdateTransportFields();
    }

    private void WireEvents()
    {
        RefreshMcpButton.Click += (_, _) => ReloadRequested?.Invoke(this, EventArgs.Empty);
        DownloadMcpDependenciesButton.Click += (_, _) => DownloadDependenciesRequested?.Invoke(this, EventArgs.Empty);
        AddButton.Click += (_, _) => ShowAddMenu();
        SaveButton.Click += async (_, _) => await SaveCurrentServerAsync();
        CancelChangesButton.Click += (_, _) => DiscardUnsavedChanges();
        DeleteButton.Click += async (_, _) => await DeleteCurrentServerAsync();
        TestButton.Click += async (_, _) => await TestCurrentServerAsync();
        ServerListBox.SelectionChanged += async (_, _) => await OnServerSelectionChangedAsync();
        SizeChanged += (_, e) => UpdateResponsiveLayout(e.NewSize.Width);

        ServerIdBox.TextChanged += (_, _) => MarkDirty();
        ServerNameBox.TextChanged += (_, _) => MarkDirty();
        CommandBox.TextChanged += (_, _) => MarkDirty();
        ArgsBox.TextChanged += (_, _) => MarkDirty();
        WorkingDirectoryBox.TextChanged += (_, _) => MarkDirty();
        UrlBox.TextChanged += (_, _) => MarkDirty();
        EnvBox.TextChanged += (_, _) => MarkDirty();
        HeadersBox.TextChanged += (_, _) => MarkDirty();
        EnabledBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty)
            {
                MarkDirty();
            }
        };
        TransportBox.SelectionChanged += (_, _) =>
        {
            UpdateTransportFields();
            MarkDirty();
        };
    }

    private void ShowAddMenu()
    {
        var menu = new ContextMenu();
        var manual = new MenuItem { Header = "手动创建" };
        manual.Click += async (_, _) => await StartNewServerAsync();
        menu.Items.Add(manual);

        var import = new MenuItem { Header = "从 JSON 导入…" };
        import.Click += async (_, _) => await OpenImportDialogAsync();
        menu.Items.Add(import);
        menu.Items.Add(new Separator());

        var builtin = new MenuItem { Header = "一键配置内置服务（记忆/文件系统）" };
        builtin.Click += (_, _) => SetupBuiltinRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(builtin);
        menu.Open(AddButton);
    }

    private async Task OpenImportDialogAsync()
    {
        if (!await ConfirmDiscardChangesAsync())
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            SetStatus("无法确定导入窗口的所有者。", true);
            return;
        }

        var dialog = new McpImportWindow();
        dialog.ImportRequested += (_, json) =>
        {
            try
            {
                var imported = _store.ImportJson(json ?? string.Empty);
                _reloadChatService?.Invoke();
                var selected = imported.FirstOrDefault()?.Id;
                RefreshServerList(selected);
                SetStatus($"已导入 {imported.Count} 个 MCP 服务。", false);
                dialog.Close();
            }
            catch (Exception ex)
            {
                dialog.SetError("导入失败：" + ex.Message);
            }
        };
        await dialog.ShowDialog(owner);
    }

    public void UpdateOverallStatus(string text)
    {
        Dispatcher.UIThread.Post(() => OverallStatusText.Text = text);
    }

    public void UpdateDependencyStatus(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            McpDependencyStatusText.IsVisible = !string.IsNullOrWhiteSpace(text);
            McpDependencyStatusText.Text = text;
        });
    }

    public void RefreshServerList(string? selectId = null)
    {
        var hiddenLegacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "memory" };
        var visible = _store.ListServers()
            .Where(server => !McpBuiltinRegistry.IsProtected(server.Id) && !hiddenLegacy.Contains(server.Id))
            .OrderBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _suppressSelectionChange = true;
        try
        {
            ServerListBox.Items.Clear();
            foreach (var server in visible)
            {
                ServerListBox.Items.Add(BuildServerItem(server));
            }

            var enabledCount = visible.Count(server => server.Enabled);
            CountText.Text = $"已启用 {enabledCount} / 共 {visible.Count}";
            if (visible.Count == 0)
            {
                ServerListBox.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock
                    {
                        Text = "还没有 MCP 服务。使用“添加”手动创建或从 JSON 导入。",
                        Classes = { "muted" },
                        TextWrapping = TextWrapping.Wrap
                    },
                    IsEnabled = false
                });
            }

            var target = selectId ?? _selectedId;
            SelectListItem(target);
        }
        finally
        {
            _suppressSelectionChange = false;
        }

        if (!string.IsNullOrWhiteSpace(selectId) && _store.GetServer(selectId) is not null)
        {
            LoadServerIntoForm(selectId);
        }
    }

    private ListBoxItem BuildServerItem(McpServerConfig server)
    {
        var statusColor = AemiUi.StatusColor(!server.Enabled ? null : server.LastStatus);
        var (statusText, statusBackground, statusForeground) = BuildStatusVisual(server);
        var dot = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(5),
            Background = AemiUi.Brush(statusColor),
            VerticalAlignment = VerticalAlignment.Center
        };

        var badges = new WrapPanel();
        var transportBadge = MakeBadge(server.Transport.ToString().ToUpperInvariant(), AemiUi.HaloSoft, AemiUi.TextSecondary);
        transportBadge.Margin = new Thickness(0, 0, 6, 0);
        badges.Children.Add(transportBadge);
        badges.Children.Add(MakeBadge(statusText, statusBackground, statusForeground));

        var text = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = server.DisplayName,
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = AemiUi.Brush(AemiUi.Ghost),
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                badges
            }
        };
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { dot, text }
        };

        var toggle = new Button
        {
            Content = server.Enabled ? "开" : "关",
            MinWidth = 46,
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Classes.Add(server.Enabled ? "primary" : "ghost");
        AutomationProperties.SetName(toggle, $"{(server.Enabled ? "停用" : "启用")} MCP 服务 {server.DisplayName}");
        toggle.Click += async (_, _) => await ToggleServerAsync(server);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(left);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        var item = new ListBoxItem { Content = grid, Tag = server.Id };
        AutomationProperties.SetName(item, $"MCP 服务 {server.DisplayName}，{statusText}");
        return item;
    }

    private async Task ToggleServerAsync(McpServerConfig server)
    {
        if (_isBusy || !await ConfirmDiscardChangesAsync())
        {
            return;
        }

        _store.SetEnabled(server.Id, !server.Enabled);
        _reloadChatService?.Invoke();
        RefreshServerList(server.Id);
        SetStatus(server.Enabled ? "MCP 服务已停用。" : "MCP 服务已启用。", false);
    }

    private async Task OnServerSelectionChangedAsync()
    {
        if (_suppressSelectionChange || ServerListBox.SelectedItem is not ListBoxItem { Tag: string id })
        {
            return;
        }

        if (string.Equals(id, _selectedId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!await ConfirmDiscardChangesAsync())
        {
            _suppressSelectionChange = true;
            SelectListItem(_selectedId);
            _suppressSelectionChange = false;
            return;
        }

        LoadServerIntoForm(id);
    }

    private async Task StartNewServerAsync()
    {
        if (!await ConfirmDiscardChangesAsync())
        {
            return;
        }

        _isLoadingForm = true;
        try
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
            ClearValidation();
            UpdateTransportFields();
            ShowEditForm();
            DeleteButton.IsEnabled = false;
            SetStatus("正在创建新的 MCP 服务，填写后选择“保存更改”。", false);
            SelectListItem(null);
        }
        finally
        {
            _isLoadingForm = false;
        }

        CaptureBaseline();
        ServerIdBox.Focus();
    }

    private void LoadServerIntoForm(string id)
    {
        var server = _store.GetServer(id);
        if (server is null)
        {
            ShowEmptyHint();
            return;
        }

        _isLoadingForm = true;
        try
        {
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
            UpdateTransportFields();
            ClearValidation();
            ShowEditForm();
            DeleteButton.IsEnabled = true;
            SetStatus(string.IsNullOrWhiteSpace(server.LastError)
                ? server.LastStatus ?? "已加载服务。"
                : $"{server.LastStatus}：{server.LastError}", !string.IsNullOrWhiteSpace(server.LastError));
            SelectListItem(server.Id);
        }
        finally
        {
            _isLoadingForm = false;
        }

        CaptureBaseline();
    }

    internal async Task<bool> SaveCurrentServerAsync()
    {
        if (_isBusy || !TryReadValidatedForm(out var server))
        {
            return false;
        }

        SetBusy(true, "正在保存 MCP 服务…");
        try
        {
            if (!string.IsNullOrWhiteSpace(_selectedId) && !string.Equals(_selectedId, server.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (_store.GetServer(server.Id) is not null)
                {
                    SetFieldError(ServerIdBox, ServerIdErrorText, $"ID“{server.Id}”已被其他服务占用。");
                    return false;
                }

                _store.SaveServer(server);
                _store.DeleteServer(_selectedId);
            }
            else
            {
                _store.SaveServer(server);
            }

            _selectedId = server.Id;
            _reloadChatService?.Invoke();
            SetDirty(false);
            RefreshServerList(server.Id);
            SetStatus("MCP 服务已保存，工具正在后台刷新。", false);
            await Task.Yield();
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("保存失败：" + ex.Message, true);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    internal async Task DeleteCurrentServerAsync(Window? ownerOverride = null)
    {
        var id = _selectedId;
        if (_isBusy || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var owner = ownerOverride ?? TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            return;
        }

        var server = _store.GetServer(id);
        if (server is null)
        {
            return;
        }

        var confirmed = await ConfirmationHandler(
            owner,
            "删除 MCP 服务",
            $"确定删除“{server.DisplayName}”吗？保存的命令、环境变量和请求头都会被移除。",
            "删除服务");
        if (!confirmed)
        {
            return;
        }

        SetBusy(true, "正在删除 MCP 服务…");
        try
        {
            if (!_store.DeleteServer(id))
            {
                SetStatus("删除失败：未找到该服务。", true);
                return;
            }

            _reloadChatService?.Invoke();
            _selectedId = null;
            _baselineSnapshot = null;
            SetDirty(false);
            RefreshServerList();
            ShowEmptyHint();
            SetStatus("MCP 服务已删除。", false);
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

    private async Task TestCurrentServerAsync()
    {
        if (_isBusy || !TryReadValidatedForm(out var server))
        {
            return;
        }

        SetBusy(true, "正在测试 MCP 连接…");
        try
        {
            var result = await _runtime.TestConnectionAsync(server);
            var tools = result.Tools.Count > 0
                ? " 工具：" + string.Join("、", result.Tools.Take(6).Select(tool => tool.ToolName))
                : string.Empty;
            SetStatus(result.Message + tools, !result.Success);
        }
        catch (Exception ex)
        {
            SetStatus("测试失败：" + ex.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool TryReadValidatedForm(out McpServerConfig server)
    {
        ClearValidation();
        var idText = ServerIdBox.Text?.Trim() ?? string.Empty;
        var normalizedId = McpServerStore.NormalizeId(idText);
        var transport = TransportBox.SelectedItem is ComboBoxItem { Tag: McpTransportType value }
            ? value
            : McpTransportType.Stdio;
        var valid = true;

        if (string.IsNullOrWhiteSpace(idText) || string.IsNullOrWhiteSpace(normalizedId))
        {
            SetFieldError(ServerIdBox, ServerIdErrorText, "请输入唯一的服务 ID。");
            valid = false;
        }

        var command = CommandBox.Text?.Trim();
        var url = UrlBox.Text?.Trim();
        if (transport == McpTransportType.Stdio && string.IsNullOrWhiteSpace(command))
        {
            SetFieldError(CommandBox, CommandErrorText, "stdio 连接必须填写命令。");
            valid = false;
        }
        else if (transport == McpTransportType.Http &&
                 (string.IsNullOrWhiteSpace(url) ||
                  !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                  (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            SetFieldError(UrlBox, UrlErrorText, "请输入以 http:// 或 https:// 开头的完整 URL。");
            valid = false;
        }

        var envValid = TryReadMap(EnvBox.Text, out var env, out var envError);
        var headersValid = TryReadMap(HeadersBox.Text, out var headers, out var headersError);
        if (transport == McpTransportType.Stdio && !envValid)
        {
            SetFieldError(EnvBox, EnvErrorText, envError);
            valid = false;
        }
        if (transport == McpTransportType.Http && !headersValid)
        {
            SetFieldError(HeadersBox, HeadersErrorText, headersError);
            valid = false;
        }

        server = new McpServerConfig
        {
            Id = normalizedId,
            Name = string.IsNullOrWhiteSpace(ServerNameBox.Text) ? normalizedId : ServerNameBox.Text.Trim(),
            Enabled = EnabledBox.IsChecked == true,
            Transport = transport,
            Command = transport == McpTransportType.Stdio ? command : null,
            Args = transport == McpTransportType.Stdio ? ReadLines(ArgsBox.Text) : [],
            WorkingDirectory = transport == McpTransportType.Stdio && !string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text)
                ? WorkingDirectoryBox.Text.Trim()
                : null,
            Env = transport == McpTransportType.Stdio ? env : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Url = transport == McpTransportType.Http ? url : null,
            Headers = transport == McpTransportType.Http ? headers : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        if (!valid)
        {
            SetStatus("请修正标出的字段后重试。", true);
        }
        return valid;
    }

    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (!_isDirty)
        {
            return true;
        }

        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }

        var discard = await DialogService.ConfirmAsync(
            owner,
            "放弃 MCP 更改",
            "当前 MCP 表单还有未保存内容。确定放弃这些更改吗？",
            "放弃更改");
        if (discard)
        {
            DiscardUnsavedChanges();
        }
        return discard;
    }

    private void UpdateTransportFields()
    {
        var transport = TransportBox.SelectedItem is ComboBoxItem { Tag: McpTransportType value }
            ? value
            : McpTransportType.Stdio;
        StdioFieldsPanel.IsVisible = transport == McpTransportType.Stdio;
        HttpFieldsPanel.IsVisible = transport == McpTransportType.Http;
        EnvFieldsPanel.IsVisible = transport == McpTransportType.Stdio;
        HeaderFieldsPanel.IsVisible = transport == McpTransportType.Http;
    }

    private void SelectTransport(McpTransportType transport)
    {
        var normalized = transport == McpTransportType.Sse ? McpTransportType.Http : transport;
        TransportBox.SelectedItem = TransportBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is McpTransportType value && value == normalized);
        if (TransportBox.SelectedItem is null)
        {
            TransportBox.SelectedIndex = 0;
        }
    }

    private void SelectListItem(string? id)
    {
        ServerListBox.SelectedItem = string.IsNullOrWhiteSpace(id)
            ? null
            : ServerListBox.Items
                .OfType<ListBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, id, StringComparison.OrdinalIgnoreCase));
    }

    private void MarkDirty()
    {
        if (_isLoadingForm || _baselineSnapshot is null)
        {
            return;
        }

        SetDirty(_baselineSnapshot != CreateSnapshot());
    }

    private void CaptureBaseline()
    {
        _baselineSnapshot = CreateSnapshot();
        SetDirty(false);
    }

    private McpFormSnapshot CreateSnapshot()
    {
        var transport = TransportBox.SelectedItem is ComboBoxItem { Tag: McpTransportType value }
            ? value
            : McpTransportType.Stdio;
        return new McpFormSnapshot(
            (_selectedId ?? string.Empty).Trim().ToLowerInvariant(),
            EnabledBox.IsChecked == true,
            transport,
            (ServerIdBox.Text ?? string.Empty).Trim().ToLowerInvariant(),
            ServerNameBox.Text?.Trim() ?? string.Empty,
            transport == McpTransportType.Stdio ? CommandBox.Text?.Trim() ?? string.Empty : string.Empty,
            transport == McpTransportType.Stdio ? NormalizeLines(ArgsBox.Text, sort: false) : string.Empty,
            transport == McpTransportType.Stdio ? WorkingDirectoryBox.Text?.Trim() ?? string.Empty : string.Empty,
            transport == McpTransportType.Stdio ? NormalizeLines(EnvBox.Text, sort: true) : string.Empty,
            transport == McpTransportType.Http ? UrlBox.Text?.Trim() ?? string.Empty : string.Empty,
            transport == McpTransportType.Http ? NormalizeLines(HeadersBox.Text, sort: true) : string.Empty);
    }

    private static string NormalizeLines(string? text, bool sort)
    {
        IEnumerable<string> lines = (text ?? string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (sort)
        {
            lines = lines.OrderBy(line => line, StringComparer.OrdinalIgnoreCase);
        }
        return string.Join("\n", lines);
    }

    private void SetDirty(bool isDirty)
    {
        _isDirty = isDirty;
        DirtyText.IsVisible = isDirty;
        CancelChangesButton.IsEnabled = isDirty && !_isBusy;
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        _isBusy = isBusy;
        BusyProgress.IsVisible = isBusy;
        SaveButton.IsEnabled = !isBusy;
        TestButton.IsEnabled = !isBusy;
        DeleteButton.IsEnabled = !isBusy && !string.IsNullOrWhiteSpace(_selectedId);
        CancelChangesButton.IsEnabled = !isBusy && _isDirty;
        AddButton.IsEnabled = !isBusy;
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
        EditFormPanel.IsVisible = false;
    }

    private void ShowEditForm()
    {
        EmptyHintPanel.IsVisible = false;
        EditFormPanel.IsVisible = true;
    }

    internal void UpdateResponsiveLayout(double width)
    {
        var narrow = width < 720;
        if (narrow)
        {
            McpLayoutGrid.ColumnDefinitions = new ColumnDefinitions("*");
            McpLayoutGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
            ServerListPane.Height = 252;
            ServerListPane.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(ServerListPane, 0);
            Grid.SetRow(ServerListPane, 0);
            Grid.SetColumn(ServerEditorPane, 0);
            Grid.SetRow(ServerEditorPane, 1);
            ServerEditorPane.Margin = new Thickness(0, 12, 0, 0);
            ServerListBox.MaxHeight = double.PositiveInfinity;
        }
        else
        {
            McpLayoutGrid.ColumnDefinitions = new ColumnDefinitions("288,*");
            McpLayoutGrid.RowDefinitions = new RowDefinitions("*");
            ServerListPane.Height = double.NaN;
            ServerListPane.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetColumn(ServerListPane, 0);
            Grid.SetRow(ServerListPane, 0);
            Grid.SetColumn(ServerEditorPane, 1);
            Grid.SetRow(ServerEditorPane, 0);
            ServerEditorPane.Margin = new Thickness(12, 0, 0, 0);
            ServerListBox.MaxHeight = double.PositiveInfinity;
        }
    }

    private static Border MakeBadge(string text, string background, string foreground)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2),
            Background = AemiUi.Brush(background),
            BorderBrush = AemiUi.Brush(AemiUi.Border),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = AemiUi.Brush(foreground)
            }
        };
    }

    private static (string Text, string Background, string Foreground) BuildStatusVisual(McpServerConfig server)
    {
        if (!server.Enabled)
        {
            return ("已停用", AemiUi.HaloSoft, AemiUi.TextMuted);
        }
        if (string.Equals(server.LastStatus, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return ("正常", AemiUi.SuccessSurface, AemiUi.Success);
        }
        if (!string.IsNullOrWhiteSpace(server.LastError) && server.LastError.Contains("超时", StringComparison.OrdinalIgnoreCase))
        {
            return ("超时", AemiUi.WarningSurface, AemiUi.Warning);
        }
        if (!string.IsNullOrWhiteSpace(server.LastError))
        {
            return ("失败", AemiUi.ErrorSurface, AemiUi.Error);
        }
        return ("未加载", AemiUi.HaloSoft, AemiUi.TextSecondary);
    }

    private static void SetFieldError(Control field, TextBlock errorText, string message)
    {
        if (!field.Classes.Contains("invalid"))
        {
            field.Classes.Add("invalid");
        }
        errorText.Text = message;
        errorText.IsVisible = true;
        AutomationProperties.SetHelpText(field, message);
    }

    private void ClearValidation()
    {
        ClearFieldError(ServerIdBox, ServerIdErrorText);
        ClearFieldError(CommandBox, CommandErrorText);
        ClearFieldError(UrlBox, UrlErrorText);
        ClearFieldError(EnvBox, EnvErrorText);
        ClearFieldError(HeadersBox, HeadersErrorText);
    }

    private static void ClearFieldError(Control field, TextBlock errorText)
    {
        field.Classes.Remove("invalid");
        errorText.Text = string.Empty;
        errorText.IsVisible = false;
        AutomationProperties.SetHelpText(field, string.Empty);
    }

    private static List<string> ReadLines(string? text)
    {
        return (text ?? string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static bool TryReadMap(string? text, out Dictionary<string, string> map, out string error)
    {
        map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;
        var lines = ReadLines(text);
        for (var index = 0; index < lines.Count; index++)
        {
            var separator = lines[index].IndexOf('=');
            if (separator <= 0)
            {
                error = $"第 {index + 1} 行必须使用 KEY=VALUE 格式。";
                return false;
            }

            var key = lines[index][..separator].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                error = $"第 {index + 1} 行的键不能为空。";
                return false;
            }
            map[key] = lines[index][(separator + 1)..].Trim();
        }
        return true;
    }

    private static string FormatMap(IReadOnlyDictionary<string, string> map)
    {
        var builder = new StringBuilder();
        foreach (var pair in map)
        {
            builder.Append(pair.Key).Append('=').AppendLine(pair.Value);
        }
        return builder.ToString().TrimEnd();
    }
}
