using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DriveMigrator.App.Controls;

/// <summary>
/// Renders the Markdown of the setup guides with the app's own controls and tokens: headings, paragraphs, nested
/// lists, quotes, pipe tables, code, emphasis and links. Images and raw HTML are not supported and are left out.
/// </summary>
public sealed class MarkdownView : Decorator
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    static MarkdownView() => MarkdownProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>The text of the first level-1 heading, e.g. for a window title.</summary>
    public static string? FirstHeading(string markdown)
        => Markdig.Markdown.Parse(markdown, Pipeline).OfType<HeadingBlock>().FirstOrDefault(h => h.Level == 1) is { Inline: { } inline }
            ? PlainText(inline)
            : null;

    /// <summary>Links open in the browser; only web addresses are followed.</summary>
    public static bool IsFollowable(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string PlainText(ContainerInline container)
    {
        var text = new System.Text.StringBuilder();
        foreach (var inline in container.Descendants())
        {
            switch (inline)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
            }
        }

        return text.ToString();
    }

    private void Rebuild()
    {
        var panel = new StackPanel { Spacing = 12 };
        if (!string.IsNullOrWhiteSpace(Markdown))
        {
            AddBlocks(panel.Children, Markdig.Markdown.Parse(Markdown, Pipeline));
        }

        Child = panel;
    }

    private static void AddBlocks(Avalonia.Controls.Controls target, IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            var control = Build(block);
            if (control is null)
            {
                continue;
            }

            // Sections breathe: a heading sits further from what precedes it than from its own text.
            if (block is HeadingBlock && target.Count > 0)
            {
                control.Margin = new Thickness(0, 8, 0, 0);
            }

            target.Add(control);
        }
    }

    private static Control? Build(Block block) => block switch
    {
        HeadingBlock heading => BuildHeading(heading),
        ParagraphBlock paragraph => BuildText(paragraph.Inline, "body"),
        ListBlock list => BuildList(list),
        QuoteBlock quote => BuildQuote(quote),
        Table table => BuildTable(table),
        CodeBlock code => BuildCode(code),
        ThematicBreakBlock => BuildRule(),
        _ => null,
    };

    private static MarkdownText BuildHeading(HeadingBlock heading)
        => BuildText(heading.Inline, heading.Level switch { 1 => "title", 2 => "heading-sm", _ => "body-strong" });

    private static MarkdownText BuildText(ContainerInline? inlines, string textClass)
    {
        var text = new MarkdownText { TextWrapping = TextWrapping.Wrap };
        text.Classes.Add(textClass);
        if (inlines is not null)
        {
            AddInlines(text, inlines, bold: false, italic: false);
        }

        return text;
    }

    private static void AddInlines(MarkdownText text, ContainerInline container, bool bold, bool italic)
    {
        // Everything added is a Run, so the text position of the next one is the length of what precedes it.
        var target = text.Inlines!;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(Styled(new Run(literal.Content.ToString()), bold, italic));
                    break;
                case EmphasisInline emphasis:
                    var strong = emphasis.DelimiterCount >= 2;
                    AddInlines(text, emphasis, bold || strong, italic || !strong);
                    break;
                case CodeInline code:
                    target.Add(BuildCode(code, bold));
                    break;
                case LinkInline { IsImage: false } link:
                    AddLink(text, link.Url, PlainText(link) is { Length: > 0 } label ? label : link.Url, bold);
                    break;
                case LinkInline image:
                    // Images aren't shown; their alt text stands in for them.
                    AddInlines(text, image, bold, italic);
                    break;
                case AutolinkInline auto:
                    AddLink(text, auto.Url, auto.Url, bold);
                    break;
                case LineBreakInline line:
                    target.Add(new Run(line.IsHard ? "\n" : " "));
                    break;
                case ContainerInline nested:
                    AddInlines(text, nested, bold, italic);
                    break;
            }
        }
    }

    private static Run Styled(Run run, bool bold, bool italic)
    {
        if (bold)
        {
            run.FontWeight = FontWeight.SemiBold;
        }

        if (italic)
        {
            run.FontStyle = FontStyle.Italic;
        }

        return run;
    }

    private static Run BuildCode(CodeInline code, bool bold)
    {
        var run = Styled(new Run(code.Content) { FontFamily = MonoFont, FontSize = 12 }, bold, italic: false);
        run[!TextElement.BackgroundProperty] = run.GetResourceObservable("surface-300").ToBinding();
        return run;
    }

    private static void AddLink(MarkdownText text, string? url, string? label, bool bold)
    {
        var run = Styled(new Run(label), bold, italic: false);
        if (IsFollowable(url))
        {
            run.TextDecorations = TextDecorations.Underline;
            run[!TextElement.ForegroundProperty] = run.GetResourceObservable("accent-100").ToBinding();
            text.AddLink(run, url!);
        }

        text.Inlines!.Add(run);
    }

    private static Grid BuildList(ListBlock list)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 6 };
        var number = int.TryParse(list.OrderedStart, CultureInfo.InvariantCulture, out var start) ? start : 1;
        var row = 0;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{number++}." : "•",
                MinWidth = 20,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0),
            };
            marker.Classes.Add("body");
            marker.Classes.Add("muted");
            Grid.SetRow(marker, row);
            grid.Children.Add(marker);

            var content = new StackPanel { Spacing = 6 };
            AddBlocks(content.Children, item);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
            row++;
        }

        return grid;
    }

    private static Border BuildRule()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(0, 4) };
        rule.Classes.Add("rule");
        return rule;
    }

    private static Border BuildQuote(QuoteBlock quote)
    {
        var content = new StackPanel { Spacing = 6 };
        AddBlocks(content.Children, quote);

        var icon = new Icon { Kind = IconKind.Info };
        icon.Classes.Add("tone");
        icon.Classes.Add("info");
        var body = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        body.Children.Add(icon);
        body.Children.Add(content);

        var banner = new Border { Child = body };
        banner.Classes.Add("banner");
        banner.Classes.Add("info");
        return banner;
    }

    private static Border BuildTable(Table table)
    {
        var grid = new Grid();
        var columns = table.ColumnDefinitions.Count;
        for (var column = 0; column < columns; column++)
        {
            // Every column fits its content; the last takes the rest and wraps.
            grid.ColumnDefinitions.Add(new ColumnDefinition(column == columns - 1 ? GridLength.Star : GridLength.Auto));
        }

        var rows = table.OfType<TableRow>().ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var cells = rows[r].OfType<TableCell>().ToList();
            for (var c = 0; c < cells.Count; c++)
            {
                var inlines = cells[c].OfType<ParagraphBlock>().FirstOrDefault()?.Inline;
                var text = BuildText(inlines, rows[r].IsHeader ? "label" : "body");
                var cell = new Border
                {
                    Padding = new Thickness(12, 8),
                    BorderThickness = new Thickness(0, 0, c < cells.Count - 1 ? 1 : 0, r < rows.Count - 1 ? 1 : 0),
                    Child = text,
                };
                cell.Classes.Add("cell");
                cell.Classes.Set("head", rows[r].IsHeader);

                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        var frame = new Border { Child = grid, ClipToBounds = true };
        frame.Classes.Add("table");
        return frame;
    }

    private static Border BuildCode(CodeBlock code)
    {
        var text = new SelectableTextBlock { Text = string.Join('\n', code.Lines.Lines.Take(code.Lines.Count).Select(l => l.ToString())), FontFamily = MonoFont, FontSize = 12 };
        var box = new Border { Child = text };
        box.Classes.Add("code");
        return box;
    }
}
