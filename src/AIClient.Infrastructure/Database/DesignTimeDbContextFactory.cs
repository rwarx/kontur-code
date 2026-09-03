using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AIClient.Infrastructure.Database;

/// <summary>
/// Lets <c>dotnet ef</c> construct a context without starting the WPF application.
/// </summary>
/// <remarks>
/// Design-time tooling would otherwise have to boot the App project to find the DI
/// container, which means starting a UI to add a migration. This factory points at a
/// throwaway file in the build output: migrations are generated from the model, never
/// from the user's real database, so scaffolding can never touch live data.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AIClientDbContext>
{
    public AIClientDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AIClientDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;

        return new AIClientDbContext(options);
    }
}
