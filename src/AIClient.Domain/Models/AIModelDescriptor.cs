namespace AIClient.Domain.Models;

/// <summary>
/// A model as returned by a provider's catalogue endpoint, before it is persisted
/// as a <see cref="Entities.Model"/>. Providers return this; nothing else in the app
/// needs to know how a given catalogue is shaped.
/// </summary>
public sealed record AIModelDescriptor
{
    /// <summary>Provider-native id used in requests.</summary>
    public required string ModelId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public int? ContextWindow { get; init; }
    public int? MaxOutputTokens { get; init; }

    public bool SupportsStreaming { get; init; } = true;
    public bool SupportsImages { get; init; }
    public bool SupportsTools { get; init; }

    public decimal? PromptPricePerMillion { get; init; }
    public decimal? CompletionPricePerMillion { get; init; }

    /// <summary>Sampling parameters the model accepts. Empty means unknown.</summary>
    public IReadOnlyList<string> SupportedParameters { get; init; } = [];

    /// <summary>Original catalogue entry, kept verbatim for diagnostics.</summary>
    public string? RawMetadataJson { get; init; }
}
