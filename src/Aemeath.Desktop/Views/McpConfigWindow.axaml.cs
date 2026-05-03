using Avalonia.Controls;

namespace Aemeath.Desktop.Views;

public partial class McpConfigWindow : Window
{
    public McpConfigWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
    }
}
