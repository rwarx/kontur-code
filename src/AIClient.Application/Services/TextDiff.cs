using System.Globalization;
using System.Text;

namespace AIClient.Application.Services;

/// <summary>
/// Turns two versions of a text into the unified diff a person reads before approving a change.
/// </summary>
/// <remarks>
/// <para>
/// A summary line decides most approvals, but "overwrite 400 lines" is not a decision anyone can make
/// without seeing which 400. This is the smallest thing that answers that question. It is deliberately
/// plain: the output is read by a person in a dialog and by a model in a tool result, and both want the
/// same familiar <c>@@</c> shape rather than word-level cleverness.
/// </para>
/// <para>
/// Three bounds keep a preview from becoming its own problem. Only the part that actually differs is
/// compared, because the common head and tail are matched off first. A pair too large to compare line by
/// line degrades to "this block became that block" instead of spending the user's time on a table of a
/// hundred million cells. And the result is capped, so a rewritten file produces a reviewable extract
/// rather than a diff longer than the file.
/// </para>
/// </remarks>
public static class TextDiff
{
    /// <summary>Unchanged lines kept either side of a change, so a hunk can be read in context.</summary>
    public const int DefaultContextLines = 3;

    /// <summary>Lines of diff produced before the rest is summarised away.</summary>
    public const int DefaultMaxLines = 400;

    /// <summary>The line that stands in for everything the cap left out.</summary>
    public const string TruncationNotice = "... the rest of this change is not shown.";

    // Above this many cells the line-by-line comparison is abandoned. Chosen so the table stays in the
    // low megabytes: this runs while somebody waits for a dialog to open.
    private const long MaxCells = 1_000_000;

    private enum Change
    {
        Same,
        Removed,
        Added,
    }

    /// <summary>
    /// Describes what turning <paramref name="before"/> into <paramref name="after"/> would change, or
    /// null when it changes nothing.
    /// </summary>
    /// <param name="path">Named in the <c>---</c>/<c>+++</c> header when given.</param>
    public static string? Unified(
        string? before,
        string? after,
        string? path = null,
        int contextLines = DefaultContextLines,
        int maxLines = DefaultMaxLines)
    {
        var oldLines = Split(before);
        var newLines = Split(after);

        if (oldLines.AsSpan().SequenceEqual(newLines))
        {
            return null;
        }

        return Render(
            Compare(oldLines, newLines),
            path,
            Math.Max(0, contextLines),
            Math.Max(8, maxLines));
    }

    /// <summary>Lines in a text, counted the way the diff counts them.</summary>
    /// <remarks>
    /// Shares <see cref="Split"/> with the diff on purpose: a summary saying "42 lines become 40" beside
    /// a diff that disagreed about the count would put the user's trust in the wrong place.
    /// </remarks>
    public static int CountLines(string? text) => Split(text).Length;

    private static string[] Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalised.Split('\n');

        // A text ending in a newline splits into a trailing empty element that is not a line. Dropping it
        // also means a missing final newline never shows up as a change: the one difference this cannot
        // see, and the one no reviewer has ever wanted a hunk about.
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static List<Line> Compare(string[] oldLines, string[] newLines)
    {
        var head = 0;

        while (head < oldLines.Length
            && head < newLines.Length
            && string.Equals(oldLines[head], newLines[head], StringComparison.Ordinal))
        {
            head++;
        }

        var tail = 0;

        while (tail < oldLines.Length - head
            && tail < newLines.Length - head
            && string.Equals(oldLines[^(tail + 1)], newLines[^(tail + 1)], StringComparison.Ordinal))
        {
            tail++;
        }

        var lines = new List<Line>(oldLines.Length + newLines.Length);

        for (var i = 0; i < head; i++)
        {
            lines.Add(new Line(Change.Same, oldLines[i]));
        }

        Middle(
            oldLines[head..(oldLines.Length - tail)],
            newLines[head..(newLines.Length - tail)],
            lines);

        for (var i = oldLines.Length - tail; i < oldLines.Length; i++)
        {
            lines.Add(new Line(Change.Same, oldLines[i]));
        }

        return lines;
    }

    private static void Middle(string[] oldLines, string[] newLines, List<Line> lines)
    {
        if (oldLines.Length > 0
            && newLines.Length > 0
            && (long)(oldLines.Length + 1) * (newLines.Length + 1) <= MaxCells)
        {
            Align(oldLines, newLines, lines);
            return;
        }

        // One side is empty, or there is too much text to look for what the two have in common. Reported
        // as one block replacing another, which is true, and is what a whole-file rewrite looks like in
        // any case.
        foreach (var line in oldLines)
        {
            lines.Add(new Line(Change.Removed, line));
        }

        foreach (var line in newLines)
        {
            lines.Add(new Line(Change.Added, line));
        }
    }

    /// <summary>
    /// Matches the two sides up on their longest common subsequence of whole lines.
    /// </summary>
    /// <remarks>
    /// The textbook table rather than Myers' algorithm: the input reaching this point is the part of two
    /// files that differs, which is small in every case that matters, and the cell budget above turns the
    /// case where it is not into a block replacement. Filled from the end so the walk can go forwards,
    /// which keeps additions and removals in the order a reader expects.
    /// </remarks>
    private static void Align(string[] oldLines, string[] newLines, List<Line> lines)
    {
        var common = new int[oldLines.Length + 1, newLines.Length + 1];

        for (var i = oldLines.Length - 1; i >= 0; i--)
        {
            for (var j = newLines.Length - 1; j >= 0; j--)
            {
                common[i, j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                    ? common[i + 1, j + 1] + 1
                    : Math.Max(common[i + 1, j], common[i, j + 1]);
            }
        }

        var x = 0;
        var y = 0;

        while (x < oldLines.Length && y < newLines.Length)
        {
            if (string.Equals(oldLines[x], newLines[y], StringComparison.Ordinal))
            {
                lines.Add(new Line(Change.Same, oldLines[x]));
                x++;
                y++;
            }
            else if (common[x + 1, y] >= common[x, y + 1])
            {
                lines.Add(new Line(Change.Removed, oldLines[x]));
                x++;
            }
            else
            {
                lines.Add(new Line(Change.Added, newLines[y]));
                y++;
            }
        }

        while (x < oldLines.Length)
        {
            lines.Add(new Line(Change.Removed, oldLines[x]));
            x++;
        }

        while (y < newLines.Length)
        {
            lines.Add(new Line(Change.Added, newLines[y]));
            y++;
        }
    }

    /// <summary>
    /// Writes the compared lines out as hunks, each with its context and its <c>@@</c> header.
    /// </summary>
    /// <remarks>
    /// Two changes closer together than twice the context are put in one hunk. Emitting them separately
    /// would repeat the same lines as trailing context and then as leading context, which reads as though
    /// the file contained them twice.
    /// </remarks>
    private static string Render(List<Line> lines, string? path, int context, int maxLines)
    {
        var oldBefore = new int[lines.Count + 1];
        var newBefore = new int[lines.Count + 1];

        for (var i = 0; i < lines.Count; i++)
        {
            oldBefore[i + 1] = oldBefore[i] + (lines[i].Change == Change.Added ? 0 : 1);
            newBefore[i + 1] = newBefore[i] + (lines[i].Change == Change.Removed ? 0 : 1);
        }

        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(path))
        {
            builder.Append("--- a/").Append(path).Append('\n');
            builder.Append("+++ b/").Append(path).Append('\n');
        }

        var emitted = 0;
        var index = 0;

        while (index < lines.Count)
        {
            while (index < lines.Count && lines[index].Change == Change.Same)
            {
                index++;
            }

            if (index == lines.Count)
            {
                break;
            }

            var start = Math.Max(0, index - context);
            var end = Extend(lines, index, context);

            builder
                .Append("@@ -")
                .Append(Position(oldBefore[start], oldBefore[end] - oldBefore[start]))
                .Append(" +")
                .Append(Position(newBefore[start], newBefore[end] - newBefore[start]))
                .Append(" @@\n");

            emitted++;

            for (var i = start; i < end; i++)
            {
                if (emitted >= maxLines)
                {
                    return builder.Append(TruncationNotice).ToString();
                }

                builder.Append(Marker(lines[i].Change)).Append(lines[i].Text).Append('\n');
                emitted++;
            }

            index = end;
        }

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>Where the hunk that starts at <paramref name="change"/> ends.</summary>
    private static int Extend(List<Line> lines, int change, int context)
    {
        var end = change;

        while (end < lines.Count)
        {
            while (end < lines.Count && lines[end].Change != Change.Same)
            {
                end++;
            }

            var next = end;

            while (next < lines.Count && lines[next].Change == Change.Same)
            {
                next++;
            }

            // A run of unchanged lines short enough to be context on both sides joins the two changes
            // into one hunk. A run at the end of the file never does: there is no next change to join to.
            if (next < lines.Count && next - end <= context * 2)
            {
                end = next;
                continue;
            }

            return Math.Min(lines.Count, end + context);
        }

        return end;
    }

    /// <summary>
    /// One side of a hunk header. A count of zero points at the line the change sits after, which is
    /// what makes a pure insertion or deletion readable.
    /// </summary>
    private static string Position(int before, int count) => count switch
    {
        0 => $"{before},0",
        1 => (before + 1).ToString(CultureInfo.InvariantCulture),
        _ => $"{before + 1},{count}",
    };

    private static char Marker(Change change) => change switch
    {
        Change.Removed => '-',
        Change.Added => '+',
        _ => ' ',
    };

    private readonly record struct Line(Change Change, string Text);
}
