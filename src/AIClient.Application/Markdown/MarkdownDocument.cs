namespace AIClient.Application.Markdown;

/// <summary>
/// A parsed Markdown document as a flat list of blocks.
/// </summary>
/// <remarks>
/// The UI renders one control per block and, while streaming, diffs the new block list
/// against the previous one so only genuinely changed blocks are rebuilt. Re-rendering a
/// whole answer per token is what makes naive streaming chat UIs stutter; this model is
/// what avoids it, and it lives here rather than in the view so it can be unit-tested.
/// </remarks>
public sealed record MarkdownDocument(IReadOnlyList<MarkdownBlock> Blocks)
{
    public static MarkdownDocument Empty { get; } = new([]);
}

/// <summary>Base of the block hierarchy. Closed set: the renderer switches over it exhaustively.</summary>
public abstract record MarkdownBlock
{
    /// <summary>
    /// Content hash used to decide whether a rendered block can be reused during streaming.
    /// Equality of the record itself would work but allocates on every comparison; this is
    /// computed once at parse time.
    /// </summary>
    public abstract int ContentHash { get; }
}

/// <summary>A paragraph of inline content.</summary>
public sealed record ParagraphBlock(IReadOnlyList<InlineSpan> Spans) : MarkdownBlock
{
    public override int ContentHash { get; } = ComputeHash(Spans);

    internal static int ComputeHash(IReadOnlyList<InlineSpan> spans)
    {
        var hash = new HashCode();
        hash.Add(spans.Count);
        foreach (var span in spans)
        {
            hash.Add(span.Text);
            hash.Add((int)span.Style);
            hash.Add(span.Url);
        }

        return hash.ToHashCode();
    }
}

/// <summary>A heading, levels 1-6.</summary>
public sealed record HeadingBlock(int Level, IReadOnlyList<InlineSpan> Spans) : MarkdownBlock
{
    public override int ContentHash { get; } = HashCode.Combine(Level, ParagraphBlock.ComputeHash(Spans));
}

/// <summary>
/// A fenced or indented code block, already tokenized for highlighting.
/// </summary>
/// <param name="Language">Fence info string, lower-cased. Empty when none was given.</param>
/// <param name="Code">Raw source, used verbatim by Copy.</param>
/// <param name="Lines">Highlighted tokens, one list per line.</param>
public sealed record CodeBlock(string Language, string Code, IReadOnlyList<IReadOnlyList<CodeToken>> Lines) : MarkdownBlock
{
    public override int ContentHash { get; } = HashCode.Combine(Language, Code);
}

/// <summary>A bullet or numbered list.</summary>
public sealed record ListBlock(bool IsOrdered, int StartNumber, IReadOnlyList<ListItem> Items) : MarkdownBlock
{
    public override int ContentHash { get; } = ComputeHash(IsOrdered, StartNumber, Items);

    private static int ComputeHash(bool isOrdered, int start, IReadOnlyList<ListItem> items)
    {
        var hash = new HashCode();
        hash.Add(isOrdered);
        hash.Add(start);
        foreach (var item in items)
        {
            hash.Add(item.Level);
            foreach (var block in item.Blocks)
            {
                hash.Add(block.ContentHash);
            }
        }

        return hash.ToHashCode();
    }
}

/// <summary>One list entry. Nested blocks let a list item hold a paragraph plus a code block.</summary>
/// <param name="Level">Nesting depth, zero-based, used for indentation.</param>
public sealed record ListItem(int Level, IReadOnlyList<MarkdownBlock> Blocks);

/// <summary>A block quote.</summary>
public sealed record QuoteBlock(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock
{
    public override int ContentHash { get; } = ComputeHash(Blocks);

    private static int ComputeHash(IReadOnlyList<MarkdownBlock> blocks)
    {
        var hash = new HashCode();
        foreach (var block in blocks)
        {
            hash.Add(block.ContentHash);
        }

        return hash.ToHashCode();
    }
}

/// <summary>A pipe table.</summary>
public sealed record TableBlock(
    IReadOnlyList<IReadOnlyList<InlineSpan>> Headers,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<InlineSpan>>> Rows) : MarkdownBlock
{
    public override int ContentHash { get; } = ComputeHash(Headers, Rows);

    private static int ComputeHash(
        IReadOnlyList<IReadOnlyList<InlineSpan>> headers,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<InlineSpan>>> rows)
    {
        var hash = new HashCode();
        foreach (var header in headers)
        {
            hash.Add(ParagraphBlock.ComputeHash(header));
        }

        foreach (var row in rows)
        {
            foreach (var cell in row)
            {
                hash.Add(ParagraphBlock.ComputeHash(cell));
            }
        }

        return hash.ToHashCode();
    }
}

/// <summary>A horizontal rule.</summary>
public sealed record ThematicBreakBlock : MarkdownBlock
{
    public override int ContentHash => 0x7B12A9;
}

/// <summary>Emphasis applied to a run of inline text. Flags so bold+italic is expressible.</summary>
[Flags]
public enum InlineStyle
{
    None = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Code = 1 << 2,
    Strikethrough = 1 << 3,
    Link = 1 << 4,
}

/// <summary>A styled run of text inside a paragraph, heading, list item or table cell.</summary>
/// <param name="Url">Target when <see cref="InlineStyle.Link"/> is set; otherwise null.</param>
public sealed record InlineSpan(string Text, InlineStyle Style = InlineStyle.None, string? Url = null);
