namespace AIClient.Application.Services;

/// <summary>
/// The questions every caller has to ask about a file before treating it as text.
/// </summary>
/// <remarks>
/// <para>
/// Two callers now need the same answers: attachments, where a renamed executable must not be
/// inlined into a prompt, and the workspace, where the agent must not read one into a transcript
/// or search one line by line. A second copy of a heuristic is a second copy that can drift, and
/// the one under discussion decides whether bytes reach a language model - so there is one.
/// </para>
/// <para>
/// Line endings are here for the same reason. A file written back with the wrong ones is a
/// whole-file diff in the user's version control, which turns a one-line edit by the agent into
/// a change nobody can review.
/// </para>
/// </remarks>
public static class TextContent
{
    /// <summary>
    /// Bytes inspected for the binary check. A UTF-8 BOM plus a header is well inside this,
    /// and reading more would not improve the verdict.
    /// </summary>
    public const int SniffLength = 8192;

    /// <summary>
    /// Share of NUL bytes above which a file is called binary. Text files contain none;
    /// a small tolerance covers UTF-16 content that slipped past encoding detection.
    /// </summary>
    public const double BinaryNulThreshold = 0.01;

    /// <summary>
    /// Reads the head of a file and reports whether its bytes are binary, whatever its
    /// extension claims.
    /// </summary>
    /// <remarks>
    /// Opened with <see cref="FileShare.ReadWrite"/> so a file held open by an editor or a build
    /// can still be examined; the alternative is refusing to read the file the user is working
    /// in, which is most of them.
    /// </remarks>
    public static async Task<bool> IsBinaryAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, SniffLength, useAsync: true);

        var buffer = new byte[(int)Math.Min(stream.Length, SniffLength)];

        if (buffer.Length == 0)
        {
            return false;
        }

        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        return LooksBinary(buffer.AsSpan(0, read));
    }

    /// <summary>The verdict itself, over bytes already in hand.</summary>
    /// <remarks>
    /// An empty span is text: an empty file is a perfectly ordinary source file, and calling it
    /// binary would hide it from every listing and search.
    /// </remarks>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        var nulCount = 0;
        foreach (var b in bytes)
        {
            if (b == 0)
            {
                nulCount++;
            }
        }

        return (double)nulCount / bytes.Length > BinaryNulThreshold;
    }

    /// <summary>
    /// The line ending a file already uses, so a rewrite can keep it.
    /// </summary>
    /// <remarks>
    /// Decided by majority rather than by first occurrence, because a mixed file - a CRLF source
    /// with one LF line pasted in - should keep the ending it mostly has. Text with no line break
    /// at all reports <see cref="Environment.NewLine"/>: there is nothing to preserve, so the
    /// platform's own is the least surprising choice.
    /// </remarks>
    public static string DominantNewline(string text)
    {
        var crlf = 0;
        var lf = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            if (i > 0 && text[i - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        if (crlf == 0 && lf == 0)
        {
            return Environment.NewLine;
        }

        return crlf >= lf ? "\r\n" : "\n";
    }

    /// <summary>
    /// Rewrites every line ending in <paramref name="text"/> as <paramref name="newline"/>.
    /// </summary>
    /// <remarks>
    /// A model writes whatever its training left it with - usually bare LF - so content arriving
    /// from a tool call is normalised on the way to disk rather than trusted.
    /// </remarks>
    public static string NormalizeNewlines(string text, string newline)
    {
        // Via LF so a CRLF input cannot become CRCRLF, which is what replacing "\n" alone does.
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        return newline == "\n" ? normalized : normalized.Replace("\n", newline, StringComparison.Ordinal);
    }
}
