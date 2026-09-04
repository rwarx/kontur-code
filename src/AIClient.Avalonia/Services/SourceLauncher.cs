using System.Diagnostics;
using System.IO;
using AIClient.Domain.Graph;

namespace AIClient.Avalonia.Services;

/// <summary>
/// Shows where a node's file lives, using the file manager instead of an in-app editor.
/// </summary>
/// <remarks>
/// The app has no editor of its own yet, so revealing the file is the honest version of
/// "open source". The path is rebuilt from the workspace root rather than stored absolute,
/// which keeps the graph portable between machines. Windows-bound until the shell grows a
/// cross-platform story for it.
/// </remarks>
public static class SourceLauncher
{
    /// <summary>
    /// Reveals the file behind a node in Explorer and returns a refusal worth showing, or
    /// null when the reveal was attempted.
    /// </summary>
    public static string? Reveal(string? workspaceRoot, GraphNode node)
    {
        if (node.Source is not { } source)
        {
            return "This node has no file behind it.";
        }

        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return "Open a project folder first.";
        }

        var relative = source.Value.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.Combine(workspaceRoot, relative);

        if (!File.Exists(full))
        {
            return "The file is not on disk.";
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"")
            {
                UseShellExecute = true,
            });

            return null;
        }
        catch (Exception)
        {
            return "The file manager could not be opened.";
        }
    }
}
