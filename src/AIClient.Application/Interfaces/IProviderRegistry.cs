using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Owns the provider catalogue: which providers exist, whether each is configured,
/// and the models each one offers. The model picker and Settings both read from here.
/// </summary>
public interface IProviderRegistry
{
    /// <summary>Every known provider, with its current configuration state.</summary>
    Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves a provider implementation by id. Null when the id is unknown.</summary>
    IAIProvider? GetProvider(string providerId);

    /// <summary>Cached models for one provider, read from the database rather than the network.</summary>
    Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Cached models across every enabled, configured provider, for the picker.</summary>
    Task<IReadOnlyList<ModelInfo>> GetAllModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>One model by provider and native id. Null when it is not in the cache.</summary>
    Task<ModelInfo?> GetModelAsync(string providerId, string modelId, CancellationToken cancellationToken = default);

    /// <summary>Fetches the catalogue from the provider and replaces the cached rows.</summary>
    Task<IReadOnlyList<ModelInfo>> RefreshModelsAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Stores an API key. An empty value removes it.</summary>
    Task SetApiKeyAsync(string providerId, string apiKey, CancellationToken cancellationToken = default);

    /// <summary>True when a key is stored, without decrypting it.</summary>
    Task<bool> HasApiKeyAsync(string providerId, CancellationToken cancellationToken = default);

    Task DeleteApiKeyAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Probes credentials and records the outcome for the status dot.</summary>
    Task<ProviderTestResult> TestConnectionAsync(string providerId, CancellationToken cancellationToken = default);

    Task SetEnabledAsync(string providerId, bool isEnabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when the model cache changes, so open pickers refresh themselves.
    /// </summary>
    /// <remarks>
    /// Raised on whichever thread completed the refresh, which is a background one whenever a
    /// key was saved or models were fetched. Subscribers that touch UI state own the hop onto
    /// their own thread.
    /// </remarks>
    event EventHandler<string>? ModelsChanged;
}

/// <summary>A provider plus its current state, as Settings displays it.</summary>
public sealed record ProviderInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required bool IsEnabled { get; init; }
    public required bool HasApiKey { get; init; }
    public ConnectionState ConnectionState { get; init; }
    public string? StatusMessage { get; init; }
    public int CachedModelCount { get; init; }
    public DateTimeOffset? ModelsRefreshedAt { get; init; }

    /// <summary>Where the user gets a key. Shown as a link in Settings.</summary>
    public string? ApiKeyUrl { get; init; }
}

/// <summary>A model as the picker sees it.</summary>
public sealed record ModelInfo
{
    public required string ProviderId { get; init; }
    public required string ProviderName { get; init; }

    /// <summary>Provider-native id sent in requests.</summary>
    public required string ModelId { get; init; }

    public required string Name { get; init; }
    public string? Description { get; init; }
    public int? ContextWindow { get; init; }
    public int? MaxOutputTokens { get; init; }
    public bool SupportsStreaming { get; init; }
    public bool SupportsImages { get; init; }
    public bool SupportsTools { get; init; }
    public decimal? PromptPricePerMillion { get; init; }
    public decimal? CompletionPricePerMillion { get; init; }

    /// <summary>Parameters this model accepts. Empty means unknown, in which case defaults are sent.</summary>
    public IReadOnlyList<string> SupportedParameters { get; init; } = [];

    /// <summary>
    /// True when the model is known to reject a parameter. An empty
    /// <see cref="SupportedParameters"/> means the catalogue said nothing, so the
    /// parameter is sent rather than dropped.
    /// </summary>
    public bool Supports(string parameter) =>
        SupportedParameters.Count == 0 ||
        SupportedParameters.Contains(parameter, StringComparer.OrdinalIgnoreCase);
}
