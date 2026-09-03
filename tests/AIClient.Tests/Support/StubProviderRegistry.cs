using AIClient.Application.Interfaces;
using AIClient.Domain.Interfaces;

namespace AIClient.Tests.Support;

/// <summary>
/// A registry backed by lists rather than a database.
/// </summary>
/// <remarks>
/// Used by the <c>ChatService</c> tests, whose subject is the turn pipeline, not the
/// catalogue. The real <c>ProviderRegistry</c> is exercised against real SQLite in
/// <c>ModelRegistryTests</c>; faking it here keeps a parameter-dropping test from also
/// depending on how models are persisted.
/// </remarks>
public sealed class StubProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, IAIProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ModelInfo> _models = [];

    public StubProviderRegistry(params IAIProvider[] providers)
    {
        foreach (var provider in providers)
        {
            _providers[provider.Id] = provider;
        }
    }

    /// <summary>Publishes a model so <see cref="GetModelAsync"/> can find it.</summary>
    public StubProviderRegistry WithModel(ModelInfo model)
    {
        _models.Add(model);
        return this;
    }

    /// <summary>Publishes a model with the given capabilities, filling in the boring parts.</summary>
    public StubProviderRegistry WithModel(
        string providerId,
        string modelId,
        int? contextWindow = null,
        int? maxOutputTokens = null,
        bool supportsStreaming = true,
        params string[] supportedParameters) =>
        WithModel(new ModelInfo
        {
            ProviderId = providerId,
            ProviderName = providerId,
            ModelId = modelId,
            Name = modelId,
            ContextWindow = contextWindow,
            MaxOutputTokens = maxOutputTokens,
            SupportsStreaming = supportsStreaming,
            SupportedParameters = supportedParameters,
        });

    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderInfo>>(_providers.Values
            .Select(p => new ProviderInfo
            {
                Id = p.Id,
                Name = p.DisplayName,
                IsEnabled = true,
                HasApiKey = true,
            })
            .ToList());

    public IAIProvider? GetProvider(string providerId) => _providers.GetValueOrDefault(providerId);

    public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string providerId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModelInfo>>(
            _models.Where(m => m.ProviderId == providerId).ToList());

    public Task<IReadOnlyList<ModelInfo>> GetAllModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModelInfo>>(_models.ToList());

    public Task<ModelInfo?> GetModelAsync(string providerId, string modelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_models.FirstOrDefault(m => m.ProviderId == providerId && m.ModelId == modelId));

    public Task<IReadOnlyList<ModelInfo>> RefreshModelsAsync(string providerId, CancellationToken cancellationToken = default) =>
        GetModelsAsync(providerId, cancellationToken);

    public Task SetApiKeyAsync(string providerId, string apiKey, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<bool> HasApiKeyAsync(string providerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task DeleteApiKeyAsync(string providerId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<ProviderTestResult> TestConnectionAsync(string providerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderTestResult(true, "OK"));

    public Task SetEnabledAsync(string providerId, bool isEnabled, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public event EventHandler<string>? ModelsChanged;

    /// <summary>Kept so the event is not merely declared; nothing in these tests subscribes.</summary>
    public void RaiseModelsChanged(string providerId) => ModelsChanged?.Invoke(this, providerId);
}
