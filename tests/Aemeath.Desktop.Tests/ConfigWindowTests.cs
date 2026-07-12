using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Aemeath.Core.Configuration;
using Aemeath.Core.MCP;
using Aemeath.Desktop.Views;

namespace Aemeath.Desktop.Tests;

public sealed class ConfigWindowTests
{
    [AvaloniaFact]
    public void FirstShowAndFirstSelection_KeepNavigationAndContentSynchronized()
    {
        using var temp = new TemporaryDirectory();
        var (window, _) = CreateWindow(temp.Path);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var navigation = window.FindControl<ListBox>("SettingsTabControl")!;
            var contentHost = window.FindControl<TransitioningContentControl>("SettingsContentHost")!;
            var provider = window.FindControl<ListBoxItem>("ProviderNavigationItem")!;
            var memory = window.FindControl<ListBoxItem>("MemoryNavigationItem")!;

            Assert.Same(provider, navigation.SelectedItem);
            Assert.Same(provider.Tag, contentHost.Content);
            Assert.Equal(SettingsPageId.Provider, window.CurrentPageId);

            navigation.SelectedItem = memory;
            Dispatcher.UIThread.RunJobs();

            Assert.Same(memory, navigation.SelectedItem);
            Assert.Same(memory.Tag, contentHost.Content);
            Assert.Equal(SettingsPageId.Memory, window.CurrentPageId);
        }
        finally
        {
            window.Close();
        }
    }

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


    [AvaloniaFact]
    public void ProviderPage_WideLayout_FillsAvailableHeightWithoutCenteredListGap()
    {
        using var temp = new TemporaryDirectory();
        var (window, _) = CreateWindow(temp.Path);
        window.Width = 1100;
        window.Height = 1000;
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var layout = window.FindControl<Grid>("ProviderLayoutGrid")!;
            var listPane = window.FindControl<Border>("ProviderListPane")!;
            var editorPane = window.FindControl<Border>("ProviderEditorPane")!;
            var cards = window.FindControl<ScrollViewer>("ProviderCardsScrollViewer")!;

            Assert.Single(layout.RowDefinitions);
            Assert.True(layout.RowDefinitions[0].Height.IsStar);
            Assert.Equal(layout.Bounds.Height, listPane.Bounds.Height, precision: 1);
            Assert.Equal(layout.Bounds.Height, editorPane.Bounds.Height, precision: 1);
            Assert.True(double.IsPositiveInfinity(cards.MaxHeight));

            var cardsOrigin = cards.TranslatePoint(default, listPane)!.Value;
            var bottomGap = listPane.Bounds.Height - (cardsOrigin.Y + cards.Bounds.Height);
            Assert.InRange(bottomGap, 13, 16);

            window.Width = 900;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, layout.RowDefinitions.Count);
            Assert.All(layout.RowDefinitions, row => Assert.True(row.Height.IsAuto));
            Assert.Equal(220, cards.MaxHeight);
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
