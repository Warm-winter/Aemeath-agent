using Avalonia.Controls;
using Aemeath.Core.AI;
using Aemeath.Core.MCP;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Aemeath.Desktop.Views;

public partial class McpConfigWindow : Window
{
    private readonly McpServerStore _store;
    private readonly McpRuntimeService _runtime;
    private readonly Action? _reloadChatService;
    private string? _selectedId;

    public McpConfigWindow() : this(new McpServerStore(), null)
    {
    }

    public McpConfigWindow(McpServerStore store, Action? reloadChatService)
    {
        InitializeComponent();
        _store = store;
        _runtime = new McpRuntimeService(_store);
        _reloadChatService = reloadChatService;

        TransportBox.Items.Add(new ComboBoxItem { Content = "stdio", Tag = McpTransportType.Stdio });
        TransportBox.Items.Add(new ComboBoxItem { Content = "sse", Tag = McpTransportType.Sse });
        TransportBox.Items.Add(new ComboBoxItem { Content = "http", Tag = McpTransportType.Http });
        TransportBox.SelectedIndex = 0;

        CloseButton.Click += (_, _) => Close();
        NewServerButton.Click += (_, _) => StartNewServer();
        SaveButton.Click += (_, _) => SaveCurrentServer();
        DeleteButton.Click += (_, _) => DeleteCurrentServer();
        TestButton.Click += async (_, _) => await TestCurrentServerAsync();
        ImportJsonButton.Click += (_, _) => ImportJson();
        ServerListBox.SelectionChanged += (_, _) => LoadSelectedServer();

        RefreshServerList();
        StartNewServer();
    }

    private void RefreshServerList(string? selectId = null)
    {
        ServerListBox.Items.Clear();
        foreach (var server in _store.ListServers())
        {
            var state = server.Enabled ? "启用" : "关闭";
            ServerListBox.Items.Add(new ListBoxItem
            {
                Content = $"{server.DisplayName}  [{server.Transport.ToString().ToLowerInvariant()} / {state}]",
                Tag = server.Id
            });
        }

        if (!string.IsNullOrWhiteSpace(selectId))
        {
            foreach (var item in ServerListBox.Items.OfType<ListBoxItem>())
            {
                if (string.Equals(item.Tag as string, selectId, StringComparison.OrdinalIgnoreCase))
                {
                    ServerListBox.SelectedItem = item;
                    break;
                }
            }
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
        StatusText.Text = "正在创建新的 MCP 服务。";
    }

    private void LoadSelectedServer()
    {
        if (ServerListBox.SelectedItem is not ListBoxItem { Tag: string id })
        {
            return;
        }

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
            ? server.LastStatus ?? "已加载服务。"
            : $"{server.LastStatus}: {server.LastError}";
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
                    StatusText.Text = $"保存失败：ID '{server.Id}' 已被其他服务占用。";
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
            StatusText.Text = "MCP 服务已保存。";
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
            StatusText.Text = "正在测试 MCP 连接...";
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _runtime.DisposeAsync();
                }
                catch
                {
                }
            });
        }
    }
}
