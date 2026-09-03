namespace AIClient.Application.Configuration;

/// <summary>
/// Chat behaviour and default sampling parameters.
/// Sampling values are nullable: null means "do not send this parameter at all",
/// which is how the app stays compatible with models that reject a given field.
/// </summary>
public sealed class ChatSettings
{
    public string? DefaultProviderId { get; set; }

    /// <summary>Provider-native model id used for new chats.</summary>
    public string? DefaultModelId { get; set; }

    /// <summary>System prompt for new conversations. Null or empty omits the system turn.</summary>
    public string? SystemPrompt { get; set; }

    public double? Temperature { get; set; } = 0.7;
    public double? TopP { get; set; }
    public int? MaxTokens { get; set; }

    /// <summary>Enter sends and Shift+Enter inserts a newline. When false the two are swapped.</summary>
    public bool SendWithEnter { get; set; } = true;

    public bool RenderMarkdown { get; set; } = true;
    public bool HighlightCode { get; set; } = true;

    /// <summary>Follow the bottom of the transcript while streaming, until the user scrolls up.</summary>
    public bool AutoScroll { get; set; } = true;

    /// <summary>Show the per-message token counts and generation time.</summary>
    public bool ShowTokenUsage { get; set; } = true;

    /// <summary>
    /// Turns kept when the model's context window is unknown. Prevents an unbounded
    /// history from being sent to a model whose limit we cannot check.
    /// </summary>
    public int MaxHistoryMessages { get; set; } = 100;

    /// <summary>Tokens held back for the answer when trimming history.</summary>
    public int ReservedOutputTokens { get; set; } = 1024;

    /// <summary>Whole-request timeout in seconds, streaming included.</summary>
    public int RequestTimeoutSeconds { get; set; } = 300;
}
