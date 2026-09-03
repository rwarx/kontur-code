using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Domain.Interfaces;
using AIClient.Infrastructure.Database;
using AIClient.Infrastructure.Providers;
using AIClient.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIClient.Tests.Support;

/// <summary>
/// A real SQLite database in a temporary directory, migrated exactly the way the
/// application migrates it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not the EF Core in-memory provider. The most recent persistence bug in this
/// project was SQLite refusing to translate <c>ORDER BY</c> over a <see cref="DateTimeOffset"/>,
/// and the in-memory provider - which is LINQ over dictionaries, with no SQL and no type
/// mapping - would have passed that test happily while the application crashed on launch.
/// A provider-specific hazard can only be caught by the provider that has it.
/// </para>
/// <para>
/// A temporary file rather than <c>:memory:</c> because the application resolves its context
/// through <see cref="IDbContextFactory{TContext}"/> and opens a new connection per operation;
/// an in-memory database is scoped to its connection, so the factory shape would have to be
/// faked to make it work. A file keeps the seam identical to production.
/// </para>
/// <para>
/// <c>Pooling=False</c> so the file has no live handles once the tests are done and the
/// directory can actually be deleted.
/// </para>
/// </remarks>
public sealed class TestDatabase : IDbContextFactory<AIClientDbContext>, IAsyncDisposable
{
    private readonly string _directory;
    private readonly DbContextOptions<AIClientDbContext> _options;

    private TestDatabase(string directory, DbContextOptions<AIClientDbContext> options)
    {
        _directory = directory;
        _options = options;
        DatabasePath = Path.Combine(directory, "aiclient.db");
    }

    /// <summary>Full path of the SQLite file, for tests that inspect the schema directly.</summary>
    public string DatabasePath { get; }

    /// <summary>Creates a fresh database with every migration applied and providers seeded.</summary>
    public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "aiclient-tests",
            Guid.CreateVersion7().ToString("n"));

        Directory.CreateDirectory(directory);

        var options = new DbContextOptionsBuilder<AIClientDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "aiclient.db")};Pooling=False")
            .Options;

        var database = new TestDatabase(directory, options);

        // The production initialiser, not EnsureCreated: section 17 asks for migrations, and
        // EnsureCreated would build the schema from the model and quietly hide a migration
        // that disagrees with it.
        await new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(cancellationToken)
            .ConfigureAwait(false);

        return database;
    }

    public AIClientDbContext CreateDbContext() => new(_options);

    /// <summary>The real conversation store over this database.</summary>
    public ConversationService Conversations(ITitleGenerator? titleGenerator = null) =>
        new(this,
            titleGenerator ?? new HeuristicTitleGenerator(),
            NullLogger<ConversationService>.Instance);

    /// <summary>The real settings store over this database.</summary>
    public SettingsService Settings() => new(this, NullLogger<SettingsService>.Instance);

    /// <summary>The real registry over this database, backed by whichever providers a test supplies.</summary>
    public ProviderRegistry Registry(ISecureStorage secureStorage, params IAIProvider[] providers) =>
        new(this, secureStorage, providers, NullLogger<ProviderRegistry>.Instance);

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test run over; the OS
            // clears %TEMP% eventually.
        }

        return ValueTask.CompletedTask;
    }
}
