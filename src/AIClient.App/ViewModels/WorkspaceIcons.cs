using AIClient.App.Controls;
using AIClient.Domain.Graph;

namespace AIClient.App.ViewModels;

/// <summary>
/// The one place that maps graph kinds and file names to icon vocabulary, shared by the
/// file tree, the context surface and the canvas palette.
/// </summary>
/// <remarks>
/// Two surfaces inferring "what this thing is" independently is how one screen calls a
/// thing a Service and its neighbour calls the same thing a Module. One mapper, one
/// vocabulary, and a change of mind happens once.
/// </remarks>
public static class WorkspaceIcons
{
    public static IconKind ForNodeKind(GraphNodeKind kind) => kind switch
    {
        GraphNodeKind.File => IconKind.File,
        GraphNodeKind.Folder => IconKind.Folder,
        GraphNodeKind.Module => IconKind.Code,
        GraphNodeKind.Service => IconKind.Package,
        GraphNodeKind.Interface => IconKind.Link,
        GraphNodeKind.Data => IconKind.Memory,
        GraphNodeKind.View => IconKind.Eye,
        GraphNodeKind.Test => IconKind.Check,
        GraphNodeKind.Plan => IconKind.Sparkle,
        GraphNodeKind.Task => IconKind.Tasks,
        GraphNodeKind.Agent => IconKind.Bot,
        GraphNodeKind.Model => IconKind.Models,
        GraphNodeKind.External => IconKind.Open,
        GraphNodeKind.Note => IconKind.Note,
        _ => IconKind.Node,
    };

    /// <summary>The same inference the graph indexer applies, kept side by side with it.</summary>
    public static IconKind ForFile(string name)
    {
        var extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);

        var isTest = name.Contains("Test", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".test.", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".spec.", StringComparison.OrdinalIgnoreCase);

        if (isTest)
        {
            return IconKind.Check;
        }

        return extension switch
        {
            ".sln" or ".csproj" or ".props" or ".targets" => IconKind.Package,
            ".cs" or ".ts" or ".tsx" or ".js" or ".py" or ".rs" or ".go" or ".java"
                => stem.StartsWith('I') && stem.Length > 1 && char.IsUpper(stem[1]) ? IconKind.Link
                : stem.EndsWith("Service", StringComparison.OrdinalIgnoreCase) ? IconKind.Package
                : IconKind.Code,
            ".xaml" or ".axaml" or ".cshtml" or ".razor" or ".vue" or ".jsx" or ".html" or ".css" => IconKind.Eye,
            ".sql" or ".db" or ".sqlite" or ".csv" or ".json" or ".xml" or ".yaml" or ".yml" => IconKind.Memory,
            ".md" or ".txt" => IconKind.Note,
            _ => IconKind.File,
        };
    }
}
