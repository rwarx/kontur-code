namespace AIClient.Domain.Entities;

/// <summary>
/// One model offered by a <see cref="Provider"/>, as discovered from its catalogue endpoint.
/// Rows are a cache: they are replaced wholesale on refresh, so nothing important may
/// depend on the surrogate <see cref="Id"/> surviving a refresh.
/// </summary>
public sealed class Model
{
    /// <summary>Surrogate key: <c>{ProviderId}:{ModelId}</c>. Stable across refreshes.</summary>
    public required string Id { get; set; }

    public required string ProviderId { get; set; }

    /// <summary>The identifier the provider's API expects, e.g. <c>anthropic/claude-sonnet-4.5</c>.</summary>
    public required string ModelId { get; set; }

    /// <summary>Display name. Falls back to <see cref="ModelId"/> when the catalogue has none.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Total context window in tokens, when advertised.</summary>
    public int? ContextWindow { get; set; }

    /// <summary>Maximum tokens the model may generate in one response, when advertised.</summary>
    public int? MaxOutputTokens { get; set; }

    public bool SupportsStreaming { get; set; } = true;
    public bool SupportsImages { get; set; }
    public bool SupportsTools { get; set; }

    /// <summary>USD per 1M prompt tokens. Null when the provider does not publish pricing.</summary>
    public decimal? PromptPricePerMillion { get; set; }

    /// <summary>USD per 1M completion tokens.</summary>
    public decimal? CompletionPricePerMillion { get; set; }

    /// <summary>
    /// Sampling parameters this model actually accepts, comma-separated
    /// (e.g. <c>temperature,top_p,max_tokens</c>). Empty means "unknown - send the defaults".
    /// Used to strip unsupported fields from the request instead of getting an HTTP 400.
    /// </summary>
    public string? SupportedParameters { get; set; }

    /// <summary>Raw catalogue JSON, kept for diagnostics and for fields we do not model yet.</summary>
    public string? RawMetadataJson { get; set; }

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public Provider? Provider { get; set; }
}
