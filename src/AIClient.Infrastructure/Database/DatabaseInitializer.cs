using AIClient.Domain.Entities;
using AIClient.Infrastructure.Providers.OpenAiCompatible;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Database;

/// <summary>
/// Applies migrations and seeds the provider rows on startup.
/// </summary>
/// <remarks>
/// Providers are seeded from code rather than from a migration's <c>HasData</c>. A model
/// seed would be baked into the schema, and correcting a display name would then need a new
/// migration on every user's machine. Seeding on startup is idempotent and lets a rename
/// land as a plain code change.
/// </remarks>
public sealed class DatabaseInitializer
{
    private readonly IDbContextFactory<AIClientDbContext> _contextFactory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        IDbContextFactory<AIClientDbContext> contextFactory,
        ILogger<DatabaseInitializer> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var pendingList = pending.ToList();

        if (pendingList.Count > 0)
        {
            _logger.LogInformation(
                "Applying {Count} database migration(s): {Migrations}.",
                pendingList.Count,
                string.Join(", ", pendingList));
        }

        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        await SeedProvidersAsync(db, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures a row exists for every provider the build ships with. Existing rows keep the
    /// user's choices - only the name and sort order, which the app owns, are refreshed.
    /// </summary>
    private async Task SeedProvidersAsync(AIClientDbContext db, CancellationToken cancellationToken)
    {
        var seeds = new (string Id, string Name, int SortOrder)[]
        {
            (OpenRouterProvider.ProviderId, "OpenRouter", 0),
            (NvidiaProvider.ProviderId, "NVIDIA", 1),
        };

        var existing = await db.Providers.ToDictionaryAsync(p => p.Id, cancellationToken).ConfigureAwait(false);
        var added = 0;

        foreach (var (id, name, sortOrder) in seeds)
        {
            if (existing.TryGetValue(id, out var row))
            {
                row.Name = name;
                row.SortOrder = sortOrder;
                continue;
            }

            db.Providers.Add(new Provider
            {
                Id = id,
                Name = name,
                SortOrder = sortOrder,
                IsEnabled = true,
            });

            added++;
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (added > 0)
        {
            _logger.LogInformation("Seeded {Count} provider(s).", added);
        }
    }
}
