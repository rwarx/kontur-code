namespace AIClient.Domain.Models;

/// <summary>
/// A fully-built request, ready for a provider to translate into its wire format.
/// Sampling parameters are nullable on purpose: null means "do not send this field",
/// which is how the app avoids HTTP 400s from models that reject a parameter outright.
/// </summary>
public sealed record AIChatRequest
{
    /// <summary>Provider-native model id, e.g. <c>anthropic/claude-sonnet-4.5</c>.</summary>
    public required string ModelId { get; init; }

    /// <summary>Full conversation, system prompt first. Never just the latest turn.</summary>
    public required IReadOnlyList<AIChatMessage> Messages { get; init; }

    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? MaxTokens { get; init; }

    /// <summary>When false the provider must issue a non-streaming request.</summary>
    public bool Stream { get; init; } = true;
}
