using System.Text;

namespace AIClient.Application.Markdown;

/// <summary>
/// Lightweight, language-aware syntax highlighter.
/// </summary>
/// <remarks>
/// A hand-written scanner rather than a regex set or a full parser. Regex highlighting
/// mis-handles the cases that matter most in chat - a <c>//</c> inside a string literal,
/// a <c>"</c> inside a comment - because it has no notion of state. This scanner walks
/// the text once, tracking whether it is inside a string or a comment, which is enough
/// to be correct for display purposes and fast enough to run per streamed code block.
///
/// It is not a compiler front-end and does not try to be: unknown languages fall back to
/// a generic C-like profile, which reads acceptably for almost everything.
/// </remarks>
public static class SyntaxHighlighter
{
    /// <summary>
    /// Guards against pathological input. A code block beyond this size is shown unhighlighted
    /// rather than freezing the UI thread; the text itself is still fully displayed.
    /// </summary>
    public const int MaxHighlightLength = 200_000;

    /// <summary>Splits source into lines of classified tokens.</summary>
    /// <param name="code">Raw source.</param>
    /// <param name="language">Fence info string, e.g. <c>csharp</c>. Unknown values use the C-like default.</param>
    public static IReadOnlyList<IReadOnlyList<CodeToken>> Highlight(string code, string? language)
    {
        if (string.IsNullOrEmpty(code))
        {
            return [];
        }

        var profile = LanguageProfiles.Resolve(language);

        if (code.Length > MaxHighlightLength || profile is null)
        {
            return SplitPlain(code);
        }

        var tokens = Scan(code, profile);
        return SplitIntoLines(tokens);
    }

    /// <summary>Single pass over the source, emitting classified runs.</summary>
    private static List<CodeToken> Scan(string code, LanguageProfile profile)
    {
        var tokens = new List<CodeToken>();
        var buffer = new StringBuilder();
        var i = 0;

        void FlushPlain()
        {
            if (buffer.Length > 0)
            {
                tokens.Add(new CodeToken(buffer.ToString(), CodeTokenKind.Plain));
                buffer.Clear();
            }
        }

        while (i < code.Length)
        {
            var c = code[i];

            // --- Comments -------------------------------------------------------
            // Checked before strings so that a quote inside a comment is inert.
            if (profile.LineComment is { } lineComment && Matches(code, i, lineComment))
            {
                FlushPlain();
                var end = code.IndexOf('\n', i);
                if (end < 0)
                {
                    end = code.Length;
                }

                tokens.Add(new CodeToken(code[i..end], CodeTokenKind.Comment));
                i = end;
                continue;
            }

            if (profile.BlockCommentStart is { } blockStart &&
                profile.BlockCommentEnd is { } blockEnd &&
                Matches(code, i, blockStart))
            {
                FlushPlain();
                var end = code.IndexOf(blockEnd, i + blockStart.Length, StringComparison.Ordinal);
                var stop = end < 0 ? code.Length : end + blockEnd.Length;
                tokens.Add(new CodeToken(code[i..stop], CodeTokenKind.Comment));
                i = stop;
                continue;
            }

            // --- Directives ------------------------------------------------------
            // Only at the start of a line, so a '#' in an expression is not misread.
            if (profile.DirectivePrefix is { } directive &&
                Matches(code, i, directive) &&
                IsAtLineStart(code, i))
            {
                FlushPlain();
                var end = code.IndexOf('\n', i);
                if (end < 0)
                {
                    end = code.Length;
                }

                tokens.Add(new CodeToken(code[i..end], CodeTokenKind.Directive));
                i = end;
                continue;
            }

            // --- Strings ---------------------------------------------------------
            if (profile.StringDelimiters.Contains(c))
            {
                FlushPlain();
                var (text, next) = ReadString(code, i, c, profile);
                tokens.Add(new CodeToken(text, CodeTokenKind.String));
                i = next;
                continue;
            }

            // --- Numbers ---------------------------------------------------------
            // A digit only starts a number when it is not part of an identifier
            // (otherwise `utf8` would highlight its '8').
            if (char.IsAsciiDigit(c) && (i == 0 || !IsIdentifierPart(code[i - 1])))
            {
                FlushPlain();
                var (text, next) = ReadNumber(code, i);
                tokens.Add(new CodeToken(text, CodeTokenKind.Number));
                i = next;
                continue;
            }

            // --- Words -----------------------------------------------------------
            if (IsIdentifierStart(c))
            {
                var start = i;
                while (i < code.Length && IsIdentifierPart(code[i]))
                {
                    i++;
                }

                var word = code[start..i];
                var kind = ClassifyWord(word, code, i, profile);

                if (kind == CodeTokenKind.Plain)
                {
                    buffer.Append(word);
                }
                else
                {
                    FlushPlain();
                    tokens.Add(new CodeToken(word, kind));
                }

                continue;
            }

            buffer.Append(c);
            i++;
        }

        FlushPlain();
        return tokens;
    }

    private static CodeTokenKind ClassifyWord(string word, string code, int wordEnd, LanguageProfile profile)
    {
        if (profile.Keywords.Contains(word))
        {
            return CodeTokenKind.Keyword;
        }

        if (profile.Types.Contains(word))
        {
            return CodeTokenKind.Type;
        }

        // An identifier directly followed by '(' reads as a call. Cheap and usually right.
        var probe = wordEnd;
        while (probe < code.Length && code[probe] == ' ')
        {
            probe++;
        }

        if (probe < code.Length && code[probe] == '(')
        {
            return CodeTokenKind.Function;
        }

        // PascalCase identifiers are types often enough in C#/Java/TS that colouring them
        // materially improves readability; the risk of a false positive is cosmetic.
        if (profile.TreatPascalCaseAsType && word.Length > 1 && char.IsAsciiLetterUpper(word[0]) &&
            word.Skip(1).Any(char.IsAsciiLetterLower))
        {
            return CodeTokenKind.Type;
        }

        return CodeTokenKind.Plain;
    }

    /// <summary>
    /// Reads a string literal, honouring escapes and stopping at a newline for languages
    /// where an unterminated literal cannot span lines. This is what keeps one stray quote
    /// from painting the rest of the file as a string.
    /// </summary>
    private static (string Text, int Next) ReadString(string code, int start, char quote, LanguageProfile profile)
    {
        var i = start + 1;

        while (i < code.Length)
        {
            var c = code[i];

            if (c == '\\' && profile.SupportsBackslashEscapes && i + 1 < code.Length)
            {
                i += 2;
                continue;
            }

            if (c == quote)
            {
                return (code[start..(i + 1)], i + 1);
            }

            if (c == '\n' && !profile.AllowsMultilineStrings)
            {
                return (code[start..i], i);
            }

            i++;
        }

        return (code[start..], code.Length);
    }

    private static (string Text, int Next) ReadNumber(string code, int start)
    {
        var i = start;

        // Hex, binary and octal prefixes.
        if (code[i] == '0' && i + 1 < code.Length && code[i + 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
        {
            i += 2;
            while (i < code.Length && (char.IsAsciiLetterOrDigit(code[i]) || code[i] == '_'))
            {
                i++;
            }

            return (code[start..i], i);
        }

        var seenDot = false;
        var seenExponent = false;

        while (i < code.Length)
        {
            var c = code[i];

            if (char.IsAsciiDigit(c) || c == '_')
            {
                i++;
            }
            else if (c == '.' && !seenDot && !seenExponent && i + 1 < code.Length && char.IsAsciiDigit(code[i + 1]))
            {
                seenDot = true;
                i++;
            }
            else if ((c is 'e' or 'E') && !seenExponent && i + 1 < code.Length &&
                     (char.IsAsciiDigit(code[i + 1]) || code[i + 1] is '+' or '-'))
            {
                seenExponent = true;
                i += 2;
            }
            else if (char.IsAsciiLetter(c))
            {
                // Numeric suffixes: 10L, 1.5f, 100u, 5n.
                i++;
                while (i < code.Length && char.IsAsciiLetter(code[i]))
                {
                    i++;
                }

                break;
            }
            else
            {
                break;
            }
        }

        return (code[start..i], i);
    }

    /// <summary>
    /// Splits token runs at newlines so the view can lay out one row per line without
    /// re-scanning. Tokens that span a newline are cut, preserving their classification.
    /// </summary>
    private static List<IReadOnlyList<CodeToken>> SplitIntoLines(List<CodeToken> tokens)
    {
        var lines = new List<IReadOnlyList<CodeToken>>();
        var current = new List<CodeToken>();

        foreach (var token in tokens)
        {
            if (!token.Text.Contains('\n'))
            {
                if (token.Text.Length > 0)
                {
                    current.Add(token);
                }

                continue;
            }

            var parts = token.Text.Split('\n');
            for (var p = 0; p < parts.Length; p++)
            {
                var part = parts[p].TrimEnd('\r');
                if (part.Length > 0)
                {
                    current.Add(new CodeToken(part, token.Kind));
                }

                if (p < parts.Length - 1)
                {
                    lines.Add(current);
                    current = [];
                }
            }
        }

        lines.Add(current);

        // A trailing newline in the source produces an empty final line that would render
        // as a blank row under the code.
        if (lines.Count > 1 && lines[^1].Count == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static List<IReadOnlyList<CodeToken>> SplitPlain(string code)
    {
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new List<IReadOnlyList<CodeToken>>(lines.Length);

        foreach (var line in lines)
        {
            result.Add(line.Length == 0 ? [] : [new CodeToken(line, CodeTokenKind.Plain)]);
        }

        if (result.Count > 1 && result[^1].Count == 0)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static bool Matches(string code, int index, string token) =>
        index + token.Length <= code.Length &&
        string.CompareOrdinal(code, index, token, 0, token.Length) == 0;

    private static bool IsAtLineStart(string code, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (code[i] == '\n')
            {
                return true;
            }

            if (code[i] is not (' ' or '\t' or '\r'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c is '_' or '$' or '@';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
}
