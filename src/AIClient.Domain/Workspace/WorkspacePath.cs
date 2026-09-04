using System.Diagnostics.CodeAnalysis;

namespace AIClient.Domain.Workspace;

/// <summary>
/// A path the model is allowed to name: relative to the workspace root, canonical, and proven
/// not to point outside it.
/// </summary>
/// <remarks>
/// <para>
/// Every path in an agent turn originates in generated text, which makes this the one type
/// standing between a language model and the user's disk. It is deliberately pure - no I/O, no
/// environment, no platform checks - so the whole rule set can be pinned by a table of inputs
/// and read in one sitting. The file system half of the guarantee (a symlink whose target sits
/// outside the root) needs the disk and lives in the workspace service.
/// </para>
/// <para>
/// The rules are all rejections rather than repairs. A <c>..</c> segment is refused outright
/// instead of being resolved and the result re-checked, because "normalise, then verify the
/// prefix" is the shape that has leaked for decades - one unnormalised encoding, one
/// case-folding difference, one symlink evaluated in the wrong order, and the check passes on a
/// path that escapes. A model has no legitimate reason to send one: it is given relative paths
/// and asked for relative paths back.
/// </para>
/// <para>
/// Rooted forms are detected textually rather than through <see cref="Path.IsPathRooted(string)"/>
/// so the verdict cannot depend on the host operating system. <c>C:\Windows</c> is not rooted
/// according to a Unix runtime, and a guard that agrees with the runtime is weakest on the
/// platform where the path means something.
/// </para>
/// </remarks>
public sealed record WorkspacePath
{
    /// <summary>
    /// Longest accepted path, in characters. Generous next to any real source tree while still
    /// bounding a model that has started repeating a directory name. The absolute path this
    /// becomes may still be too long for the operating system; that is reported by the file
    /// system and surfaced as a failed tool result rather than guessed at here.
    /// </summary>
    public const int MaxLength = 400;

    /// <summary>Deepest accepted nesting. Far past anything a project uses in practice.</summary>
    public const int MaxSegments = 64;

    /// <summary>
    /// Characters refused anywhere in a path. <c>:</c> is in here for two reasons beyond being
    /// illegal in a Windows file name: it opens an NTFS alternate data stream
    /// (<c>notes.txt:hidden</c> writes bytes no listing shows), and it is the separator of the
    /// <c>\\?\</c> and <c>C:</c> prefixes that would take a path out of the root.
    /// </summary>
    private static readonly char[] Forbidden = ['<', '>', ':', '"', '|', '?', '*'];

    /// <summary>
    /// DOS device names, which Windows still resolves in any directory and at any depth.
    /// Opening <c>src/nul</c> for writing silently discards everything written to it, and
    /// <c>com1</c> blocks on a serial port; neither is a file the user has.
    /// </summary>
    /// <remarks>
    /// Matched against the segment up to its first dot, because the reservation survives an
    /// extension: <c>nul.txt</c> is the same device as <c>nul</c>.
    /// </remarks>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private WorkspacePath(string value) => Value = value;

    /// <summary>The workspace root itself, which is a valid target for a listing and nothing else.</summary>
    public static WorkspacePath Root { get; } = new(string.Empty);

    /// <summary>
    /// The canonical form: <c>/</c>-separated, no leading or trailing separator, no <c>.</c>
    /// segments, empty for the root.
    /// </summary>
    /// <remarks>
    /// One separator regardless of platform, because this string is also what the model reads
    /// back. Handing it a mix of <c>\</c> and <c>/</c> across a conversation invites it to
    /// invent a third form.
    /// </remarks>
    public string Value { get; }

    public bool IsRoot => Value.Length == 0;

    /// <summary>The final segment, i.e. the file or directory name. Empty for the root.</summary>
    public string Name => IsRoot ? string.Empty : Value[(Value.LastIndexOf('/') + 1)..];

    /// <summary>The containing directory, or null when this is a top-level entry or the root.</summary>
    public WorkspacePath? Parent
    {
        get
        {
            var cut = Value.LastIndexOf('/');
            return cut < 0 ? (IsRoot ? null : Root) : new WorkspacePath(Value[..cut]);
        }
    }

    public IReadOnlyList<string> Segments => IsRoot ? [] : Value.Split('/');

    /// <summary>
    /// Validates a path from outside and returns its canonical form, or the reason it was
    /// refused.
    /// </summary>
    /// <remarks>
    /// The rejection text is written for the model, not for a log: it is handed straight back as
    /// a tool result, and a message that says which rule was broken is the difference between
    /// the model correcting itself on the next step and retrying the same path until the step
    /// budget runs out.
    /// </remarks>
    public static bool TryParse(
        string? raw,
        [NotNullWhen(true)] out WorkspacePath? path,
        [NotNullWhen(false)] out string? error)
    {
        path = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "A path is required. Use '.' for the workspace root.";
            return false;
        }

        // Trimmed as a whole, which forgives the stray space a model puts before a path. Segments
        // are not trimmed: doing that would quietly turn 'a /b' into a path the caller never
        // asked for.
        var candidate = raw.Trim();

        if (candidate.Length > MaxLength)
        {
            error = $"That path is {candidate.Length} characters long; the limit is {MaxLength}.";
            return false;
        }

        if (candidate[0] is '/' or '\\')
        {
            error = "Paths must be relative to the workspace root, so they cannot start with a slash.";
            return false;
        }

        if (candidate.Length >= 2 && candidate[1] == ':' && char.IsAsciiLetter(candidate[0]))
        {
            error = "Paths must be relative to the workspace root, so they cannot name a drive.";
            return false;
        }

        foreach (var c in candidate)
        {
            if (char.IsControl(c))
            {
                error = "A path may not contain control characters.";
                return false;
            }

            if (Array.IndexOf(Forbidden, c) >= 0)
            {
                error = $"A path may not contain '{c}'.";
                return false;
            }
        }

        var segments = new List<string>();

        foreach (var segment in candidate.Split('/', '\\'))
        {
            // Collapses 'a//b', a trailing slash, and the './' a model often prefixes.
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                error = "'..' is not allowed. Every path has to stay inside the workspace root.";
                return false;
            }

            if (segment.All(c => c == '.'))
            {
                error = $"'{segment}' is not a usable name.";
                return false;
            }

            if (segment[^1] is '.' or ' ')
            {
                // Windows drops these when it opens the file, so 'notes.txt.' and 'notes.txt'
                // are the same file under two names. Accepting both would mean a later
                // comparison between two paths could disagree with the file system.
                error = $"'{segment}' ends with a dot or a space, which Windows silently drops.";
                return false;
            }

            var stem = segment.Split('.')[0];

            if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            {
                error = $"'{segment}' is a reserved device name on Windows and cannot be used as a file name.";
                return false;
            }

            segments.Add(segment);
        }

        if (segments.Count > MaxSegments)
        {
            error = $"That path is {segments.Count} levels deep; the limit is {MaxSegments}.";
            return false;
        }

        error = null;
        path = segments.Count == 0 ? Root : new WorkspacePath(string.Join('/', segments));
        return true;
    }

    /// <summary>
    /// Validates a path that is expected to be well formed, throwing if it is not.
    /// </summary>
    /// <remarks>
    /// For paths the application itself produced. Anything reaching the app from a model or a
    /// user goes through <see cref="TryParse"/>, where the refusal is a message rather than an
    /// exception.
    /// </remarks>
    public static WorkspacePath Parse(string raw) =>
        TryParse(raw, out var path, out var error) ? path : throw new ArgumentException(error, nameof(raw));

    /// <summary>
    /// Validates <paramref name="name"/> as a child of this path.
    /// </summary>
    /// <remarks>
    /// Used while walking the disk, where the names come from the file system rather than the
    /// model. They are validated all the same: a name this type would refuse as input must not
    /// be handed out as a path, or the model gets an entry it can see and never open.
    /// </remarks>
    public bool TryAppend(
        string name,
        [NotNullWhen(true)] out WorkspacePath? child,
        [NotNullWhen(false)] out string? error) =>
        TryParse(IsRoot ? name : $"{Value}/{name}", out child, out error);

    /// <summary>
    /// Joins this path onto an absolute root directory, in the platform's own separator.
    /// </summary>
    /// <remarks>
    /// Pure string work - nothing here touches the disk. The caller still has to confirm the
    /// result resolves inside the root, because a symlink can move a path that looks contained.
    /// </remarks>
    public string ResolveAgainst(string rootDirectory) =>
        IsRoot
            ? rootDirectory
            : Path.Combine(rootDirectory, Value.Replace('/', Path.DirectorySeparatorChar));

    public override string ToString() => IsRoot ? "." : Value;
}
