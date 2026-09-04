using System.Text.Json;
using AIClient.Application.Configuration;
using AIClient.Application.Interfaces;
using AIClient.Domain.Entities;
using AIClient.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Repositories;

/// <summary>
/// Settings persistence, one JSON document per section.
/// </summary>
/// <remarks>
/// A section-per-row layout means adding a setting is a code change with no migration, and
/// a corrupt or unreadable section falls back to its defaults without taking the rest of
/// the configuration down with it. The in-memory tree is authoritative during a session;
/// writes go through <see cref="UpdateAsync{TSection}"/> so a change is always persisted
/// and announced together.
/// </remarks>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IDbContextFactory<AIClientDbContext> _contextFactory;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SettingsService(
        IDbContextFactory<AIClientDbContext> contextFactory,
        ILogger<SettingsService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>Defaults apply until <see cref="LoadAsync"/> replaces them.</summary>
    public AppSettings Current { get; private set; } = new();

    public event EventHandler<string>? SettingsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rows = await db.Settings
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Key, e => e.Value, cancellationToken)
            .ConfigureAwait(false);

        Current = new AppSettings
        {
            General = Deserialize<GeneralSettings>(rows, AppSettings.Keys.General),
            Appearance = Deserialize<AppearanceSettings>(rows, AppSettings.Keys.Appearance),
            Chat = Deserialize<ChatSettings>(rows, AppSettings.Keys.Chat),
            Storage = Deserialize<StorageSettings>(rows, AppSettings.Keys.Storage),
            Agent = Deserialize<AgentSettings>(rows, AppSettings.Keys.Agent),
            Canvas = Deserialize<CanvasSettings>(rows, AppSettings.Keys.Canvas),
        };

        _logger.LogInformation("Loaded {Count} settings section(s).", rows.Count);
    }

    public async Task UpdateAsync<TSection>(
        Action<TSection> mutate,
        CancellationToken cancellationToken = default)
        where TSection : class
    {
        ArgumentNullException.ThrowIfNull(mutate);

        var (section, key) = Resolve<TSection>();

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            mutate(section);
            await PersistAsync(key, section, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        // Raised outside the lock: a handler that touches settings must not deadlock.
        SettingsChanged?.Invoke(this, key);
    }

    public async Task SaveAllAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistAsync(AppSettings.Keys.General, Current.General, cancellationToken).ConfigureAwait(false);
            await PersistAsync(AppSettings.Keys.Appearance, Current.Appearance, cancellationToken).ConfigureAwait(false);
            await PersistAsync(AppSettings.Keys.Chat, Current.Chat, cancellationToken).ConfigureAwait(false);
            await PersistAsync(AppSettings.Keys.Storage, Current.Storage, cancellationToken).ConfigureAwait(false);
            await PersistAsync(AppSettings.Keys.Agent, Current.Agent, cancellationToken).ConfigureAwait(false);
            await PersistAsync(AppSettings.Keys.Canvas, Current.Canvas, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Maps a section type to the live instance and the row it is stored under.
    /// </summary>
    /// <remarks>
    /// One switch returning both halves, deliberately. Written as two switches - one for the
    /// instance and one for the key - the pairing is only correct as long as both are edited
    /// together, and a default arm on the key switch turns a forgotten edit into a section that
    /// silently overwrites another section's row.
    /// </remarks>
    private (TSection Section, string Key) Resolve<TSection>() where TSection : class
    {
        (object Section, string Key) resolved = typeof(TSection) switch
        {
            var t when t == typeof(GeneralSettings) => (Current.General, AppSettings.Keys.General),
            var t when t == typeof(AppearanceSettings) => (Current.Appearance, AppSettings.Keys.Appearance),
            var t when t == typeof(ChatSettings) => (Current.Chat, AppSettings.Keys.Chat),
            var t when t == typeof(StorageSettings) => (Current.Storage, AppSettings.Keys.Storage),
            var t when t == typeof(AgentSettings) => (Current.Agent, AppSettings.Keys.Agent),
            var t when t == typeof(CanvasSettings) => (Current.Canvas, AppSettings.Keys.Canvas),
            _ => throw new ArgumentException(
                $"'{typeof(TSection).Name}' is not a settings section.", nameof(TSection)),
        };

        return ((TSection)resolved.Section, resolved.Key);
    }

    private async Task PersistAsync(string key, object section, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(section, section.GetType(), JsonOptions);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existing = await db.Settings
            .FirstOrDefaultAsync(e => e.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.Settings.Add(new AppSettingsEntry { Key = key, Value = json });
        }
        else
        {
            existing.Value = json;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one section, falling back to defaults when it is missing or malformed.
    /// A settings file the user cannot fix by hand must never stop the app from starting.
    /// </summary>
    private TSection Deserialize<TSection>(Dictionary<string, string> rows, string key)
        where TSection : class, new()
    {
        if (!rows.TryGetValue(key, out var json) || string.IsNullOrWhiteSpace(json))
        {
            return new TSection();
        }

        try
        {
            return JsonSerializer.Deserialize<TSection>(json, JsonOptions) ?? new TSection();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Settings section '{Key}' could not be read and was reset to defaults.", key);
            return new TSection();
        }
    }
}
