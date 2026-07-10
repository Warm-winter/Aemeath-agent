using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Aemeath.Core.AI;

namespace Aemeath.Desktop.Services;

internal static class AttachmentCardFactory
{
    public static Border CreateFileCard(
        ChatAttachment attachment,
        IImage icon,
        string? unavailableReason = null)
    {
        var unavailable = !string.IsNullOrWhiteSpace(unavailableReason);
        var iconImage = new Image
        {
            Source = icon,
            Width = 24,
            Height = 24,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };

        var metadata = unavailable
            ? $"{unavailableReason} \u00B7 {AttachmentService.FormatBytes(attachment.SizeBytes)}"
            : $"{AttachmentService.GetAttachmentKindLabel(attachment.Kind)} \u00B7 {attachment.MimeType} \u00B7 {AttachmentService.FormatBytes(attachment.SizeBytes)}";
        var details = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = attachment.Name,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = AemiUi.Brush(AemiUi.Ghost),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 330
                },
                new TextBlock
                {
                    Text = metadata,
                    FontSize = 11,
                    Foreground = AemiUi.Brush(unavailable ? AemiUi.Error : AemiUi.TextMuted),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 330
                }
            }
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 10,
            Children = { iconImage, details }
        };
        Grid.SetColumn(details, 1);

        var card = new Border
        {
            MaxWidth = 410,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(11, 8),
            CornerRadius = new CornerRadius(11),
            Background = AemiUi.Brush(unavailable ? AemiUi.ErrorSurface : AemiUi.HaloSoft),
            BorderBrush = AemiUi.Brush(unavailable ? AemiUi.ErrorBorder : AemiUi.Border),
            BorderThickness = new Thickness(1),
            Child = grid
        };
        AutomationProperties.SetName(
            card,
            unavailable
                ? $"\u4e0d\u53ef\u7528\u9644\u4ef6 {attachment.Name}\uff1a{unavailableReason}"
                : $"\u9644\u4ef6 {attachment.Name}\uff0c{AttachmentService.GetAttachmentKindLabel(attachment.Kind)}\uff0c{AttachmentService.FormatBytes(attachment.SizeBytes)}");
        ToolTip.SetTip(card, attachment.Path);
        return card;
    }
}
