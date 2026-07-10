using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Aemeath.Desktop.Views;

public partial class McpImportWindow : Window
{
    public event EventHandler<string>? ImportRequested;

    public McpImportWindow()
    {
        InitializeComponent();
        Opened += (_, _) => JsonBox.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        var json = JsonBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            SetError("请粘贴 JSON 配置。");
            JsonBox.Focus();
            return;
        }

        ErrorText.Text = string.Empty;
        ImportRequested?.Invoke(this, json);
    }

    public void SetError(string message)
    {
        ErrorText.Text = message;
    }
}
