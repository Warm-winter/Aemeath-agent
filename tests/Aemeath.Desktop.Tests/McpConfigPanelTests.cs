using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Aemeath.Core.MCP;
using Aemeath.Desktop.Views;

namespace Aemeath.Desktop.Tests;

public sealed class McpConfigPanelTests
{
    [AvaloniaFact]
    public void EditField_ExistingServer_SetsDirtyAndDiscardRestoresSavedValue()
    {
        using var temp = new TemporaryDirectory();
        var store = CreateStoreWithServer(temp.Path);
        var panel = new McpConfigPanel(store, null);
        var owner = new Window { Content = panel, Width = 900, Height = 700 };
        owner.Show();
        try
        {
            panel.RefreshServerList("demo");
            var idBox = panel.FindControl<TextBox>("ServerIdBox");
            Assert.NotNull(idBox);
            idBox.Focus();
            idBox.SelectAll();
            owner.KeyTextInput("changed");

            Assert.True(panel.HasUnsavedChanges);
            panel.DiscardUnsavedChanges();
            Assert.False(panel.HasUnsavedChanges);
            Assert.Equal("demo", idBox.Text);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void EditThenRestoreField_RecomputesDirtyStateFromSavedSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var panel = new McpConfigPanel(CreateStoreWithServer(temp.Path), null);
        var owner = new Window { Content = panel, Width = 900, Height = 700 };
        owner.Show();
        try
        {
            panel.RefreshServerList("demo");
            var idBox = panel.FindControl<TextBox>("ServerIdBox")!;
            idBox.Focus();
            idBox.SelectAll();
            owner.KeyTextInput("changed");
            Assert.True(panel.HasUnsavedChanges);

            idBox.SelectAll();
            owner.KeyTextInput("demo");
            Assert.False(panel.HasUnsavedChanges);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void TransportSelection_Http_ShowsOnlyRelevantFieldsAndHasAccessibleListName()
    {
        using var temp = new TemporaryDirectory();
        var panel = new McpConfigPanel(CreateStoreWithServer(temp.Path), null);
        panel.RefreshServerList("demo");
        var transport = panel.FindControl<ComboBox>("TransportBox");
        Assert.NotNull(transport);
        transport.SelectedItem = transport.Items
            .OfType<ComboBoxItem>()
            .Single(item => item.Tag is McpTransportType.Http);

        var stdioPanel = panel.FindControl<Border>("StdioFieldsPanel");
        Assert.NotNull(stdioPanel);
        Assert.False(stdioPanel.IsVisible);
        var httpPanel = panel.FindControl<Border>("HttpFieldsPanel");
        Assert.NotNull(httpPanel);
        Assert.True(httpPanel.IsVisible);
        var list = panel.FindControl<ListBox>("ServerListBox");
        Assert.NotNull(list);
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(list)));
    }

    [AvaloniaFact]
    public void ResponsiveLayout_NarrowWidth_StacksListAboveEditor()
    {
        using var temp = new TemporaryDirectory();
        var panel = new McpConfigPanel(CreateStoreWithServer(temp.Path), null);
        var editor = panel.FindControl<Border>("ServerEditorPane");
        Assert.NotNull(editor);

        panel.UpdateResponsiveLayout(640);

        Assert.Equal(1, Grid.GetRow(editor));
        Assert.Equal(0, Grid.GetColumn(editor));
        var listPane = panel.FindControl<Border>("ServerListPane")!;
        Assert.Equal(252, listPane.Height);

        panel.UpdateResponsiveLayout(900);
        var layout = panel.FindControl<Grid>("McpLayoutGrid")!;
        Assert.True(layout.RowDefinitions[0].Height.IsStar);
        Assert.True(double.IsNaN(listPane.Height));
    }

    [AvaloniaFact]
    public async Task DeleteButton_ConfirmationDeniedThenAccepted_ProtectsDangerousDelete()
    {
        using var temp = new TemporaryDirectory();
        var store = CreateStoreWithServer(temp.Path);
        var panel = new McpConfigPanel(store, null);
        var owner = new Window { Content = panel, Width = 900, Height = 700 };
        owner.Show();
        try
        {
            panel.RefreshServerList("demo");
            var confirmationCount = 0;
            panel.ConfirmationHandler = (_, _, _, _) =>
            {
                confirmationCount++;
                return Task.FromResult(false);
            };

            await panel.DeleteCurrentServerAsync(owner);

            Assert.Equal(1, confirmationCount);
            Assert.NotNull(store.GetServer("demo"));

            panel.ConfirmationHandler = (_, _, _, _) =>
            {
                confirmationCount++;
                return Task.FromResult(true);
            };
            await panel.DeleteCurrentServerAsync(owner);

            Assert.Equal(2, confirmationCount);
            Assert.Null(store.GetServer("demo"));
        }
        finally
        {
            owner.Close();
        }
    }

    private static McpServerStore CreateStoreWithServer(string root)
    {
        var store = new McpServerStore(root);
        store.SaveServer(new McpServerConfig
        {
            Id = "demo",
            Name = "Demo",
            Enabled = true,
            Transport = McpTransportType.Stdio,
            Command = "demo.exe"
        });
        return store;
    }
}
