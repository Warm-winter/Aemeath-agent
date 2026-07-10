using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Aemeath.Desktop.Services;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Aemeath.Desktop.Views;
using MarkdownInline = Markdig.Syntax.Inlines.Inline;

internal sealed class MarkdownPresenter : UserControl
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public MarkdownPresenter(string markdown)
    {
        Content = BuildDocument(markdown ?? string.Empty);
    }

    internal static bool IsSafeLink(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static Control BuildDocument(string markdown)
    {
        var panel = new StackPanel { Spacing = 8 };
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var block in document)
        {
            var control = RenderBlock(block);
            if (control is not null)
            {
                panel.Children.Add(control);
            }
        }

        if (panel.Children.Count == 0)
        {
            panel.Children.Add(CreateText(markdown));
        }

        return panel;
    }

    private static Control? RenderBlock(Block block)
    {
        return block switch
        {
            HeadingBlock heading => RenderHeading(heading),
            ParagraphBlock paragraph => RenderParagraph(paragraph),
            FencedCodeBlock fenced => RenderCodeBlock(fenced),
            CodeBlock code => RenderCodeBlock(code),
            QuoteBlock quote => RenderQuote(quote),
            ListBlock list => RenderList(list),
            Table table => RenderTable(table),
            ThematicBreakBlock => new Border
            {
                Height = 1,
                Background = AemiUi.Brush(AemiUi.Border),
                Margin = new Thickness(0, 6)
            },
            HtmlBlock => null,
            ContainerBlock container => RenderContainer(container),
            LeafBlock leaf when leaf.Inline is not null => CreateInlineText(leaf.Inline),
            _ => null
        };
    }

    private static Control RenderHeading(HeadingBlock heading)
    {
        var text = CreateInlineText(heading.Inline);
        text.FontSize = heading.Level switch
        {
            1 => 22,
            2 => 19,
            3 => 17,
            _ => 15
        };
        text.FontWeight = FontWeight.SemiBold;
        text.Margin = new Thickness(0, heading.Level <= 2 ? 6 : 3, 0, 0);
        AutomationProperties.SetHeadingLevel(text, Math.Clamp(heading.Level, 1, 6));
        return text;
    }

    private static Control RenderParagraph(ParagraphBlock paragraph)
        => CreateInlineText(paragraph.Inline);

    private static Control RenderQuote(QuoteBlock quote)
    {
        return new Border
        {
            Background = AemiUi.Brush(AemiUi.PanelSoft),
            BorderBrush = AemiUi.Brush(AemiUi.Pink),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Child = RenderContainer(quote)
        };
    }

    private static Control RenderContainer(ContainerBlock container)
    {
        var panel = new StackPanel { Spacing = 7 };
        foreach (var child in container)
        {
            var rendered = RenderBlock(child);
            if (rendered is not null)
            {
                panel.Children.Add(rendered);
            }
        }
        return panel;
    }

    private static Control RenderList(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 5 };
        var index = list.IsOrdered && int.TryParse(list.OrderedStart, out var parsed) ? parsed : 1;
        foreach (var child in list)
        {
            if (child is not ListItemBlock item)
            {
                continue;
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(new TextBlock
            {
                Text = list.IsOrdered ? $"{index}." : "\u2022",
                Foreground = AemiUi.Brush(AemiUi.Icon),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 8, 0)
            });
            var body = RenderContainer(item);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            panel.Children.Add(row);
            index++;
        }
        return panel;
    }

    private static Control RenderCodeBlock(CodeBlock block)
    {
        var code = block.Lines.ToString().TrimEnd();
        var language = block is FencedCodeBlock fenced
            ? fenced.Info?.ToString().Trim()
            : string.Empty;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "CODE" : language.ToUpperInvariant(),
            FontFamily = new FontFamily("Cascadia Code,Consolas,Courier New"),
            FontSize = 11,
            Foreground = AemiUi.Brush(AemiUi.TextMuted),
            VerticalAlignment = VerticalAlignment.Center
        });

        var copyButton = AemiUi.Button("\u590d\u5236\u4ee3\u7801", "ghost", 86);
        copyButton.MinHeight = 30;
        copyButton.Padding = new Thickness(9, 4);
        AutomationProperties.SetName(copyButton, "\u590d\u5236\u4ee3\u7801\u5757");
        copyButton.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(copyButton)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(code);
                copyButton.Content = "\u5df2\u590d\u5236";
            }
        };
        Grid.SetColumn(copyButton, 1);
        header.Children.Add(copyButton);

        var codeText = new SelectableTextBlock
        {
            Text = code,
            FontFamily = new FontFamily("Cascadia Code,Consolas,Courier New"),
            FontSize = 13,
            LineHeight = 20,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = AemiUi.Brush(AemiUi.Ghost)
        };

        return new Border
        {
            Background = AemiUi.Brush(AemiUi.CodeSurface),
            BorderBrush = AemiUi.Brush(AemiUi.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                Children =
                {
                    header,
                    new ScrollViewer
                    {
                        Margin = new Thickness(0, 8, 0, 0),
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        Content = codeText
                    }
                }
            }
        }.Also(border =>
        {
            if (border.Child is Grid grid && grid.Children.Count > 1)
            {
                Grid.SetRow(grid.Children[1], 1);
            }
        });
    }

    private static Control RenderTable(Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        var columnCount = rows.Select(row => row.Count).DefaultIfEmpty(0).Max();
        if (columnCount == 0)
        {
            return CreateText(string.Empty);
        }

        var grid = new Grid();
        for (var i = 0; i < columnCount; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
            {
                if (row[columnIndex] is not TableCell cell)
                {
                    continue;
                }

                var text = new SelectableTextBlock
                {
                    Text = ExtractText(cell),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = AemiUi.Brush(AemiUi.Ghost),
                    FontWeight = row.IsHeader ? FontWeight.SemiBold : FontWeight.Normal
                };
                var border = new Border
                {
                    Background = AemiUi.Brush(row.IsHeader ? AemiUi.HaloSoft : AemiUi.Panel),
                    BorderBrush = AemiUi.Brush(AemiUi.Border),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(10, 7),
                    Child = text
                };
                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, columnIndex);
                grid.Children.Add(border);
            }
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = grid
        };
    }

    private static SelectableTextBlock CreateInlineText(ContainerInline? container)
    {
        var block = CreateText(string.Empty);
        if (container is null)
        {
            return block;
        }

        AppendInlines(container.FirstChild, block.Inlines!);
        return block;
    }

    private static SelectableTextBlock CreateText(string text)
    {
        return new SelectableTextBlock
        {
            Text = text,
            Foreground = AemiUi.Brush(AemiUi.Ghost),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 23,
            FontSize = 15,
            SelectionBrush = AemiUi.Brush(AemiUi.PinkSoft),
            SelectionForegroundBrush = AemiUi.Brush(AemiUi.Ghost)
        };
    }

    private static void AppendInlines(MarkdownInline? inline, InlineCollection target)
    {
        for (var current = inline; current is not null; current = current.NextSibling)
        {
            switch (current)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case LineBreakInline:
                    target.Add(new Run(Environment.NewLine));
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Cascadia Code,Consolas,Courier New"),
                        Foreground = AemiUi.Brush(AemiUi.Icon)
                    });
                    break;
                case EmphasisInline emphasis:
                {
                    Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AppendInlines(emphasis.FirstChild, span.Inlines);
                    target.Add(span);
                    break;
                }
                case LinkInline link:
                {
                    var label = ExtractInlineText(link);
                    if (link.IsImage)
                    {
                        target.Add(new Run($"[\u56fe\u7247: {label}]"));
                        break;
                    }

                    if (IsSafeLink(link.Url))
                    {
                        var linkText = string.IsNullOrWhiteSpace(label) ? link.Url! : label;
                        var button = new HyperlinkButton
                        {
                            Content = linkText,
                            NavigateUri = new Uri(link.Url!, UriKind.Absolute),
                            Padding = new Thickness(0),
                            MinHeight = 0,
                            Foreground = AemiUi.Brush(AemiUi.Icon)
                        };
                        AutomationProperties.SetName(button, $"\u6253\u5f00\u94fe\u63a5 {linkText}");
                        target.Add(button);
                    }
                    else
                    {
                        target.Add(new Run(label));
                    }
                    break;
                }
                case ContainerInline nested:
                    AppendInlines(nested.FirstChild, target);
                    break;
                case HtmlInline:
                    break;
                default:
                    target.Add(new Run(current.ToString() ?? string.Empty));
                    break;
            }
        }
    }

    private static string ExtractInlineText(ContainerInline container)
    {
        var builder = new System.Text.StringBuilder();
        for (var current = container.FirstChild; current is not null; current = current.NextSibling)
        {
            switch (current)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LineBreakInline:
                    builder.Append(' ');
                    break;
                case ContainerInline nested:
                    builder.Append(ExtractInlineText(nested));
                    break;
            }
        }
        return builder.ToString();
    }

    private static string ExtractText(ContainerBlock block)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var child in block)
        {
            if (child is LeafBlock { Inline: not null } leaf)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }
                builder.Append(ExtractInlineText(leaf.Inline));
            }
            else if (child is ContainerBlock nested)
            {
                builder.Append(ExtractText(nested));
            }
        }
        return builder.ToString();
    }
}

internal static class ControlExtensions
{
    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
