using System.IO;
using System.Text;
using Aemeath.Core.AI;
using Avalonia.Platform.Storage;

namespace Aemeath.Desktop.Services;

/// <summary>
/// 管理聊天待发送附件的集合与校验逻辑（从 ChatWindow 抽出，降低主窗口体积）。
/// 职责：维护待发送附件列表、创建/校验附件、构建可见文本、文件类型定义。
/// 不负责 UI 渲染（渲染仍由 ChatWindow 的 AttachmentPanel 完成），仅提供数据。
/// </summary>
public sealed class AttachmentService
{
    private const int MaxAttachmentCount = 6;
    private const long MaxAttachmentBytes = 10 * 1024 * 1024;

    private readonly List<ChatAttachment> _pending = [];

    /// <summary>当前待发送附件的快照。</summary>
    public IReadOnlyList<ChatAttachment> Pending => _pending;

    public int Count => _pending.Count;

    public bool AtCapacity => _pending.Count >= MaxAttachmentCount;

    public bool ContainsPath(string path) =>
        _pending.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>尝试从本地路径创建并加入待发送列表。返回错误提示（如为空表示成功）。</summary>
    public string? TryAdd(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "无法读取该附件的本地路径。";
        }

        if (AtCapacity)
        {
            return $"最多一次附加 {MaxAttachmentCount} 个文件。";
        }

        if (ContainsPath(path))
        {
            return null; // 已存在，静默跳过
        }

        var (attachment, error) = CreateAttachment(path);
        if (attachment is null)
        {
            return error;
        }

        _pending.Add(attachment);
        return null;
    }

    public void Remove(ChatAttachment attachment) => _pending.Remove(attachment);

    public void Clear() => _pending.Clear();

    public List<ChatAttachment> Snapshot() => _pending.ToList();

    /// <summary>把用户输入与附件列表拼成一条可见消息文本。</summary>
    public static string BuildVisibleUserContent(string userInput, IReadOnlyList<ChatAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return userInput;
        }

        var sb = new StringBuilder();
        sb.AppendLine(userInput);
        sb.AppendLine();
        sb.AppendLine("附件：");
        foreach (var attachment in attachments)
        {
            sb.AppendLine($"- {attachment.Name} ({GetAttachmentKindLabel(attachment.Kind)}, {FormatBytes(attachment.SizeBytes)})");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>文件选择对话框的类型过滤器。</summary>
    public static IReadOnlyList<FilePickerFileType> BuildFileTypes(bool imagesOnly)
    {
        var imageType = new FilePickerFileType("图片")
        {
            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp"],
            MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/gif", "image/bmp"]
        };

        if (imagesOnly)
        {
            return [imageType];
        }

        return
        [
            new FilePickerFileType("文本与代码")
            {
                Patterns = ["*.txt", "*.md", "*.markdown", "*.cs", "*.json", "*.xml", "*.xaml", "*.axaml", "*.yaml", "*.yml", "*.log", "*.csv", "*.tsv", "*.html", "*.css", "*.js", "*.ts", "*.py", "*.ps1", "*.bat"],
                MimeTypes = ["text/plain", "text/markdown", "application/json", "application/xml", "text/csv", "text/html", "text/css", "text/javascript"]
            },
            FilePickerFileTypes.All
        ];
    }

    private static (ChatAttachment? attachment, string? error) CreateAttachment(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (null, "文件不存在：" + Path.GetFileName(path));
            }

            var info = new FileInfo(path);
            if (info.Length > MaxAttachmentBytes)
            {
                return (null, $"文件过大：{info.Name}，单个附件最大 {FormatBytes(MaxAttachmentBytes)}。");
            }

            var extension = info.Extension.ToLowerInvariant();
            var kind = GetAttachmentKind(extension);
            return (new ChatAttachment(
                info.FullName,
                info.Name,
                GetMimeType(extension, kind),
                kind,
                info.Length), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (null, "无法读取文件：" + ex.Message);
        }
    }

    private static ChatAttachmentKind GetAttachmentKind(string extension)
    {
        if (extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp")
        {
            return ChatAttachmentKind.Image;
        }

        return extension is ".txt" or ".md" or ".markdown" or ".cs" or ".json" or ".xml" or ".xaml" or ".axaml" or ".yaml" or ".yml" or ".log" or ".csv" or ".tsv" or ".html" or ".css" or ".js" or ".ts" or ".py" or ".ps1" or ".bat"
            ? ChatAttachmentKind.Text
            : ChatAttachmentKind.Other;
    }

    private static string GetMimeType(string extension, ChatAttachmentKind kind)
    {
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".json" => "application/json",
            ".xml" or ".xaml" or ".axaml" => "application/xml",
            ".csv" => "text/csv",
            ".tsv" => "text/tab-separated-values",
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "text/javascript",
            _ => kind == ChatAttachmentKind.Text ? "text/plain" : "application/octet-stream"
        };
    }

    public static string GetAttachmentKindLabel(ChatAttachmentKind kind)
    {
        return kind switch
        {
            ChatAttachmentKind.Image => "图片",
            ChatAttachmentKind.Text => "文本",
            _ => "文件"
        };
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.##} MB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:0.##} KB" : $"{bytes} B";
    }
}
