using AIClient.Domain.Enums;

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

    /// <summary>
    /// Tools the model may call this turn. Empty - the default - means plain chat, and the
    /// field is left out of the payload entirely, so a model or gateway that has never heard
    /// of tool calling behaves exactly as it did before this existed.
    /// </summary>
    public IReadOnlyList<AIToolDefinition> Tools { get; init; } = [];

    /// <summary>
    /// How hard to push towards a call. Ignored, and omitted from the payload, when
    /// <see cref="Tools"/> is empty.
    /// </summary>
    public AIToolChoice ToolChoice { get; init; } = AIToolChoice.Auto;
}
