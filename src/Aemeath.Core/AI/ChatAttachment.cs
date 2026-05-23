namespace Aemeath.Core.AI;

public sealed record ChatAttachment(
    string Path,
    string Name,
    string MimeType,
    ChatAttachmentKind Kind,
    long SizeBytes);

public enum ChatAttachmentKind
{
    Image,
    Text,
    Other
}
