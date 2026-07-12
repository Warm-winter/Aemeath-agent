using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Aemeath.Core.AI;
using Aemeath.Core.Configuration;
using Aemeath.Desktop.Services;
using Aemeath.Desktop.Views;
using System.Runtime.CompilerServices;

namespace Aemeath.Desktop.Tests;

public sealed class EmptyReplyTests
{
    [AvaloniaFact]
    public async Task EmptyStreamingReply_IsReportedAsFailureAndNotPersisted()
    {
        using var temp = new TemporaryDirectory();
        var settings = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        settings.UpdateApiKey("openai", "test-key", "https://example.invalid/v1", "test-model");
        var sessions = new ChatSessionStore(Path.Combine(temp.Path, "sessions.json"));
        var window = new ChatWindow(
            new EmptyStreamingChatService(),
            settings,
            sessions,
            new AttachmentThumbnailCache());
        var terminalActivity = new TaskCompletionSource<ChatActivityKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ActivityChanged += (_, args) =>
        {
            if (args.Kind is ChatActivityKind.Completed or ChatActivityKind.Failed)
            {
                terminalActivity.TrySetResult(args.Kind);
            }
        };

        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.FindControl<TextBox>("InputBox")!.Text = "hello";
            window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var activity = await terminalActivity.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ChatActivityKind.Failed, activity);
            var session = sessions.GetSession(window.CurrentSessionId)!;
            Assert.Single(session.Messages);
            Assert.Equal("user", session.Messages[0].Role);
            Assert.DoesNotContain(session.Messages, message => message.Content.Contains("\u65e0\u56de\u590d", StringComparison.Ordinal));
            Assert.Contains(
                "\u672a\u6536\u5230\u6709\u6548\u56de\u590d",
                window.FindControl<TextBlock>("ProviderSwitchStatusText")!.Text,
                StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class EmptyStreamingChatService : IChatService
    {
        public string CurrentAssistantName => "test";
        public bool IsProcessing => false;
        public Task<string> SendMessageAsync(string message, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
        public Task<string> SendMessageAsync(string message, IReadOnlyList<ChatAttachment>? attachments, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
        public IAsyncEnumerable<string> SendMessageStreamingAsync(string message, CancellationToken cancellationToken = default)
            => SendMessageStreamingAsync(message, null, cancellationToken);
        public async IAsyncEnumerable<string> SendMessageStreamingAsync(
            string message,
            IReadOnlyList<ChatAttachment>? attachments,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public void ClearHistory() { }
        public Task<bool> SwitchProviderAsync(string providerName, string apiKey, string? endpoint = null)
            => Task.FromResult(true);
        public void RegisterTool(string toolName, string description, Func<string, Task<string>> handler) { }
    }
}
