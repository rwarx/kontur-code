using AIClient.Application.Configuration;
using AIClient.Domain.Entities;
using AIClient.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace AIClient.Tests;

/// <summary>
/// Sections 12 and 34: one place that owns configuration, one row per section.
/// </summary>
/// <remarks>
/// The persistence tests all read back through a second service instance over the same file,
/// because "the setting stuck" and "the setting is in the in-memory tree" are different claims
/// and only the first one survives closing the app.
/// </remarks>
public sealed class SettingsServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await TestDatabase.CreateAsync();

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task An_empty_database_yields_a_complete_tree_of_defaults()
    {
        // Nothing has been written yet, and the app still has to start with usable values.
        var service = _db.Settings();

        await service.LoadAsync();

        Assert.Equal(0.7, service.Current.Chat.Temperature);
        Assert.Null(service.Current.Chat.TopP);
        Assert.Equal(ThemeMode.System, service.Current.Appearance.Theme);
        Assert.True(service.Current.General.AutoGenerateTitles);
        Assert.Equal(1024L * 1024, service.Current.Storage.MaxAttachmentBytes);
    }

    [Fact]
    public async Task A_change_survives_a_restart()
    {
        var first = _db.Settings();
        await first.LoadAsync();

        await first.UpdateAsync<ChatSettings>(chat =>
        {
            chat.Temperature = 0.2;
            chat.MaxTokens = 2048;
            chat.DefaultModelId = "openai/gpt-5-mini";
        });

        var second = _db.Settings();
        await second.LoadAsync();

        Assert.Equal(0.2, second.Current.Chat.Temperature);
        Assert.Equal(2048, second.Current.Chat.MaxTokens);
        Assert.Equal("openai/gpt-5-mini", second.Current.Chat.DefaultModelId);
    }

    [Fact]
    public async Task An_explicit_null_is_persisted_as_a_null_and_not_as_a_default()
    {
        // Null means "do not send this parameter". If a reload turned it back into 0.7 the
        // user could never stop the app from sending a temperature.
        var first = _db.Settings();
        await first.LoadAsync();

        await first.UpdateAsync<ChatSettings>(chat => chat.Temperature = null);

        var second = _db.Settings();
        await second.LoadAsync();

        Assert.Null(second.Current.Chat.Temperature);
    }

    [Fact]
    public async Task Writing_one_section_leaves_the_others_untouched()
    {
        var service = _db.Settings();
        await service.LoadAsync();

        await service.UpdateAsync<AppearanceSettings>(a => a.Theme = ThemeMode.Dark);
        await service.UpdateAsync<GeneralSettings>(g => g.ConfirmBeforeDelete = false);

        var reloaded = _db.Settings();
        await reloaded.LoadAsync();

        Assert.Equal(ThemeMode.Dark, reloaded.Current.Appearance.Theme);
        Assert.False(reloaded.Current.General.ConfirmBeforeDelete);

        // The section nobody touched is still at its defaults rather than blank.
        Assert.Equal(0.7, reloaded.Current.Chat.Temperature);
    }

    [Fact]
    public async Task An_enum_and_a_guid_both_round_trip()
    {
        var id = Guid.CreateVersion7();
        var service = _db.Settings();
        await service.LoadAsync();

        await service.UpdateAsync<AppearanceSettings>(a => a.Theme = ThemeMode.Light);
        await service.UpdateAsync<GeneralSettings>(g =>
        {
            g.LastConversationId = id;
            g.HasCompletedFirstRun = true;
        });

        var reloaded = _db.Settings();
        await reloaded.LoadAsync();

        // Restoring the last conversation on launch depends on both of these surviving.
        Assert.Equal(ThemeMode.Light, reloaded.Current.Appearance.Theme);
        Assert.Equal(id, reloaded.Current.General.LastConversationId);
        Assert.True(reloaded.Current.General.HasCompletedFirstRun);
    }

    [Fact]
    public async Task Each_section_is_stored_under_its_own_stable_key()
    {
        var service = _db.Settings();
        await service.LoadAsync();

        await service.SaveAllAsync();

        await using var db = _db.CreateDbContext();
        var keys = await db.Settings.Select(e => e.Key).OrderBy(k => k).ToListAsync();

        // These keys are the primary key of the table. Renaming one silently resets that
        // section for every existing installation.
        Assert.Equal(["appearance", "chat", "general", "storage"], keys);
    }

    [Fact]
    public async Task Saving_twice_updates_the_row_rather_than_adding_another()
    {
        var service = _db.Settings();
        await service.LoadAsync();

        await service.UpdateAsync<ChatSettings>(c => c.Temperature = 0.1);
        await service.UpdateAsync<ChatSettings>(c => c.Temperature = 0.9);

        await using var db = _db.CreateDbContext();

        Assert.Equal(1, await db.Settings.CountAsync(e => e.Key == AppSettings.Keys.Chat));
    }

    [Fact]
    public async Task The_stored_json_uses_the_camel_case_names_the_reader_expects()
    {
        // Serialising with one naming policy and reading with another is a silent reset:
        // every value falls back to its default and nothing logs an error.
        var service = _db.Settings();
        await service.LoadAsync();

        await service.UpdateAsync<ChatSettings>(c => c.Temperature = 0.35);

        await using var db = _db.CreateDbContext();
        var json = await db.Settings.Where(e => e.Key == AppSettings.Keys.Chat).Select(e => e.Value).SingleAsync();

        Assert.Contains("\"temperature\":0.35", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_section_written_by_an_older_version_keeps_its_values_and_defaults_the_rest()
    {
        // What an upgrade looks like: the row on disk predates two of today's settings.
        await WriteRawAsync(AppSettings.Keys.Chat, """{"temperature":0.15,"sendWithEnter":false}""");

        var service = _db.Settings();
        await service.LoadAsync();

        Assert.Equal(0.15, service.Current.Chat.Temperature);
        Assert.False(service.Current.Chat.SendWithEnter);
        Assert.Equal(100, service.Current.Chat.MaxHistoryMessages);
        Assert.Equal(300, service.Current.Chat.RequestTimeoutSeconds);
    }

    [Fact]
    public async Task A_corrupt_section_falls_back_to_defaults_without_taking_the_others_down()
    {
        // Hand-edited or half-written rows happen. Refusing to start over one is not an option.
        await WriteRawAsync(AppSettings.Keys.Chat, "{ this is not json");
        await WriteRawAsync(AppSettings.Keys.Appearance, """{"theme":2,"chatFontSize":18}""");

        var service = _db.Settings();
        await service.LoadAsync();

        Assert.Equal(0.7, service.Current.Chat.Temperature);
        Assert.Equal(ThemeMode.Dark, service.Current.Appearance.Theme);
        Assert.Equal(18d, service.Current.Appearance.ChatFontSize);
    }

    [Fact]
    public async Task An_empty_section_value_is_treated_as_absent()
    {
        await WriteRawAsync(AppSettings.Keys.Storage, "   ");

        var service = _db.Settings();
        await service.LoadAsync();

        Assert.Equal(120_000, service.Current.Storage.MaxAttachmentCharacters);
    }

    [Fact]
    public async Task A_change_is_announced_with_the_key_of_the_section_that_changed()
    {
        var service = _db.Settings();
        await service.LoadAsync();

        var announced = new List<string>();
        service.SettingsChanged += (_, key) => announced.Add(key);

        await service.UpdateAsync<AppearanceSettings>(a => a.Theme = ThemeMode.Dark);
        await service.UpdateAsync<ChatSettings>(c => c.AutoScroll = false);

        Assert.Equal([AppSettings.Keys.Appearance, AppSettings.Keys.Chat], announced);
    }

    [Fact]
    public async Task The_new_value_is_already_visible_when_the_change_is_announced()
    {
        // The theme service reads Current from inside this handler. If the event fired before
        // the mutation it would apply the old theme.
        var service = _db.Settings();
        await service.LoadAsync();

        ThemeMode? observed = null;
        service.SettingsChanged += (_, _) => observed = service.Current.Appearance.Theme;

        await service.UpdateAsync<AppearanceSettings>(a => a.Theme = ThemeMode.Dark);

        Assert.Equal(ThemeMode.Dark, observed);
    }

    [Fact]
    public async Task A_handler_that_writes_another_setting_does_not_deadlock()
    {
        // The event is deliberately raised outside the mutex. A re-entrant write from a
        // handler is exactly the shape that would hang if it were not.
        var service = _db.Settings();
        await service.LoadAsync();

        var reentrant = new TaskCompletionSource();

        service.SettingsChanged += (_, key) =>
        {
            if (key != AppSettings.Keys.Appearance)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await service.UpdateAsync<GeneralSettings>(g => g.RestoreLastConversation = false);
                    reentrant.TrySetResult();
                }
                catch (Exception ex)
                {
                    reentrant.TrySetException(ex);
                }
            });
        };

        await service.UpdateAsync<AppearanceSettings>(a => a.Theme = ThemeMode.Dark);

        var completed = await Task.WhenAny(reentrant.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(reentrant.Task, completed);
        await reentrant.Task;
        Assert.False(service.Current.General.RestoreLastConversation);
    }

    [Fact]
    public async Task Concurrent_writers_all_land()
    {
        // Two view models can save at the same time - the settings window and a keyboard
        // shortcut, say - and neither may lose its write or throw.
        var service = _db.Settings();
        await service.LoadAsync();

        await Task.WhenAll(
            service.UpdateAsync<ChatSettings>(c => c.Temperature = 0.4),
            service.UpdateAsync<GeneralSettings>(g => g.ConfirmBeforeDelete = false),
            service.UpdateAsync<AppearanceSettings>(a => a.ChatFontSize = 16),
            service.UpdateAsync<StorageSettings>(s => s.LogRetentionDays = 30));

        var reloaded = _db.Settings();
        await reloaded.LoadAsync();

        Assert.Equal(0.4, reloaded.Current.Chat.Temperature);
        Assert.False(reloaded.Current.General.ConfirmBeforeDelete);
        Assert.Equal(16d, reloaded.Current.Appearance.ChatFontSize);
        Assert.Equal(30, reloaded.Current.Storage.LogRetentionDays);
    }

    [Fact]
    public async Task Asking_for_a_type_that_is_not_a_section_is_a_programming_error()
    {
        var service = _db.Settings();
        await service.LoadAsync();

        // Caught at the seam rather than silently writing a fifth row nobody reads.
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateAsync<AppSettings>(_ => { }));
    }

    [Fact]
    public async Task A_null_mutation_is_rejected()
    {
        var service = _db.Settings();
        await service.LoadAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.UpdateAsync<ChatSettings>(null!));
    }

    private async Task WriteRawAsync(string key, string value)
    {
        await using var db = _db.CreateDbContext();

        db.Settings.Add(new AppSettingsEntry { Key = key, Value = value });

        await db.SaveChangesAsync();
    }
}
