using AIClient.Application.Interfaces;

namespace AIClient.Application.Services;

/// <summary>
/// Local, offline title generator.
/// </summary>
/// <remarks>
/// Deliberately not a model call. Titling every new chat with an API request costs money,
/// adds latency to the first answer, and fails when offline - for a string the user can
/// rename in one click. The heuristic: drop a leading imperative ("explain", "напиши"),
/// take the first sentence, cap the length at a word boundary, and title-case the result.
/// </remarks>
public sealed class HeuristicTitleGenerator : ITitleGenerator
{
    private const int MaxTitleLength = 48;

    /// <summary>
    /// Openers that describe the request rather than its subject. Removing them turns
    /// "Explain this WPF binding" into "This WPF Binding" -> "WPF Binding".
    /// </summary>
    private static readonly string[] LeadingVerbs =
    [
        // English
        "explain", "describe", "write", "create", "make", "generate", "show me", "show",
        "help me", "help", "tell me", "tell", "give me", "give", "how do i", "how to",
        "what is", "what are", "can you", "could you", "please", "i need", "i want",
        "implement", "build", "fix", "debug", "analyze", "analyse", "review", "summarize",
        "summarise", "translate", "convert", "refactor", "optimize", "optimise",
        // Russian
        "объясни", "опиши", "напиши", "создай", "сделай", "покажи", "помоги", "расскажи",
        "дай", "как мне", "как", "что такое", "что это", "можешь", "пожалуйста",
        "реализуй", "построй", "исправь", "проанализируй", "переведи", "преобразуй",
        "отрефактори", "оптимизируй",
    ];

    /// <summary>Fillers left dangling after a verb is stripped.</summary>
    private static readonly string[] LeadingFillers =
    [
        "me", "the", "a", "an", "this", "that", "my", "these", "those", "it",
        "мне", "этот", "эту", "это", "эти", "мой", "моя",
    ];

    public Task<string?> GenerateAsync(string firstUserMessage, CancellationToken cancellationToken = default)
        => Task.FromResult(Generate(firstUserMessage));

    /// <summary>Synchronous core, exposed for tests.</summary>
    public static string? Generate(string? firstUserMessage)
    {
        if (string.IsNullOrWhiteSpace(firstUserMessage))
        {
            return null;
        }

        var text = StripMarkdownNoise(firstUserMessage).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        text = TakeFirstSentence(text);
        text = StripLeadingWords(text, LeadingVerbs);
        text = StripLeadingWords(text, LeadingFillers);
        text = text.Trim(' ', '\t', ':', '-', '—', ',', '.', '?', '!', '"', '\'', '«', '»');

        if (text.Length == 0)
        {
            // The message was nothing but an imperative ("explain this"). Fall back to
            // the original first sentence rather than returning an empty title.
            text = TakeFirstSentence(StripMarkdownNoise(firstUserMessage).Trim());
            if (text.Length == 0)
            {
                return null;
            }
        }

        text = Truncate(text, MaxTitleLength);
        return CapitalizeFirst(text);
    }

    /// <summary>
    /// Removes fenced code, inline code and heading markers. A title made of the first
    /// line of a pasted code block is useless in the sidebar.
    /// </summary>
    private static string StripMarkdownNoise(string input)
    {
        var lines = input.Split('\n');
        var kept = new List<string>(lines.Length);
        var insideFence = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                insideFence = !insideFence;
                continue;
            }

            if (insideFence)
            {
                continue;
            }

            kept.Add(line.TrimStart('#', '>', '*', '-', ' ', '\t').Replace("`", string.Empty, StringComparison.Ordinal));
        }

        return string.Join(' ', kept.Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    private static string TakeFirstSentence(string text)
    {
        var end = text.AsSpan().IndexOfAny('.', '?', '!');
        if (end > 0 && end < text.Length - 1)
        {
            // Guard against abbreviations and decimals: a terminator must be followed by
            // whitespace to actually end a sentence.
            if (char.IsWhiteSpace(text[end + 1]))
            {
                return text[..end].Trim();
            }
        }
        else if (end > 0)
        {
            return text[..end].Trim();
        }

        var newline = text.IndexOf('\n');
        return newline > 0 ? text[..newline].Trim() : text;
    }

    private static string StripLeadingWords(string text, string[] candidates)
    {
        foreach (var candidate in candidates.OrderByDescending(c => c.Length))
        {
            if (!text.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Only strip on a word boundary, so "implementation" does not lose "implement".
            if (text.Length > candidate.Length && !IsBoundary(text[candidate.Length]))
            {
                continue;
            }

            return text[candidate.Length..].TrimStart(' ', ',', ':', '-', '\t');
        }

        return text;
    }

    private static bool IsBoundary(char c) => char.IsWhiteSpace(c) || char.IsPunctuation(c);

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var slice = text[..maxLength];
        var lastSpace = slice.LastIndexOf(' ');

        // Cut at a word boundary when one exists reasonably close to the limit.
        return lastSpace > maxLength / 2
            ? slice[..lastSpace].TrimEnd(',', '.', ':', ';', '-') + "…"
            : slice.TrimEnd() + "…";
    }

    private static string CapitalizeFirst(string text)
    {
        if (text.Length == 0 || char.IsUpper(text[0]))
        {
            return text;
        }

        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
