using AIClient.Application.Interfaces;
using AIClient.Domain.Entities;
using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Infrastructure.Database;
using AIClient.Infrastructure.Providers.OpenAiCompatible;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Providers;

/// <summary>
/// The single place that knows which providers exist, which are usable, and what models
/// each one offers.
/// </summary>
/// <remarks>
/// Models are served from SQLite, not from the network. Opening the picker must not cost an
/// HTTP round trip, and the app has to keep working offline (section 31) - a cached
/// catalogue means the last known model list is still there with no connection. The network
/// is touched only by <see cref="RefreshModelsAsync"/> and <see cref="TestConnectionAsync"/>,
/// both of which the user triggers.
///
/// Connection state is deliberately in memory rather than in the database. It describes this
/// session's last probe; persisting "Connected" from yesterday would show a green dot for a
/// key that has since been revoked.
/// </remarks>
public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly IDbContextFactory<AIClientDbContext> _contextFactory;
    private readonly ISecureStorage _secureStorage;
    private readonly ILogger<ProviderRegistry> _logger;
    private readonly Dictionary<string, IAIProvider> _providers;
    private readonly Dictionary<string, ProviderStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshMutex = new(1, 1);

    public ProviderRegistry(
        IDbContextFactory<AIClientDbContext> contextFactory,
        ISecureStorage secureStorage,
        IEnumerable<IAIProvider> providers,
        ILogger<ProviderRegistry> logger)
    {
        _contextFactory = contextFactory;
        _secureStorage = secureStorage;
        _logger = logger;
        _providers = providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
    }

    public event EventHandler<string>? ModelsChanged;

    public IAIProvider? GetProvider(string providerId) =>
        _providers.GetValueOrDefault(providerId);

    public async Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rows = await db.Providers
            .AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.IsEnabled,
                p.ModelsRefreshedAt,
                ModelCount = p.Models.Count,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new List<ProviderInfo>(rows.Count);

        foreach (var row in rows)
        {
            // Only the presence of a key is checked here - it is never decrypted for display.
            var hasKey = await _secureStorage.ContainsAsync(row.Id, cancellationToken).ConfigureAwait(false);
            var status = _status.GetValueOrDefault(row.Id);

            result.Add(new ProviderInfo
            {
                Id = row.Id,
                Name = row.Name,
                IsEnabled = row.IsEnabled,
                HasApiKey = hasKey,
                ConnectionState = hasKey
                    ? status?.State ?? ConnectionState.Unknown
                    : ConnectionState.NotConfigured,
                StatusMessage = hasKey ? status?.Message : null,
                CachedModelCount = row.ModelCount,
                ModelsRefreshedAt = row.ModelsRefreshedAt,
                ApiKeyUrl = ResolveApiKeyUrl(row.Id),
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await QueryModels(db, m => m.ProviderId == providerId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ModelInfo>> GetAllModelsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // A disabled provider's models stay in the database but never reach the picker.
        // Filtered before projection, so the predicate translates to SQL.
        return await QueryModels(db, m => m.Provider!.IsEnabled)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ModelInfo?> GetModelAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await QueryModels(db, m => m.ProviderId == providerId && m.ModelId == modelId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ModelInfo>> RefreshModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(providerId)
            ?? throw new InvalidOperationException($"Unknown provider '{providerId}'.");

        // The catalogue fetch is outside the lock: it is the slow part, and a failure must
        // not leave the cache half-written.
        var descriptors = await provider.GetModelsAsync(cancellationToken).ConfigureAwait(false);

        await _refreshMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var existing = await db.Models
                .Where(m => m.ProviderId == providerId)
                .ToDictionaryAsync(m => m.ModelId, cancellationToken)
                .ConfigureAwait(false);

            foreach (var descriptor in descriptors)
            {
                if (existing.Remove(descriptor.ModelId, out var row))
                {
                    Apply(row, descriptor, now);
                }
                else
                {
                    db.Models.Add(ToEntity(providerId, descriptor, now));
                }
            }

            // Whatever the catalogue no longer lists has been retired upstream.
            if (existing.Count > 0)
            {
                db.Models.RemoveRange(existing.Values);
            }

            await db.Providers
                .Where(p => p.Id == providerId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ModelsRefreshedAt, now), cancellationToken)
                .ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Refreshed {Provider}: {Added} model(s) cached, {Removed} retired.",
                providerId,
                descriptors.Count,
                existing.Count);
        }
        finally
        {
            _refreshMutex.Release();
        }

        SetStatus(providerId, ConnectionState.Connected, $"{descriptors.Count} models available.");
        ModelsChanged?.Invoke(this, providerId);

        return await GetModelsAsync(providerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetApiKeyAsync(
        string providerId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await DeleteApiKeyAsync(providerId, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Pasted keys routinely carry stray whitespace, which produces a confusing 401.
        await _secureStorage.SetAsync(providerId, apiKey.Trim(), cancellationToken).ConfigureAwait(false);

        // The stored value is not logged, and neither is its length.
        _logger.LogInformation("API key updated for provider {Provider}.", providerId);

        SetStatus(providerId, ConnectionState.Unknown, null);
    }

    public Task<bool> HasApiKeyAsync(string providerId, CancellationToken cancellationToken = default) =>
        _secureStorage.ContainsAsync(providerId, cancellationToken);

    public async Task DeleteApiKeyAsync(string providerId, CancellationToken cancellationToken = default)
    {
        await _secureStorage.DeleteAsync(providerId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("API key removed for provider {Provider}.", providerId);

        SetStatus(providerId, ConnectionState.NotConfigured, null);
    }

    public async Task<ProviderTestResult> TestConnectionAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(providerId);

        if (provider is null)
        {
            return new ProviderTestResult(false, $"Unknown provider '{providerId}'.");
        }

        SetStatus(providerId, ConnectionState.Testing, "Testing…");

        var result = await provider.TestConnectionAsync(cancellationToken).ConfigureAwait(false);

        SetStatus(
            providerId,
            result.Success ? ConnectionState.Connected : ConnectionState.Failed,
            result.Message);

        return result;
    }

    public async Task SetEnabledAsync(
        string providerId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        await db.Providers
            .Where(p => p.Id == providerId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsEnabled, isEnabled), cancellationToken)
            .ConfigureAwait(false);

        // Enabling or disabling changes which models the picker may show.
        ModelsChanged?.Invoke(this, providerId);
    }

    /// <summary>
    /// Projection shared by every read. The filter is applied to the entity before the
    /// projection so it translates to SQL and can reach navigation properties that the DTO
    /// does not carry. <c>AsNoTracking</c> plus a DTO projection keeps entities out of the
    /// ViewModels and means the change tracker never sees these rows.
    /// </summary>
    private static IQueryable<ModelInfo> QueryModels(
        AIClientDbContext db,
        System.Linq.Expressions.Expression<Func<Model, bool>> predicate) =>
        db.Models
            .AsNoTracking()
            .Where(predicate)
            .OrderBy(m => m.Provider!.SortOrder)
            .ThenBy(m => m.Name)
            .Select(m => new ModelInfo
            {
                ProviderId = m.ProviderId,
                ProviderName = m.Provider!.Name,
                ModelId = m.ModelId,
                Name = m.Name,
                Description = m.Description,
                ContextWindow = m.ContextWindow,
                MaxOutputTokens = m.MaxOutputTokens,
                SupportsStreaming = m.SupportsStreaming,
                SupportsImages = m.SupportsImages,
                SupportsTools = m.SupportsTools,
                PromptPricePerMillion = m.PromptPricePerMillion,
                CompletionPricePerMillion = m.CompletionPricePerMillion,
                SupportedParameters = m.SupportedParameters == null || m.SupportedParameters.Length == 0
                    ? new List<string>()
                    : m.SupportedParameters.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
            });

    private static Model ToEntity(string providerId, Domain.Models.AIModelDescriptor descriptor, DateTimeOffset now)
    {
        var entity = new Model
        {
            Id = $"{providerId}:{descriptor.ModelId}",
            ProviderId = providerId,
            ModelId = descriptor.ModelId,
            Name = descriptor.Name,
        };

        Apply(entity, descriptor, now);
        return entity;
    }

    private static void Apply(Model entity, Domain.Models.AIModelDescriptor descriptor, DateTimeOffset now)
    {
        entity.Name = descriptor.Name;
        entity.Description = descriptor.Description;
        entity.ContextWindow = descriptor.ContextWindow;
        entity.MaxOutputTokens = descriptor.MaxOutputTokens;
        entity.SupportsStreaming = descriptor.SupportsStreaming;
        entity.SupportsImages = descriptor.SupportsImages;
        entity.SupportsTools = descriptor.SupportsTools;
        entity.PromptPricePerMillion = descriptor.PromptPricePerMillion;
        entity.CompletionPricePerMillion = descriptor.CompletionPricePerMillion;
        entity.SupportedParameters = descriptor.SupportedParameters.Count == 0
            ? null
            : string.Join(',', descriptor.SupportedParameters);
        entity.RawMetadataJson = descriptor.RawMetadataJson;
        entity.LastSeenAt = now;
    }

    private void SetStatus(string providerId, ConnectionState state, string? message) =>
        _status[providerId] = new ProviderStatus(state, message);

    /// <summary>Sign-up page for a key. Keeps the URL out of the ViewModels.</summary>
    private static string? ResolveApiKeyUrl(string providerId) => providerId switch
    {
        OpenRouterProvider.ProviderId => OpenRouterProvider.ApiKeyUrl,
        NvidiaProvider.ProviderId => NvidiaProvider.ApiKeyUrl,
        _ => null,
    };

    private sealed record ProviderStatus(ConnectionState State, string? Message);
}
