using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Aemeath.Core.AI;
using Aemeath.Core.Configuration;
using Aemeath.Desktop.Services;
using Aemeath.Desktop.Views;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Aemeath.Desktop.Tests;

public sealed class AttachmentRenderingTests
{
    [AvaloniaFact]
    public async Task ThumbnailCache_DecodesFirstFrameWithinBoundAndReusesBoundedEntry()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "large.png");
        using (var image = new Image<Rgba32>(1200, 600, new Rgba32(255, 105, 180, 255)))
        {
            image.SaveAsPng(path);
        }
        var attachment = new ChatAttachment(path, "large.png", "image/png", ChatAttachmentKind.Image, new FileInfo(path).Length);
        using var cache = new AttachmentThumbnailCache();

        var first = await cache.GetAsync(attachment);
        var second = await cache.GetAsync(attachment);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first.PixelSize.Width <= AttachmentThumbnailCache.MaxThumbnailDimension);
        Assert.True(first.PixelSize.Height <= AttachmentThumbnailCache.MaxThumbnailDimension);
        Assert.Equal(1, cache.CachedEntryCount);
        Assert.True(cache.CachedEntryCount <= AttachmentThumbnailCache.MaxCacheEntries);
    }

    [AvaloniaFact]
    public async Task ThumbnailCache_MissingOrCorruptImage_ReturnsNull()
    {
        using var temp = new TemporaryDirectory();
        var corruptPath = Path.Combine(temp.Path, "broken.png");
        await File.WriteAllBytesAsync(corruptPath, [1, 2, 3, 4]);
        using var cache = new AttachmentThumbnailCache();

        var missing = await cache.GetAsync(new ChatAttachment(
            Path.Combine(temp.Path, "missing.png"), "missing.png", "image/png", ChatAttachmentKind.Image, 0));
        var corrupt = await cache.GetAsync(new ChatAttachment(
            corruptPath, "broken.png", "image/png", ChatAttachmentKind.Image, 4));

        Assert.Null(missing);
        Assert.Null(corrupt);
    }

    [AvaloniaFact]
    public void FileCard_MissingFile_ExposesUnavailableAccessibleState()
    {
        var attachment = new ChatAttachment(
            @"C:\missing\report.pdf",
            "report.pdf",
            "application/pdf",
            ChatAttachmentKind.Other,
            4096);
        var icon = AemiUi.CreateVectorIcon("M2 2 L14 2 L14 14 L2 14 Z", 16, 16);

        var card = AttachmentCardFactory.CreateFileCard(attachment, icon, "\u6587\u4ef6\u4e0d\u5b58\u5728");

        Assert.Contains("\u4e0d\u53ef\u7528\u9644\u4ef6", AutomationProperties.GetName(card));
        Assert.Contains("report.pdf", AutomationProperties.GetName(card));
        Assert.IsType<Grid>(card.Child);
    }

    [AvaloniaFact]
    public void MessageActions_AreAlwaysVisibleAndDeleteUsesDangerTone()
    {
        using var temp = new TemporaryDirectory();
        var settings = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        var store = new ChatSessionStore(Path.Combine(temp.Path, "sessions.json"));
        var window = new ChatWindow(new NoOpChatService(), settings, store, new AttachmentThumbnailCache());
        window.Show();
        try
        {
            var actions = window.BuildMessageActions(0, isAssistant: true);

            Assert.Equal(1, actions.Opacity);
            Assert.True(actions.IsHitTestVisible);
            Assert.Equal(3, actions.Children.Count);
            var delete = Assert.IsType<Button>(actions.Children[2]);
            Assert.Contains("danger", delete.Classes);
            Assert.DoesNotContain("ghost", delete.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RegenerateAssistant_PassesPersistedUserAttachmentsToChatService()
    {
        using var temp = new TemporaryDirectory();
        var settings = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        var store = new ChatSessionStore(Path.Combine(temp.Path, "sessions.json"));
        var session = store.CreateSession("attachment retry");
        var path = Path.Combine(temp.Path, "image.png");
        using (var image = new Image<Rgba32>(32, 32, new Rgba32(255, 105, 180, 255)))
        {
            image.SaveAsPng(path);
        }
        var attachment = new ChatAttachment(path, "image.png", "image/png", ChatAttachmentKind.Image, new FileInfo(path).Length);
        store.AppendMessage(session.Id, "user", "inspect", [attachment]);
        store.AppendMessage(session.Id, "assistant", "first reply");
        var chatService = new CapturingChatService();
        var window = new ChatWindow(chatService, settings, store, new AttachmentThumbnailCache());
        window.Show();
        try
        {
            var actions = window.BuildMessageActions(1, isAssistant: true);
            var retry = Assert.IsType<Button>(actions.Children[1]);
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var received = await chatService.AttachmentsReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(attachment, Assert.Single(received!));
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class CapturingChatService : IChatService
    {
        public TaskCompletionSource<IReadOnlyList<ChatAttachment>?> AttachmentsReceived { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string CurrentAssistantName => "test";
        public bool IsProcessing => false;

        public Task<string> SendMessageAsync(string message, CancellationToken cancellationToken = default)
            => Task.FromResult("ok");

        public Task<string> SendMessageAsync(
            string message,
            IReadOnlyList<ChatAttachment>? attachments,
            CancellationToken cancellationToken = default)
        {
            AttachmentsReceived.TrySetResult(attachments);
            return Task.FromResult("ok");
        }

        public IAsyncEnumerable<string> SendMessageStreamingAsync(
            string message,
            CancellationToken cancellationToken = default)
            => SendMessageStreamingAsync(message, null, cancellationToken);

        public async IAsyncEnumerable<string> SendMessageStreamingAsync(
            string message,
            IReadOnlyList<ChatAttachment>? attachments,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            AttachmentsReceived.TrySetResult(attachments);
            yield return "ok";
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
