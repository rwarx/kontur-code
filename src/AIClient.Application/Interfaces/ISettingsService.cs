using AIClient.Application.Configuration;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Single source of truth for settings. Holds the current tree in memory, persists
/// changes, and raises <see cref="SettingsChanged"/> so views react without polling.
/// </summary>
public interface ISettingsService
{
    /// <summary>The live settings. Never null; defaults are used until <see cref="LoadAsync"/> completes.</summary>
    AppSettings Current { get; }

    /// <summary>Loads all sections from storage. Called once during startup.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a change to one section and persists just that section.
    /// Taking a mutator rather than a whole object keeps concurrent edits to
    /// different sections from clobbering each other.
    /// </summary>
    Task UpdateAsync<TSection>(Action<TSection> mutate, CancellationToken cancellationToken = default)
        where TSection : class;

    /// <summary>Persists every section. Used on shutdown.</summary>
    Task SaveAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised after a section has been persisted, with the section name from <see cref="AppSettings.Keys"/>.</summary>
    event EventHandler<string>? SettingsChanged;
}
