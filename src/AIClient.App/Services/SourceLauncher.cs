using System.Diagnostics;
using System.IO;
using AIClient.Domain.Graph;

namespace AIClient.App.Services;

/// <summary>
/// Shows the file behind a graph node in the OS file manager.
/// </summary>
/// <remarks>
/// The app has no code editor, so "open source" means "show me where this is". Kept as a helper
/// rather than a service because both the canvas and the inspector offer the action and neither
/// should have to reference the other to share fifteen lines.
/// </remarks>
internal static class SourceLauncher
{
    /// <summary>
    /// Reveals the node's file. Returns null on success, or a sentence worth showing when there is
    /// nothing to reveal.
    /// </summary>
    public static string? Reveal(string? root, GraphNode node)
    {
        if (node.Source is not { } source || source.IsRoot)
        {
            return "This node has no file behind it.";
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            return "No project folder is open.";
        }

        try
        {
            // Resolved by the sandbox's own rule, then checked again here. The path is about to
            // become an argument to a shell command, and a damaged row must not be able to point
            // that command anywhere outside the folder the person opened.
            var full = source.ResolveAgainst(root);
            var fence = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!full.StartsWith(fence, StringComparison.OrdinalIgnoreCase))
            {
                return "That path is outside the project folder.";
            }

            if (Directory.Exists(full))
            {
                Process.Start(new ProcessStartInfo(full) { UseShellExecute = true })?.Dispose();
                return null;
            }

            if (!File.Exists(full))
            {
                return "That file is no longer on disk.";
            }

            // /select opens the containing folder with the file highlighted, which is more useful
            // than launching whatever application happens to own the extension.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"")
            {
                UseShellExecute = true,
            })?.Dispose();

            return null;
        }
        catch (Exception)
        {
            // Never carries the exception text: it would put an absolute path, and with it the
            // account name, into the window.
            return "That file could not be shown.";
        }
    }
}
