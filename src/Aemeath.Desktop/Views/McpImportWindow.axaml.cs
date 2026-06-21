using Avalonia.Controls;
using Avalonia.Interactivity;
using System;

namespace Aemeath.Desktop.Views;

/// <summary>
/// MCP JSON 导入弹窗：粘贴标准 mcpServers 配置后确认导入。
/// 导入实际逻辑由父面板通过 ImportRequested 事件处理。
/// </summary>
public partial class McpImportWindow : Window
{
    /// <summary>用户点「导入」时触发，参数为粘贴的 JSON 文本。</summary>
    public event EventHandler<string>? ImportRequested;

    public McpImportWindow()
    {
        InitializeComponent();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        var json = JsonBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            SetError("请粘贴 JSON 配置。");
            return;
        }
        ImportRequested?.Invoke(this, json);
    }

    /// <summary>由父面板在导入失败时调用，显示错误并保持窗口打开。</summary>
    public void SetError(string message)
    {
        ErrorText.Text = message;
    }
}
