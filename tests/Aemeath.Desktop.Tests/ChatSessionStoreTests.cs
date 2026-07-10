using Aemeath.Core.AI;
using Aemeath.Desktop.Services;

namespace Aemeath.Desktop.Tests;

public sealed class ChatSessionStoreTests
{
    [Fact]
    public void AppendMessage_FirstUserMessage_GeneratesShortTitleAndSupportsRename()
    {
        using var temp = new TemporaryDirectory();
        var store = new ChatSessionStore(System.IO.Path.Combine(temp.Path, "sessions.json"));
        var session = store.CreateSession();
        var longMessage = "请帮我规划一个包含多个阶段、风险和验收标准的完整项目执行方案，并保持结构清晰";

        store.AppendMessage(session.Id, "user", longMessage);

        var updated = store.GetSession(session.Id);
        Assert.NotNull(updated);
        Assert.EndsWith("…", updated.Title);
        Assert.True(updated.Title.Length <= 31);
        Assert.True(store.RenameSession(session.Id, "  发布准备  "));
        Assert.Equal("发布准备", store.GetSession(session.Id)?.Title);
    }

    [Fact]
    public void RenameSession_BlankTitle_DoesNotChangeSession()
    {
        using var temp = new TemporaryDirectory();
        var store = new ChatSessionStore(System.IO.Path.Combine(temp.Path, "sessions.json"));
        var session = store.CreateSession("保留标题");

        var result = store.RenameSession(session.Id, "   ");

        Assert.False(result);
        Assert.Equal("保留标题", store.GetSession(session.Id)?.Title);
    }

    [Fact]
    public void Attachments_RoundTripAndPureAttachmentKeepsVisibleTextEmpty()
    {
        using var temp = new TemporaryDirectory();
        var store = new ChatSessionStore(System.IO.Path.Combine(temp.Path, "sessions.json"));
        var session = store.CreateSession();
        var attachment = new ChatAttachment(
            System.IO.Path.Combine(temp.Path, "photo.png"),
            "photo.png",
            "image/png",
            ChatAttachmentKind.Image,
            1234);

        store.AppendMessage(session.Id, "user", string.Empty, [attachment]);

        var message = Assert.Single(store.GetSession(session.Id)!.Messages);
        Assert.Equal(string.Empty, message.Content);
        var restored = Assert.Single(message.Attachments);
        Assert.Equal(attachment, restored);
        Assert.Equal("photo.png", store.GetSession(session.Id)!.Title);

        store.ReplaceMessages(session.Id, store.GetSession(session.Id)!.Messages);
        Assert.Equal(attachment, Assert.Single(store.GetSession(session.Id)!.Messages[0].Attachments));
        Assert.Equal(string.Empty, AttachmentService.BuildVisibleUserContent(string.Empty, [attachment]));
    }

    [Fact]
    public void LegacyJson_WithoutAttachments_LoadsWithEmptyAttachmentCollection()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "sessions.json");
        File.WriteAllText(path, """
        {
          "Sessions": [
            {
              "Id": "legacy",
              "Title": "\u65e7\u4f1a\u8bdd",
              "CreatedAt": "2026-01-01T00:00:00+00:00",
              "UpdatedAt": "2026-01-01T00:00:00+00:00",
              "Messages": [
                {
                  "Role": "user",
                  "Content": "\u65e7\u6d88\u606f",
                  "Timestamp": "2026-01-01T00:00:00+00:00"
                }
              ]
            }
          ]
        }
        """);

        var message = Assert.Single(new ChatSessionStore(path).GetSession("legacy")!.Messages);

        Assert.NotNull(message.Attachments);
        Assert.Empty(message.Attachments);
        Assert.Equal("\u65e7\u6d88\u606f", message.Content);
    }
}
