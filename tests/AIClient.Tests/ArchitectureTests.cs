using System.Reflection;

namespace AIClient.Tests;

/// <summary>
/// Verifies that the clean architecture layering rules are enforced at compile time.
/// Domain and Application must never reference WPF, PresentationFramework, or UI toolkit assemblies.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly string[] ForbiddenAssemblies =
    [
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "System.Xaml",
        "WPF-UI",
        "Wpf.Ui",
        "CommunityToolkit.Mvvm",
    ];

    [Fact]
    public void Domain_ShouldNotReferenceWpfAssemblies()
    {
        var references = GetReferencedAssemblies("AIClient.Domain");
        var violations = references
            .Where(r => ForbiddenAssemblies.Contains(r, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(violations.Count == 0,
            $"Domain layer references WPF assemblies: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Application_ShouldNotReferenceWpfAssemblies()
    {
        var references = GetReferencedAssemblies("AIClient.Application");
        var violations = references
            .Where(r => ForbiddenAssemblies.Contains(r, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(violations.Count == 0,
            $"Application layer references WPF assemblies: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Domain_ShouldNotReferenceApplication()
    {
        var references = GetReferencedAssemblies("AIClient.Domain");
        Assert.DoesNotContain("AIClient.Application", references);
    }

    [Fact]
    public void Application_ShouldNotReferenceInfrastructure()
    {
        var references = GetReferencedAssemblies("AIClient.Application");
        Assert.DoesNotContain("AIClient.Infrastructure", references);
    }

    [Fact]
    public void Application_ShouldNotReferenceApp()
    {
        var references = GetReferencedAssemblies("AIClient.Application");
        Assert.DoesNotContain("AIClient.App", references);
    }

    [Fact]
    public void Domain_ShouldNotReferenceInfrastructure()
    {
        var references = GetReferencedAssemblies("AIClient.Domain");
        Assert.DoesNotContain("AIClient.Infrastructure", references);
    }

    [Fact]
    public void Domain_ShouldNotReferenceApp()
    {
        var references = GetReferencedAssemblies("AIClient.Domain");
        Assert.DoesNotContain("AIClient.App", references);
    }

    private static List<string> GetReferencedAssemblies(string assemblyName)
    {
        var assembly = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == assemblyName);

        if (assembly is null)
        {
            var path = FindAssemblyDll(assemblyName);
            if (path is null)
            {
                return [];
            }

            assembly = Assembly.LoadFrom(path);
        }

        return assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FindAssemblyDll(string assemblyName)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, $"{assemblyName}.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "src", assemblyName, "bin", "Debug", "net10.0", $"{assemblyName}.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "src", assemblyName, "bin", "Release", "net10.0", $"{assemblyName}.dll"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
