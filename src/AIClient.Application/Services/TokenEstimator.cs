namespace AIClient.Application.Services;

/// <summary>
/// Approximates token counts without shipping a tokenizer per model family.
/// </summary>
/// <remarks>
/// Every provider uses a different vocabulary, so an exact count is impossible client-side.
/// This estimate exists only to decide how much history fits, and it deliberately
/// over-estimates: sending slightly less history is harmless, while under-estimating
/// produces an HTTP 400 the user has to recover from.
///
/// Ratios come from the observed behaviour of BPE tokenizers: roughly 4 characters per
/// token for Latin prose, closer to 2 for Cyrillic and CJK, which fragment into more
/// tokens per character.
/// </remarks>
public static class TokenEstimator
{
    private const double LatinCharsPerToken = 3.6;
    private const double WideCharsPerToken = 1.8;

    /// <summary>Per-message overhead for role markers and delimiters in the chat template.</summary>
    public const int PerMessageOverhead = 4;

    /// <summary>Estimates the token count of a string, rounding up.</summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var wide = 0;
        var latin = 0;

        foreach (var c in text)
        {
            // Cyrillic, CJK, Hangul, Hiragana/Katakana and most non-Latin scripts cost
            // materially more tokens per character than ASCII does.
            if (c > 0x024F)
            {
                wide++;
            }
            else
            {
                latin++;
            }
        }

        var estimate = (latin / LatinCharsPerToken) + (wide / WideCharsPerToken);
        return (int)Math.Ceiling(estimate);
    }

    /// <summary>Estimates a message including its chat-template overhead.</summary>
    public static int EstimateMessage(string? content) => Estimate(content) + PerMessageOverhead;
}
