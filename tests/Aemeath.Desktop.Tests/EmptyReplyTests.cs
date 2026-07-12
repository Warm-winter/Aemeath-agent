using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aemeath.Core.AI;
using Aemeath.Core.Configuration;
using Aemeath.Desktop.Services;
using Aemeath.Desktop.Views;
using System.Runtime.CompilerServices;

namespace Aemeath.Desktop.Tests;

public sealed class EmptyReplyTests
{
    [AvaloniaFact]
    public async Task EmptyStreamingReply_IsTransientActionableFailureAndNotPersisted()
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

            var messages = window.FindControl<StackPanel>("MessagesPanel")!;
            var failureRoot = Assert.IsType<StackPanel>(messages.Children[^1]);
            var actions = failureRoot.GetVisualDescendants()
                .OfType<WrapPanel>()
                .Single(panel => panel.Classes.Contains("message-actions"));
            var actionNames = actions.Children
                .OfType<Button>()
                .Select(AutomationProperties.GetName)
                .ToArray();
            Assert.Equal(
                new[] { "\u590d\u5236", "\u91cd\u8bd5", "\u5220\u9664" },
                actionNames.Select(name => name ?? string.Empty).ToArray());

            var delete = actions.Children
                .OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "\u5220\u9664");
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Single(messages.Children);
            session = sessions.GetSession(window.CurrentSessionId)!;
            Assert.Single(session.Messages);
            Assert.Equal("user", session.Messages[0].Role);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TransientFailure_RetryUsesOriginalAttachmentsAndPersistsSuccessfulReply()
    {
        using var temp = new TemporaryDirectory();
        var attachmentPath = Path.Combine(temp.Path, "note.txt");
        await File.WriteAllTextAsync(attachmentPath, "attachment body", TestContext.Current.CancellationToken);
        var attachment = new ChatAttachment(
            attachmentPath,
            "note.txt",
            "text/plain",
            ChatAttachmentKind.Text,
            new FileInfo(attachmentPath).Length);
        var settings = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        settings.UpdateApiKey("openai", "test-key", "https://example.invalid/v1", "test-model");
        var sessions = new ChatSessionStore(Path.Combine(temp.Path, "sessions.json"));
        var session = sessions.CreateSession("retry attachments");
        sessions.AppendMessage(session.Id, "user", "inspect this", [attachment]);
        sessions.AppendMessage(session.Id, "assistant", "old reply");
        var service = new SequenceStreamingChatService(null, "recovered reply");
        var window = new ChatWindow(service, settings, sessions, new AttachmentThumbnailCache());
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ActivityChanged += (_, args) =>
        {
            if (args.Kind == ChatActivityKind.Failed)
            {
                failed.TrySetResult();
            }
            else if (args.Kind == ChatActivityKind.Completed)
            {
                completed.TrySetResult();
            }
        };

        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var messages = window.FindControl<StackPanel>("MessagesPanel")!;
            var oldAssistantRoot = Assert.IsType<StackPanel>(messages.Children[1]);
            var regenerate = oldAssistantRoot.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "\u91cd\u65b0\u56de\u7b54");
            regenerate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Dispatcher.UIThread.RunJobs();
            var failureRoot = Assert.IsType<StackPanel>(messages.Children[^1]);
            var retry = failureRoot.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "\u91cd\u8bd5");
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, service.AttachmentsByCall.Count);
            Assert.All(service.AttachmentsByCall, attachments =>
            {
                var sent = Assert.Single(attachments);
                Assert.Equal(attachmentPath, sent.Path);
            });
            var stored = sessions.GetSession(session.Id)!;
            Assert.Equal(2, stored.Messages.Count);
            Assert.Equal("user", stored.Messages[0].Role);
            Assert.Equal("assistant", stored.Messages[1].Role);
            Assert.Equal("recovered reply", stored.Messages[1].Content);
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

    private sealed class SequenceStreamingChatService(params string?[] responses) : IChatService
    {
        private readonly Queue<string?> _responses = new(responses);
        public List<IReadOnlyList<ChatAttachment>> AttachmentsByCall { get; } = [];
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
            AttachmentsByCall.Add(attachments?.Select(item => item with { }).ToList() ?? []);
            var response = _responses.Count > 0 ? _responses.Dequeue() : null;
            await Task.CompletedTask;
            if (response is not null)
            {
                yield return response;
            }
        }
        public void ClearHistory() { }
        public Task<bool> SwitchProviderAsync(string providerName, string apiKey, string? endpoint = null)
            => Task.FromResult(true);
        public void RegisterTool(string toolName, string description, Func<string, Task<string>> handler) { }
    }
}
