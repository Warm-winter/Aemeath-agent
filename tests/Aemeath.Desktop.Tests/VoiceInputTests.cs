using Avalonia.Automation;
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

public sealed class VoiceInputTests
{
    [AvaloniaFact]
    public async Task RecordButton_ClickStartThenStop_ShowsStatesAndAutoSendsRecognition()
    {
        using var temp = new TemporaryDirectory();
        var settings = CreateConfiguredSettings(temp.Path);
        var sessions = new ChatSessionStore(Path.Combine(temp.Path, "sessions.json"));
        var chat = new CapturingChatService();
        var voice = new FakeVoiceCaptureSession();
        var window = new ChatWindow(
            chat,
            settings,
            sessions,
            new AttachmentThumbnailCache(),
            () => voice);

        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var modeButton = window.FindControl<Button>("VoiceButton")!;
            var recordButton = window.FindControl<Button>("VoiceRecordButton")!;
            var recordText = window.FindControl<TextBlock>("VoiceRecordButtonText")!;
            var pulse = window.FindControl<Avalonia.Controls.Shapes.Ellipse>("VoicePulseRing")!;

            modeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            recordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await voice.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsVoiceRecording);
            Assert.StartsWith("\u7ed3\u675f\u5f55\u97f3", recordText.Text);
            Assert.Equal("\u7ed3\u675f\u5f55\u97f3", AutomationProperties.GetName(recordButton));
            Assert.Contains("recording", recordButton.Classes);
            Assert.True(pulse.IsVisible);

            await Task.Delay(520);
            recordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await voice.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsVoiceRecognizing);
            Assert.Equal("\u6b63\u5728\u8bc6\u522b\u2026", recordText.Text);
            Assert.Equal("\u6b63\u5728\u8bc6\u522b\u8bed\u97f3", AutomationProperties.GetName(recordButton));
            Assert.Contains("recognizing", recordButton.Classes);

            voice.Recognition.TrySetResult("\u8bed\u97f3\u6d4b\u8bd5");
            var prompt = await chat.StreamingPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("\u8bed\u97f3\u6d4b\u8bd5", prompt, StringComparison.Ordinal);
            Assert.False(window.IsVoiceRecording);
            Assert.False(window.IsVoiceRecognizing);
            Assert.Equal("\u5f00\u59cb\u5f55\u97f3", recordText.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RecordButton_ReduceMotion_UsesStaticRecordingIndicator()
    {
        using var temp = new TemporaryDirectory();
        var settings = CreateConfiguredSettings(temp.Path);
        settings.Current.ReduceMotion = true;
        settings.Save();
        var voice = new FakeVoiceCaptureSession();
        var window = new ChatWindow(
            new CapturingChatService(),
            settings,
            new ChatSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new AttachmentThumbnailCache(),
            () => voice);

        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.FindControl<Button>("VoiceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.FindControl<Button>("VoiceRecordButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await voice.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsVoiceRecording);
            Assert.False(window.IsVoicePulseAnimationRunning);
            Assert.False(window.FindControl<Avalonia.Controls.Shapes.Ellipse>("VoicePulseRing")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static SettingsService CreateConfiguredSettings(string directory)
    {
        var settings = new SettingsService(Path.Combine(directory, "settings.json"));
        settings.UpdateApiKey("openai", "test-key", "https://example.invalid/v1", "test-model");
        return settings;
    }

    private sealed class FakeVoiceCaptureSession : IVoiceCaptureSession
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Recognition { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            return Task.CompletedTask;
        }

        public Task<string?> StopAndRecognizeAsync(CancellationToken cancellationToken)
        {
            StopRequested.TrySetResult(true);
            cancellationToken.Register(() => Recognition.TrySetCanceled(cancellationToken));
            return Recognition.Task;
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingChatService : IChatService
    {
        public TaskCompletionSource<string> StreamingPrompt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string CurrentAssistantName => "test";
        public bool IsProcessing => false;

        public Task<string> SendMessageAsync(string message, CancellationToken cancellationToken = default)
            => Task.FromResult("ok");

        public Task<string> SendMessageAsync(string message, IReadOnlyList<ChatAttachment>? attachments, CancellationToken cancellationToken = default)
            => Task.FromResult("ok");

        public IAsyncEnumerable<string> SendMessageStreamingAsync(string message, CancellationToken cancellationToken = default)
            => SendMessageStreamingAsync(message, null, cancellationToken);

        public async IAsyncEnumerable<string> SendMessageStreamingAsync(
            string message,
            IReadOnlyList<ChatAttachment>? attachments,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingPrompt.TrySetResult(message);
            yield return "\u6536\u5230\u5566\u3002";
            await Task.CompletedTask;
        }

        public void ClearHistory()
        {
        }

        public Task<bool> SwitchProviderAsync(string providerName, string apiKey, string? endpoint = null)
            => Task.FromResult(true);

        public void RegisterTool(string toolName, string description, Func<string, Task<string>> handler)
        {
        }
    }
}
