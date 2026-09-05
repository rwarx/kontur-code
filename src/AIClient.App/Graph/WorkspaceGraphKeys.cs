using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AIClient.App.Graph;

/// <summary>
/// The persistence key for the canvas state: derived from the workspace root, so each
/// folder owns one graph document.
/// </summary>
/// <remarks>
/// The store sanitises keys itself, but the rule "one workspace, one graph" is a product
/// decision, not a storage one, so it lives here where the workspace-facing code can see
/// it. A null root (no workspace open) still gets a key: the canvas remains usable and
/// its contents survive a restart even without a folder attached.
/// </remarks>
public static class WorkspaceGraphKeys
{
    /// <summary>Stable key for a workspace root; the same folder always maps to the same key.</summary>
    public static string FromWorkspaceRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return "workspace-none";
        }

        var normalised = root.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Roots contain separators and case quirks that make poor file names; the hash
        // sidesteps both and keeps the key short enough to read in a log line.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        var suffix = Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();

        return $"ws-{suffix}";
    }
}
