namespace AIClient.App.ViewModels;

/// <summary>One line of a unified diff, and what it is, so the view can colour it.</summary>
public sealed record DiffLine(string Text, DiffLineKind Kind);

/// <summary>The kinds of line a diff contains.</summary>
public enum DiffLineKind
{
    /// <summary>Unchanged, shown for context.</summary>
    Context,

    /// <summary>A line the change would add.</summary>
    Added,

    /// <summary>A line the change would remove.</summary>
    Removed,

    /// <summary>A file or hunk header.</summary>
    Header,

    /// <summary>The note saying the diff was cut short.</summary>
    Notice,
}

/// <summary>
/// Turns the text of a diff into lines a template can colour.
/// </summary>
/// <remarks>
/// Shared by the approval card and the tool-call cards, which show the same kind of text for the same
/// reason. Splitting once here rather than in a per-line converter matters because these lists are
/// bound directly and never change after the text arrives.
/// </remarks>
public static class DiffLines
{
    /// <summary>
    /// Whether a piece of text is a unified diff at all.
    /// </summary>
    /// <remarks>
    /// Worth asking before colouring anything. A tool result is often ordinary text - a file listing,
    /// the contents of a file, a sentence explaining a refusal - and plenty of ordinary text has lines
    /// beginning with a hyphen. Painting a markdown bullet red as though a line were being deleted is
    /// a lie about the user's files, which is exactly what these cards exist not to tell.
    /// </remarks>
    public static bool LooksLikeDiff(string? text)
    {
        if (text is not { Length: > 0 })
        {
            return false;
        }

        var end = text.IndexOf('\n');
        var first = end < 0 ? text : text[..end];

        return first.StartsWith("@@", StringComparison.Ordinal)
            || first.StartsWith("--- ", StringComparison.Ordinal);
    }

    /// <summary>Splits diff text into tagged lines. Empty for nothing at all.</summary>
    public static IReadOnlyList<DiffLine> Split(string? text)
    {
        if (text is not { Length: > 0 })
        {
            return [];
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var tagged = new List<DiffLine>(lines.Length);

        foreach (var line in lines)
        {
            tagged.Add(new DiffLine(line, Classify(line)));
        }

        return tagged;
    }

    /// <summary>
    /// What a line of a unified diff is.
    /// </summary>
    /// <remarks>
    /// The file headers start with the same characters as an added and a removed line, so they are
    /// tested first. Getting that order wrong paints <c>+++ b/Program.cs</c> green as though the path
    /// itself were being inserted.
    /// </remarks>
    private static DiffLineKind Classify(string line)
    {
        if (line == Application.Services.TextDiff.TruncationNotice)
        {
            return DiffLineKind.Notice;
        }

        if (line.StartsWith("+++", StringComparison.Ordinal)
            || line.StartsWith("---", StringComparison.Ordinal)
            || line.StartsWith("@@", StringComparison.Ordinal))
        {
            return DiffLineKind.Header;
        }

        return line.Length == 0 ? DiffLineKind.Context : line[0] switch
        {
            '+' => DiffLineKind.Added,
            '-' => DiffLineKind.Removed,
            _ => DiffLineKind.Context,
        };
    }
}
