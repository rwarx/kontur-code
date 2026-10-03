namespace AIClient.Domain.Workspace;

/// <summary>
/// Names the path segments that hold credentials or version-control internals, wherever they sit
/// on disk. The workspace guard refuses these inside the project; the external-file guard refuses
/// them anywhere, so the same names cannot be reached by stepping outside the folder instead.
/// </summary>
/// <remarks>
/// A deliberate, self-contained copy of the workspace service's own list rather than a shared
/// dependency: this type is pure Domain with no I/O, and keeping it independent means the external
/// path can be reasoned about on its own without weakening the in-workspace guard. Exact names,
/// never prefixes - a prefix test would refuse '.gitignore' along with '.git'. The one family
/// match is '.env', whose real files all hold secrets while '.env.example' exists to be committed.
/// </remarks>
public static class SensitiveFiles
{
    private static readonly string[] ProtectedNames =
    [
        ".git", ".svn", ".hg", ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker",
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", ".netrc", "_netrc", ".npmrc", ".pypirc",
        ".git-credentials", ".htpasswd", "credentials", "credentials.json", "secrets.json",
        "secrets.yaml", "secrets.yml", "appsettings.secrets.json", "serviceaccount.json",
    ];

    private static readonly string[] ProtectedExtensions =
    [
        ".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".ppk", ".kdbx", ".gpg", ".asc",
    ];

    private static readonly string[] EnvTemplateSuffixes = [".example", ".sample", ".template", ".dist"];

    /// <summary>
    /// Whether one segment of a path names something the agent may not touch, checked
    /// case-insensitively because the file systems this runs on are.
    /// </summary>
    /// <remarks>
    /// Meant to be applied to every segment of a path, not just its last: 'C:\keys\.ssh\config'
    /// is refused because '.ssh' is, and 'secrets.json' is refused whether it is the file being
    /// named or a folder above it.
    /// </remarks>
    public static bool IsProtectedSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return false;
        }

        if (ProtectedNames.Contains(segment, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (segment.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            && !EnvTemplateSuffixes.Any(suffix => segment.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var extension = Path.GetExtension(segment);

        return extension.Length > 0
            && ProtectedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
