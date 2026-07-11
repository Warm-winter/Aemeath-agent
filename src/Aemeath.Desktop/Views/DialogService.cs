using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Aemeath.Desktop.Services;

namespace Aemeath.Desktop.Views;

internal static class DialogService
{
    public static async Task<bool> ConfirmAsync(
        Window owner,
        string title,
        string message,
        string confirmText = "确认",
        bool destructive = true)
    {
        var dialog = CreateDialog(owner, title, 440, 260);
        var result = false;

        var cancelButton = AemiUi.Button("取消", "ghost", 92);
        var confirmButton = AemiUi.Button(confirmText, destructive ? "danger" : "primary", 104);
        AutomationProperties.SetName(cancelButton, "取消并关闭对话框");
        AutomationProperties.SetName(confirmButton, destructive ? $"确认危险操作：{confirmText}" : confirmText);

        cancelButton.Click += (_, _) => dialog.Close();
        confirmButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };

        SetDialogContent(dialog, BuildDialogContent(
            destructive ? "危险操作" : "确认操作",
            title,
            message,
            cancelButton,
            confirmButton,
            destructive));

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                dialog.Close();
            }
        };
        dialog.Opened += (_, _) => cancelButton.Focus();

        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task<UnsavedChangesDecision> ChooseUnsavedChangesAsync(
        Window owner,
        string title,
        string message,
        string discardText,
        string saveText)
    {
        var dialog = CreateDialog(owner, title, 520, 280);
        var result = UnsavedChangesDecision.Cancel;

        var cancelButton = AemiUi.Button("取消", "ghost", 88);
        var discardButton = AemiUi.Button(discardText, "danger", 126);
        var saveButton = AemiUi.Button(saveText, "primary", 126);
        AutomationProperties.SetName(cancelButton, "取消并留在当前页面");
        AutomationProperties.SetName(discardButton, discardText);
        AutomationProperties.SetName(saveButton, saveText);

        cancelButton.Click += (_, _) => dialog.Close();
        discardButton.Click += (_, _) =>
        {
            result = UnsavedChangesDecision.Discard;
            dialog.Close();
        };
        saveButton.Click += (_, _) =>
        {
            result = UnsavedChangesDecision.Save;
            dialog.Close();
        };

        var content = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                AemiUi.Badge("未保存更改", "danger"),
                new TextBlock
                {
                    Text = title,
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = AemiUi.Brush(AemiUi.Ghost)
                },
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = AemiUi.Brush(AemiUi.TextSecondary)
                },
                CreateButtonRow(cancelButton, discardButton, saveButton)
            }
        };
        SetDialogContent(dialog, AemiUi.Surface(content, radius: 18, padding: 20), showCloseButton: false);

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                dialog.Close();
            }
        };
        dialog.Opened += (_, _) => cancelButton.Focus();

        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task<string?> PromptAsync(
        Window owner,
        string title,
        string message,
        string initialValue,
        string confirmText = "保存")
    {
        var dialog = CreateDialog(owner, title, 460, 280);
        string? result = null;
        var input = new TextBox
        {
            Text = initialValue,
            MinWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(input, title);
        AutomationProperties.SetHelpText(input, message);

        var errorText = new TextBlock
        {
            Foreground = AemiUi.Brush(AemiUi.Error),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        AutomationProperties.SetLiveSetting(errorText, AutomationLiveSetting.Assertive);

        var cancelButton = AemiUi.Button("取消", "ghost", 92);
        var confirmButton = AemiUi.Button(confirmText, "primary", 104);

        void Submit()
        {
            var value = input.Text?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                errorText.Text = "内容不能为空。";
                errorText.IsVisible = true;
                input.Focus();
                return;
            }

            result = value;
            dialog.Close();
        }

        cancelButton.Click += (_, _) => dialog.Close();
        confirmButton.Click += (_, _) => Submit();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Submit();
            }
        };
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                dialog.Close();
            }
        };

        var body = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                AemiUi.Badge("编辑内容", "halo"),
                new TextBlock
                {
                    Text = title,
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = AemiUi.Brush(AemiUi.Ghost)
                },
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = AemiUi.Brush(AemiUi.TextSecondary)
                },
                input,
                errorText,
                CreateButtonRow(cancelButton, confirmButton)
            }
        };
        SetDialogContent(dialog, AemiUi.Surface(body, radius: 18, padding: 20));
        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    private static Window CreateDialog(Window owner, string title, double width, double height)
    {
        return new Window
        {
            Title = title,
            Width = width,
            Height = height + 46,
            MinWidth = Math.Min(width, 360),
            MinHeight = Math.Min(height + 46, 266),
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = AemiUi.Brush(AemiUi.Void),
            WindowDecorations = WindowDecorations.BorderOnly,
            Icon = owner.Icon
        };
    }

    private static void SetDialogContent(Window dialog, Control content, bool showCloseButton = true)
    {
        var body = new Border
        {
            Margin = new Thickness(16),
            Child = content
        };
        Grid.SetRow(body, 1);

        dialog.Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                BuildDialogTitleBar(dialog.Title ?? string.Empty, showCloseButton),
                body
            }
        };
    }

    internal static AemeathTitleBar BuildDialogTitleBar(string title, bool showCloseButton = true)
    {
        return new AemeathTitleBar
        {
            Title = title,
            ShowMinimizeButton = false,
            ShowMaximizeButton = false,
            ShowCloseButton = showCloseButton
        };
    }

    private static Control BuildDialogContent(
        string badgeText,
        string title,
        string message,
        Button cancelButton,
        Button confirmButton,
        bool destructive)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = AemiUi.Brush(AemiUi.Ghost)
        };
        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AemiUi.Brush(AemiUi.TextSecondary)
        };
        AutomationProperties.SetLiveSetting(messageBlock, AutomationLiveSetting.Polite);

        var content = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                AemiUi.Badge(badgeText, destructive ? "danger" : "halo"),
                titleBlock,
                messageBlock,
                CreateButtonRow(cancelButton, confirmButton)
            }
        };

        return AemiUi.Surface(content, radius: 18, padding: 20);
    }

    private static StackPanel CreateButtonRow(params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }
        return row;
    }
}
