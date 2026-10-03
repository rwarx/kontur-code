namespace AIClient.Application.DTOs;

/// <summary>
/// The results of the out-of-project file tools. Deliberately separate from the workspace DTOs:
/// those carry a <c>WorkspacePath</c>, which by construction cannot name anything outside the
/// project, so an external result needs a plain string path instead. Same shapes otherwise, so a
/// caller that has read the workspace tools reads these the same way.
/// </summary>
/// <remarks>
/// All reference types, so they satisfy <see cref="WorkspaceResult{T}"/>'s <c>class</c> constraint
/// and reuse that one result envelope rather than inventing another.
/// </remarks>
public sealed record ExternalFile
{
    /// <summary>The absolute path that was read, exactly as it was resolved on disk.</summary>
    public required string Path { get; init; }

    public required string Content { get; init; }

    /// <summary>1-based line number of the first returned line.</summary>
    public int FirstLine { get; init; }

    /// <summary>Lines actually returned.</summary>
    public int LineCount { get; init; }

    /// <summary>Lines in the whole file, so the caller can tell what it has not seen.</summary>
    public int TotalLines { get; init; }

    public long Size { get; init; }

    /// <summary>True when the character cap cut the content short.</summary>
    public bool IsTruncated { get; init; }
}

/// <summary>What an external write changed.</summary>
public sealed record ExternalWrite
{
    public required string Path { get; init; }

    /// <summary>True when the file did not exist before.</summary>
    public required bool Created { get; init; }

    public int LinesBefore { get; init; }

    public int LinesAfter { get; init; }

    public long Size { get; init; }

    /// <summary>Occurrences substituted by a replace. Zero for a whole-file write.</summary>
    public int Replacements { get; init; }
}

/// <summary>One entry in an external listing: a file or a directory.</summary>
public sealed record ExternalEntry
{
    public required string Name { get; init; }

    public required bool IsDirectory { get; init; }

    /// <summary>Size in bytes, and zero for a directory.</summary>
    public long Size { get; init; }
}

/// <summary>The immediate children of one external directory.</summary>
public sealed record ExternalListing
{
    public required string Path { get; init; }

    public required IReadOnlyList<ExternalEntry> Entries { get; init; }

    /// <summary>True when the listing stopped at its cap.</summary>
    public bool IsTruncated { get; init; }
}
