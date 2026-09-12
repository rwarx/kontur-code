using System.Net;
using AIClient.Domain.Entities;
using AIClient.Domain.Interfaces;
using AIClient.Infrastructure.Database;
using AIClient.Infrastructure.Providers.OpenAiCompatible;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Providers;

/// <summary>
/// The user-defined providers: their rows live in the database like everyone else's,
/// their implementations are built at runtime from those rows.
/// </summary>
/// <remarks>
/// <para>
/// A custom provider is a <see cref="Provider"/> row with <c>Type = "custom"</c> and a
/// base URL. The implementations are <see cref="CustomOpenAiCompatibleProvider"/> objects
/// created on demand and cached, because the set is user state rather than build state -
/// DI registers the built-ins, and this store covers everything the user adds afterwards.
/// </para>
/// <para>
/// The cache is warmed by <see cref="LoadAsync"/> (called once during startup, after the
/// database is migrated) and kept in step by <see cref="AddAsync"/> and
/// <see cref="RemoveAsync"/>. <see cref="Resolve"/> reads only the cache, which is what
/// lets <c>IProviderRegistry.GetProvider</c> stay synchronous - the chat turn path calls
/// it on the hot loop.
/// </para>
/// </remarks>
public sealed class CustomProviderStore
{
    /// <summary>Marker type distinguishing user-created rows from the seeded built-ins.</summary>
    public const string RowType = "custom";

    /// <summary>Every custom id starts with this prefix, so a built-in id can never collide.</summary>
    public const string IdPrefix = "custom-";

    private readonly IDbContextFactory<AIClientDbContext> _contextFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecureStorage _secureStorage;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CustomProviderStore> _logger;
    private readonly Dictionary<string, IAIProvider> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public CustomProviderStore(
        IDbContextFactory<AIClientDbContext> contextFactory,
        IHttpClientFactory httpClientFactory,
        ISecureStorage secureStorage,
        ILoggerFactory loggerFactory,
        ILogger<CustomProviderStore> logger)
    {
        _contextFactory = contextFactory;
        _httpClientFactory = httpClientFactory;
        _secureStorage = secureStorage;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>Whether an id names a user-created provider rather than a seeded one.</summary>
    public static bool IsCustomId(string providerId) =>
        providerId.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Loads every custom row into the cache, replacing whatever was there.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var rows = await db.Providers
                .AsNoTracking()
                .Where(p => p.Type == RowType)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            _cache.Clear();

            foreach (var row in rows)
            {
                if (row.BaseUrlOverride is not { Length: > 0 } baseUrl)
                {
                    _logger.LogWarning(
                        "Custom provider {Id} has no base URL and was not loaded.",
                        row.Id);
                    continue;
                }

                _cache[row.Id] = CreateProvider(row.Id, row.Name, baseUrl);
            }

            _logger.LogInformation("Loaded {Count} custom provider(s).", _cache.Count);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Resolves a custom provider from the cache. Null when the id is not one, or not loaded.</summary>
    public IAIProvider? Resolve(string providerId) =>
        IsCustomId(providerId) ? _cache.GetValueOrDefault(providerId) : null;

    /// <summary>
    /// Creates a custom provider row and its implementation. The id is minted here and
    /// returned, because it is the key under which the user's API key will be stored.
    /// </summary>
    public async Task<IAIProvider> AddAsync(string name, string baseUrl, CancellationToken cancellationToken = default)
    {
        var trimmedName = name.Trim();
        var normalizedUrl = NormalizeBaseUrl(baseUrl);

        if (trimmedName.Length == 0)
        {
            throw new ArgumentException("The provider needs a name.", nameof(name));
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = $"{IdPrefix}{Guid.NewGuid():N}"[..24];

            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            db.Providers.Add(new Provider
            {
                Id = id,
                Name = trimmedName,
                Type = RowType,
                BaseUrlOverride = normalizedUrl,
                IsEnabled = true,
                SortOrder = 200,
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var provider = CreateProvider(id, trimmedName, normalizedUrl);
            _cache[id] = provider;

            _logger.LogInformation("Added custom provider {Id} ({Name}).", id, trimmedName);
            return provider;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Deletes a custom provider row, its cached models (cascade) and its stored key.
    /// A built-in id is refused: those rows are the app's, not the user's.
    /// </summary>
    public async Task RemoveAsync(string providerId, CancellationToken cancellationToken = default)
    {
        if (!IsCustomId(providerId))
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' is built in and cannot be removed.");
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var row = await db.Providers
                .FirstOrDefaultAsync(p => p.Id == providerId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                return;
            }

            if (row.Type != RowType)
            {
                throw new InvalidOperationException(
                    $"Provider '{providerId}' is built in and cannot be removed.");
            }

            db.Providers.Remove(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _cache.Remove(providerId);
        }
        finally
        {
            _mutex.Release();
        }

        // The key lives outside the database; a leftover one would resurface if the user
        // ever re-added a provider that happened to mint the same id.
        await _secureStorage.DeleteAsync(providerId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Removed custom provider {Id}.", providerId);
    }

    private IAIProvider CreateProvider(string id, string name, string baseUrl) =>
        new CustomOpenAiCompatibleProvider(
            id,
            name,
            baseUrl,
            _httpClientFactory,
            _secureStorage,
            _loggerFactory.CreateLogger<CustomOpenAiCompatibleProvider>());

    /// <summary>
    /// Accepts what a user pastes: an optional trailing slash, and a missing scheme that
    /// defaults to https. Rejects anything that is not an absolute http(s) URL with a
    /// plausible host, because a malformed base URL is otherwise a confusing failure on
    /// the first request.
    /// </summary>
    private static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');

        if (trimmed.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"'{baseUrl}' is not a valid base URL.", nameof(baseUrl));
        }

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = $"https://{trimmed}";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.Host.Length == 0 ||
            // "https://not-a-url" parses with a single-label host; a base URL names a
            // machine, so require a dotted name or an explicit localhost.
            (uri.Host != "localhost" && !uri.Host.Contains('.')))
        {
            throw new ArgumentException($"'{baseUrl}' is not a valid base URL.", nameof(baseUrl));
        }

        return trimmed;
    }
}
