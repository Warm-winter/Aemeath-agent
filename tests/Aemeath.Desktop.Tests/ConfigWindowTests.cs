using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Aemeath.Core.Configuration;
using Aemeath.Core.MCP;
using Aemeath.Desktop.Views;

namespace Aemeath.Desktop.Tests;

public sealed class ConfigWindowTests
{
    [AvaloniaFact]
    public async Task InitializationAndUnmodifiedNavigation_DoNotRequestUnsavedDecision()
    {
        using var temp = new TemporaryDirectory();
        var (window, _) = CreateWindow(temp.Path);
        var promptCount = 0;
        window.UnsavedChangesHandler = (_, _, _, _, _) =>
        {
            promptCount++;
            return Task.FromResult(UnsavedChangesDecision.Cancel);
        };
        window.Show();
        try
        {
            Assert.False(window.HasUnsavedChanges(SettingsPageId.Provider));
            Assert.False(window.HasUnsavedChanges(SettingsPageId.ComputerControl));

            var changed = await window.TryChangeSettingsPageAsync(SettingsPageId.Memory);

            Assert.True(changed);
            Assert.Equal(0, promptCount);
            Assert.Equal(SettingsPageId.Memory, window.CurrentPageId);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PageChange_CancelDiscardAndFailedSave_RespectCurrentPageAndDirtyState()
    {
        using var temp = new TemporaryDirectory();
        var (window, _) = CreateWindow(temp.Path);
        window.Show();
        try
        {
            var providerName = window.FindControl<TextBox>("ProviderNameBox")!;
            var original = providerName.Text;
            providerName.Focus();
            providerName.SelectAll();
            window.KeyTextInput("changed-provider");
            Assert.True(window.HasUnsavedChanges(SettingsPageId.Provider));

            window.UnsavedChangesHandler = (_, _, _, _, _) => Task.FromResult(UnsavedChangesDecision.Cancel);
            Assert.False(await window.TryChangeSettingsPageAsync(SettingsPageId.Memory));
            Assert.Equal(SettingsPageId.Provider, window.CurrentPageId);
            Assert.True(window.HasUnsavedChanges(SettingsPageId.Provider));

            window.UnsavedChangesHandler = (_, _, _, _, _) => Task.FromResult(UnsavedChangesDecision.Discard);
            Assert.True(await window.TryChangeSettingsPageAsync(SettingsPageId.Memory));
            Assert.Equal(SettingsPageId.Memory, window.CurrentPageId);
            Assert.False(window.HasUnsavedChanges(SettingsPageId.Provider));
            Assert.Equal(original, providerName.Text);

            Assert.True(await window.TryChangeSettingsPageAsync(SettingsPageId.Provider));
            var endpoint = window.FindControl<TextBox>("EndpointBox")!;
            endpoint.Focus();
            endpoint.SelectAll();
            window.KeyTextInput("not-a-url");
            window.UnsavedChangesHandler = (_, _, _, _, _) => Task.FromResult(UnsavedChangesDecision.Save);
            Assert.False(await window.TryChangeSettingsPageAsync(SettingsPageId.Memory));
            Assert.Equal(SettingsPageId.Provider, window.CurrentPageId);
            Assert.True(window.HasUnsavedChanges(SettingsPageId.Provider));
        }
        finally
        {
            window.UnsavedChangesHandler = (_, _, _, _, _) => Task.FromResult(UnsavedChangesDecision.Discard);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CloseWindow_SaveDecision_PersistsDirtyProviderBeforeClosing()
    {
        using var temp = new TemporaryDirectory();
        var (window, settings) = CreateWindow(temp.Path);
        window.UnsavedChangesHandler = (_, _, _, _, _) => Task.FromResult(UnsavedChangesDecision.Save);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();

        var endpoint = window.FindControl<TextBox>("EndpointBox")!;
        endpoint.Focus();
        endpoint.SelectAll();
        window.KeyTextInput("https://example.test/v1");
        Assert.True(window.HasUnsavedChanges(SettingsPageId.Provider));

        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("https://example.test/v1", settings.GetApiKeyInfo("openai")?.Endpoint);
    }

    [AvaloniaFact]
    public void NavigationGroups_AreIndependentDisabledHeadings()
    {
        using var temp = new TemporaryDirectory();
        var (window, _) = CreateWindow(temp.Path);
        window.Show();
        try
        {
            foreach (var name in new[] { "ConnectionGroupItem", "CapabilityGroupItem", "PersonalizationGroupItem" })
            {
                var item = window.FindControl<ListBoxItem>(name)!;
                Assert.False(item.IsEnabled);
                Assert.False(item.Focusable);
                Assert.False(item.IsHitTestVisible);
                Assert.Contains("group-heading", item.Classes);
            }

            var providerItem = window.FindControl<ListBoxItem>("ProviderNavigationItem")!;
            var providerLabel = Assert.IsType<TextBlock>(providerItem.Content);
            Assert.Equal("AI \u670d\u52a1", providerLabel.Text);
        }
        finally
        {
            window.Close();
        }
    }

    private static (ConfigWindow Window, SettingsService Settings) CreateWindow(string root)
    {
        var settings = new SettingsService(Path.Combine(root, "settings.json"));
        var store = new McpServerStore(Path.Combine(root, "app-data"));
        var window = new ConfigWindow(settings, new NoOpChatService(), null, store);
        return (window, settings);
    }
}
