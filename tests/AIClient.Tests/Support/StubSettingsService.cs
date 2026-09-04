using AIClient.Application.Configuration;
using AIClient.Application.Interfaces;

namespace AIClient.Tests.Support;

/// <summary>
/// An in-memory <see cref="ISettingsService"/> for the services that only read configuration.
/// </summary>
/// <remarks>
/// The real implementation needs a database and a load pass before it answers anything, which
/// is noise in a test about reading a file or dropping an unsupported sampling parameter. The
/// behaviour that matters to those callers is exactly what this provides: a settings tree that
/// can be adjusted synchronously and an event when it changes.
/// </remarks>
public sealed class StubSettingsService : ISettingsService
{
    public AppSettings Current { get; } = new();

    public event EventHandler<string>? SettingsChanged;

    /// <summary>Adjusts a section in place, the way a test wants to arrange one.</summary>
    public StubSettingsService With<TSection>(Action<TSection> mutate)
        where TSection : class
    {
        mutate(Section<TSection>());
        return this;
    }

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateAsync<TSection>(Action<TSection> mutate, CancellationToken cancellationToken = default)
        where TSection : class
    {
        var (section, key) = Resolve<TSection>();

        mutate(section);
        SettingsChanged?.Invoke(this, key);
        return Task.CompletedTask;
    }

    public Task SaveAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private TSection Section<TSection>() where TSection : class => Resolve<TSection>().Section;

    /// <summary>
    /// Maps a section type to the live instance and its row key, in one switch.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>SettingsService.Resolve</c>, including the shape: paired in one arm so a new
    /// section cannot be given one half and not the other, and no default key so the mistake is
    /// a thrown exception rather than a write over someone else's row.
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
            _ => throw new ArgumentException($"'{typeof(TSection).Name}' is not a settings section."),
        };

        return ((TSection)resolved.Section, resolved.Key);
    }
}
