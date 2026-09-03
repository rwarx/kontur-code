using AIClient.Domain.Entities;
using AIClient.Domain.Enums;
using AIClient.Infrastructure.Database;
using AIClient.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIClient.Tests;

/// <summary>
/// Schema, migrations and the type mapping that turns a <see cref="DateTimeOffset"/> into
/// something SQLite can sort.
/// </summary>
/// <remarks>
/// The timestamp tests exist because of a real failure: SQLite has no date type, so
/// <c>ORDER BY</c> over a <see cref="DateTimeOffset"/> is untranslatable and the sidebar query
/// threw on launch. The fix was a global value converter to UTC ticks. These tests pin down
/// both halves of it - the column really is an integer, and ordering really happens in SQL -
/// so the next person to add a timestamp column finds out at test time rather than at launch.
/// </remarks>
public sealed class DatabaseTests : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await TestDatabase.CreateAsync();

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Initialization_applies_migrations_and_leaves_none_pending()
    {
        await using var context = _db.CreateDbContext();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        var pending = await context.Database.GetPendingMigrationsAsync();

        Assert.NotEmpty(applied);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task Every_timestamp_column_is_stored_as_an_integer()
    {
        // Walked from the model rather than listed by hand, so a timestamp column added
        // later is covered without anyone remembering to extend this test.
        await using var context = _db.CreateDbContext();

        var timestampColumns = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?))
                .Select(p => (Table: entity.GetTableName()!, Column: p.GetColumnName())))
            .ToList();

        Assert.NotEmpty(timestampColumns);

        foreach (var (table, column) in timestampColumns)
        {
            var types = await ColumnTypesAsync(table);

            Assert.True(types.ContainsKey(column), $"{table}.{column} is missing from the database.");
            Assert.Equal("INTEGER", types[column]);
        }
    }

    [Fact]
    public async Task Ordering_by_a_timestamp_is_translated_to_SQL()
    {
        await using var context = _db.CreateDbContext();

        var query = context.Conversations
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.UpdatedAt);

        // EF Core does not silently fall back to client evaluation: if the ORDER BY is in the
        // generated SQL, the translation the sidebar depends on works.
        Assert.Contains("ORDER BY", query.ToQueryString(), StringComparison.Ordinal);

        // And it has to actually run. The original bug threw here, not at translation time.
        var rows = await query.ToListAsync();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Aggregating_over_a_timestamp_is_translated_to_SQL()
    {
        await using var context = _db.CreateDbContext();

        // MIN/MAX over a date was the second thing SQLite refused. Exercised separately
        // because it fails independently of ORDER BY.
        var newest = await context.Conversations.MaxAsync(c => (DateTimeOffset?)c.UpdatedAt);

        Assert.Null(newest);
    }

    [Fact]
    public async Task Timestamps_written_in_different_offsets_sort_by_instant()
    {
        // 10:00+05:00 is 05:00 UTC; 09:00-05:00 is 14:00 UTC. Local wall-clock order and
        // instant order disagree, which is exactly what a naive text or local-time column
        // gets wrong - and what a user crossing a time zone would hit.
        var earlierInstant = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.FromHours(5));
        var laterInstant = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.FromHours(-5));

        Assert.True(laterInstant.UtcDateTime > earlierInstant.UtcDateTime);

        await using (var seed = _db.CreateDbContext())
        {
            seed.Conversations.Add(new Conversation
            {
                Title = "Written in +05:00",
                CreatedAt = earlierInstant,
                UpdatedAt = earlierInstant,
            });

            seed.Conversations.Add(new Conversation
            {
                Title = "Written in -05:00",
                CreatedAt = laterInstant,
                UpdatedAt = laterInstant,
            });

            await seed.SaveChangesAsync();
        }

        var summaries = await _db.Conversations().GetSummariesAsync();

        Assert.Equal(["Written in -05:00", "Written in +05:00"], summaries.Select(s => s.Title));
    }

    [Fact]
    public async Task A_timestamp_round_trips_to_the_same_instant()
    {
        var written = new DateTimeOffset(2026, 7, 4, 12, 34, 56, 789, TimeSpan.FromHours(3));
        var id = Guid.CreateVersion7();

        await using (var seed = _db.CreateDbContext())
        {
            seed.Conversations.Add(new Conversation
            {
                Id = id,
                Title = "Timestamps",
                CreatedAt = written,
                UpdatedAt = written,
            });

            await seed.SaveChangesAsync();
        }

        await using var read = _db.CreateDbContext();
        var loaded = await read.Conversations.SingleAsync(c => c.Id == id);

        // The instant survives exactly. The original offset does not, by design: the column
        // holds UTC ticks, and no part of the app needs to know which zone a row was written
        // in - only when it happened.
        Assert.Equal(written.UtcTicks, loaded.CreatedAt.UtcTicks);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAt.Offset);
    }

    [Fact]
    public async Task A_nullable_timestamp_round_trips_as_null()
    {
        // The pre-convention converter has to cover DateTimeOffset? as well as
        // DateTimeOffset; missing the nullable variant is an easy mistake to make.
        await using var context = _db.CreateDbContext();

        var provider = await context.Providers.SingleAsync(p => p.Id == "openrouter");

        Assert.Null(provider.ModelsRefreshedAt);
    }

    [Fact]
    public async Task Deleting_a_conversation_cascades_to_messages_and_attachments()
    {
        var conversationId = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        await using (var seed = _db.CreateDbContext())
        {
            seed.Conversations.Add(new Conversation
            {
                Id = conversationId,
                Title = "Cascade",
                Messages =
                [
                    new Message
                    {
                        Id = messageId,
                        Role = MessageRole.User,
                        Content = "Look at this file",
                        SequenceNumber = 0,
                        Attachments =
                        [
                            new Attachment
                            {
                                FileName = "notes.md",
                                MimeType = "text/markdown",
                                Size = 12,
                                TextContent = "# Notes",
                            },
                        ],
                    },
                ],
            });

            await seed.SaveChangesAsync();
        }

        await using (var delete = _db.CreateDbContext())
        {
            var conversation = await delete.Conversations.SingleAsync(c => c.Id == conversationId);
            delete.Conversations.Remove(conversation);
            await delete.SaveChangesAsync();
        }

        await using var verify = _db.CreateDbContext();

        // Orphaned rows here would mean a deleted chat's message bodies stayed on disk,
        // which is a privacy problem, not just untidiness.
        Assert.Empty(await verify.Messages.Where(m => m.ConversationId == conversationId).ToListAsync());
        Assert.Empty(await verify.Attachments.Where(a => a.MessageId == messageId).ToListAsync());
    }

    [Fact]
    public async Task The_same_model_cannot_be_cached_twice_for_one_provider()
    {
        await using var context = _db.CreateDbContext();

        context.Models.Add(NewModel("openrouter", "openai/gpt-5-mini", "first"));
        await context.SaveChangesAsync();

        await using var duplicate = _db.CreateDbContext();

        // A different surrogate id, the same provider and native id. The unique index is what
        // keeps a half-failed refresh from producing two rows for one model in the picker.
        duplicate.Models.Add(NewModel("openrouter", "openai/gpt-5-mini", "second", surrogate: "other"));

        await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
    }

    [Fact]
    public async Task Providers_are_seeded_once_and_re_running_initialization_changes_nothing()
    {
        await using (var first = _db.CreateDbContext())
        {
            var providers = await first.Providers.OrderBy(p => p.SortOrder).ToListAsync();

            Assert.Equal(["openrouter", "nvidia"], providers.Select(p => p.Id));
            Assert.All(providers, p => Assert.True(p.IsEnabled));
        }

        // Startup runs this on every launch, so it has to be idempotent.
        await new DatabaseInitializer(_db, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        await using var second = _db.CreateDbContext();
        Assert.Equal(2, await second.Providers.CountAsync());
    }

    private static Model NewModel(string providerId, string modelId, string name, string? surrogate = null) =>
        new()
        {
            Id = surrogate ?? $"{providerId}:{modelId}",
            ProviderId = providerId,
            ModelId = modelId,
            Name = name,
        };

    /// <summary>Column name to declared SQLite type, read from the file itself.</summary>
    private async Task<Dictionary<string, string>> ColumnTypesAsync(string table)
    {
        await using var context = _db.CreateDbContext();
        var connection = context.Database.GetDbConnection();

        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        // Interpolated rather than parameterised because pragma_table_info takes a literal,
        // and the value comes from the EF model - there is no user input anywhere near it.
        command.CommandText = $"SELECT name, type FROM pragma_table_info('{table}')";

        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            types[reader.GetString(0)] = reader.GetString(1);
        }

        return types;
    }
}
