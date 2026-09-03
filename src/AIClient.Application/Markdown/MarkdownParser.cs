using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AIClient.Application.Markdown;

/// <summary>
/// Parses Markdown into the <see cref="MarkdownDocument"/> block model the chat view renders.
/// </summary>
/// <remarks>
/// Markdig does the CommonMark parsing; this class projects its AST onto a small, closed
/// model. The projection is worth the code: it keeps Markdig types out of the view layer,
/// makes the renderer a total switch over a handful of cases, and gives every block a
/// content hash so streaming can reuse unchanged visuals.
///
/// The parser is called on every streamed chunk, so it must stay allocation-light and must
/// tolerate incomplete input - a half-written fence or an unclosed bold marker is normal
/// mid-stream and must not throw or flicker.
/// </remarks>
public sealed class MarkdownParser
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdownParser()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseGridTables()
            .UsePipeTables()
            .UseEmphasisExtras()
            .UseAutoLinks()
            .UseTaskLists()
            // No UseAdvancedExtensions(): it pulls in footnotes, abbreviations, figures and
            // custom containers, none of which models emit and all of which are extra
            // parse work on a hot path.
            .Build();
    }

    /// <summary>Parses Markdown into blocks. Never throws on malformed or partial input.</summary>
    public MarkdownDocument Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return MarkdownDocument.Empty;
        }

        MarkdownObject parsed;
        try
        {
            parsed = Markdig.Markdown.Parse(markdown, _pipeline);
        }
        catch (Exception)
        {
            // Mid-stream text can be pathological. Degrading to a plain paragraph is far
            // better than letting an exception reach the UI thread on every token.
            return new MarkdownDocument([new ParagraphBlock([new InlineSpan(markdown)])]);
        }

        var blocks = new List<MarkdownBlock>();
        ConvertBlocks((ContainerBlock)parsed, blocks);

        return blocks.Count == 0
            ? new MarkdownDocument([new ParagraphBlock([new InlineSpan(markdown)])])
            : new MarkdownDocument(blocks);
    }

    private static void ConvertBlocks(ContainerBlock container, List<MarkdownBlock> output)
    {
        foreach (var block in container)
        {
            var converted = ConvertBlock(block);
            if (converted is not null)
            {
                output.Add(converted);
            }
        }
    }

    private static MarkdownBlock? ConvertBlock(Block block)
    {
        switch (block)
        {
            case Markdig.Syntax.ParagraphBlock paragraph:
            {
                var spans = ConvertInlines(paragraph.Inline);
                return spans.Count == 0 ? null : new ParagraphBlock(spans);
            }

            case Markdig.Syntax.HeadingBlock heading:
            {
                var spans = ConvertInlines(heading.Inline);
                return spans.Count == 0 ? null : new HeadingBlock(Math.Clamp(heading.Level, 1, 6), spans);
            }

            case FencedCodeBlock fenced:
            {
                var language = (fenced.Info ?? string.Empty).Trim();
                var code = ExtractCode(fenced);
                return new CodeBlock(language, code, SyntaxHighlighter.Highlight(code, language));
            }

            case Markdig.Syntax.CodeBlock code when code is not FencedCodeBlock:
            {
                var text = ExtractCode(code);
                return string.IsNullOrWhiteSpace(text)
                    ? null
                    : new CodeBlock(string.Empty, text, SyntaxHighlighter.Highlight(text, null));
            }

            case Markdig.Syntax.ListBlock list:
                return ConvertList(list);

            case Markdig.Syntax.QuoteBlock quote:
            {
                var inner = new List<MarkdownBlock>();
                ConvertBlocks(quote, inner);
                return inner.Count == 0 ? null : new QuoteBlock(inner);
            }

            case Table table:
                return ConvertTable(table);

            case Markdig.Syntax.ThematicBreakBlock:
                return new ThematicBreakBlock();

            case ContainerBlock nested:
            {
                // Unmodelled containers (custom containers, list item wrappers reached
                // directly) still have renderable children; flatten rather than drop them.
                var inner = new List<MarkdownBlock>();
                ConvertBlocks(nested, inner);
                return inner.Count switch
                {
                    0 => null,
                    1 => inner[0],
                    _ => new QuoteBlock(inner),
                };
            }

            default:
                return null;
        }
    }

    private static MarkdownBlock? ConvertList(Markdig.Syntax.ListBlock list, int level = 0)
    {
        var items = new List<ListItem>();

        foreach (var child in list)
        {
            if (child is not ListItemBlock itemBlock)
            {
                continue;
            }

            var blocks = new List<MarkdownBlock>();

            foreach (var inner in itemBlock)
            {
                // A nested list becomes further items at a deeper level rather than a
                // nested ListBlock, which keeps the renderer's job to a flat loop.
                if (inner is Markdig.Syntax.ListBlock nestedList)
                {
                    if (blocks.Count > 0)
                    {
                        items.Add(new ListItem(level, blocks));
                        blocks = [];
                    }

                    if (ConvertList(nestedList, level + 1) is ListBlock nested)
                    {
                        items.AddRange(nested.Items);
                    }

                    continue;
                }

                var converted = ConvertBlock(inner);
                if (converted is not null)
                {
                    blocks.Add(converted);
                }
            }

            if (blocks.Count > 0)
            {
                items.Add(new ListItem(level, blocks));
            }
        }

        if (items.Count == 0)
        {
            return null;
        }

        var start = list.IsOrdered && int.TryParse(list.OrderedStart, out var parsed) ? parsed : 1;
        return new ListBlock(list.IsOrdered, start, items);
    }

    private static MarkdownBlock? ConvertTable(Table table)
    {
        List<IReadOnlyList<InlineSpan>>? headers = null;
        var rows = new List<IReadOnlyList<IReadOnlyList<InlineSpan>>>();

        foreach (var child in table)
        {
            if (child is not TableRow row)
            {
                continue;
            }

            var cells = new List<IReadOnlyList<InlineSpan>>();

            foreach (var cellObject in row)
            {
                if (cellObject is not TableCell cell)
                {
                    continue;
                }

                var spans = new List<InlineSpan>();
                foreach (var content in cell)
                {
                    if (content is Markdig.Syntax.ParagraphBlock paragraph)
                    {
                        spans.AddRange(ConvertInlines(paragraph.Inline));
                    }
                }

                cells.Add(spans);
            }

            if (row.IsHeader && headers is null)
            {
                headers = cells;
            }
            else
            {
                rows.Add(cells);
            }
        }

        if (headers is null && rows.Count == 0)
        {
            return null;
        }

        return new TableBlock(headers ?? [], rows);
    }

    /// <summary>Flattens Markdig's inline tree into styled runs, merging adjacent runs that match.</summary>
    private static IReadOnlyList<InlineSpan> ConvertInlines(ContainerInline? container)
    {
        if (container is null)
        {
            return [];
        }

        var spans = new List<InlineSpan>();
        AppendInlines(container, InlineStyle.None, null, spans);
        return Merge(spans);
    }

    private static void AppendInlines(
        ContainerInline container,
        InlineStyle style,
        string? url,
        List<InlineSpan> output)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                {
                    var text = literal.Content.ToString();
                    if (text.Length > 0)
                    {
                        output.Add(new InlineSpan(text, style, url));
                    }

                    break;
                }

                case CodeInline code:
                    output.Add(new InlineSpan(code.Content, style | InlineStyle.Code, url));
                    break;

                case EmphasisInline emphasis:
                {
                    // Markdig encodes bold/italic/strikethrough by delimiter char and count.
                    var added = emphasis.DelimiterChar switch
                    {
                        '~' => InlineStyle.Strikethrough,
                        _ => emphasis.DelimiterCount >= 2 ? InlineStyle.Bold : InlineStyle.Italic,
                    };

                    AppendInlines(emphasis, style | added, url, output);
                    break;
                }

                case LinkInline link:
                {
                    if (link.IsImage)
                    {
                        // Images are not rendered in the MVP; showing the alt text keeps the
                        // sentence readable instead of leaving a hole in it.
                        var alt = link.FirstChild is LiteralInline lit ? lit.Content.ToString() : "image";
                        output.Add(new InlineSpan($"[{alt}]", style | InlineStyle.Italic, null));
                        break;
                    }

                    AppendInlines(link, style | InlineStyle.Link, link.Url, output);
                    break;
                }

                case AutolinkInline autolink:
                    output.Add(new InlineSpan(autolink.Url, style | InlineStyle.Link, autolink.Url));
                    break;

                case LineBreakInline lineBreak:
                    output.Add(new InlineSpan(lineBreak.IsHard ? "\n" : " ", style, url));
                    break;

                case ContainerInline nested:
                    AppendInlines(nested, style, url, output);
                    break;

                case HtmlInline:
                    // Raw HTML is deliberately dropped rather than rendered: this is untrusted
                    // model output and the app must never interpret markup from it.
                    break;
            }
        }
    }

    /// <summary>Coalesces neighbouring runs with identical styling, which the tree walk produces in quantity.</summary>
    private static List<InlineSpan> Merge(List<InlineSpan> spans)
    {
        if (spans.Count < 2)
        {
            return spans;
        }

        var merged = new List<InlineSpan>(spans.Count);
        var current = spans[0];

        for (var i = 1; i < spans.Count; i++)
        {
            var next = spans[i];

            // Code runs stay separate: each is rendered with its own background.
            if (current.Style == next.Style &&
                current.Url == next.Url &&
                !current.Style.HasFlag(InlineStyle.Code))
            {
                current = current with { Text = current.Text + next.Text };
                continue;
            }

            merged.Add(current);
            current = next;
        }

        merged.Add(current);
        return merged;
    }

    private static string ExtractCode(LeafBlock block)
    {
        if (block.Lines.Lines is null)
        {
            return string.Empty;
        }

        var lines = block.Lines;
        var builder = new System.Text.StringBuilder();

        for (var i = 0; i < lines.Count; i++)
        {
            builder.Append(lines.Lines[i].Slice.ToString());
            if (i < lines.Count - 1)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString().TrimEnd('\n');
    }
}
