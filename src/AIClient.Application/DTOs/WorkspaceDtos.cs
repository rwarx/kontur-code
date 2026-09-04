using AIClient.Domain.Workspace;

namespace AIClient.Application.DTOs;

/// <summary>
/// The outcome of a workspace operation: a value, or the reason there is none.
/// </summary>
/// <remarks>
/// Every failure here is expected - a path outside the root, a file that grew past the cap, a
/// search string that matches nothing - and every one of them is read by a language model as a
/// tool result. An exception would be the wrong shape twice over: it would climb out of the agent
/// loop looking like a defect, and its message would be written for a log rather than for the
/// caller that has to correct itself on the next step.
/// </remarks>
public sealed record WorkspaceResult<T>(bool Success, T? Value, string? Error)
    where T : class
{
    public static WorkspaceResult<T> Ok(T value) => new(true, value, null);

    public static WorkspaceResult<T> Fail(string error) => new(false, null, error);
}

/// <summary>One entry in a listing: a file or a directory, named relative to the root.</summary>
public sealed record WorkspaceEntry
{
    public required WorkspacePath Path { get; init; }

    public required bool IsDirectory { get; init; }

    /// <summary>
    /// Size in bytes, and zero for a directory - which is not walked to total up its contents,
    /// because listing a folder should not cost a recursive scan of everything under it.
    /// </summary>
    public long Size { get; init; }

    public DateTimeOffset ModifiedAt { get; init; }
}

/// <summary>The contents of one directory, or of a subtree when the walk was recursive.</summary>
public sealed record WorkspaceListing
{
    public required WorkspacePath Path { get; init; }

    public required IReadOnlyList<WorkspaceEntry> Entries { get; init; }

    /// <summary>
    /// True when the listing stopped at its cap. Reported rather than hidden: a caller that
    /// cannot tell a complete listing from a truncated one will conclude a file does not exist.
    /// </summary>
    public bool IsTruncated { get; init; }
}

/// <summary>A slice of a text file.</summary>
public sealed record WorkspaceFile
{
    public required WorkspacePath Path { get; init; }

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

/// <summary>What a write changed.</summary>
public sealed record WorkspaceWrite
{
    public required WorkspacePath Path { get; init; }

    /// <summary>
    /// True when the file did not exist before, which is the difference between "wrote a new
    /// file" and "replaced the one that was there" - the second is the one a user wants to know
    /// about.
    /// </summary>
    public required bool Created { get; init; }

    public int LinesBefore { get; init; }

    public int LinesAfter { get; init; }

    public long Size { get; init; }

    /// <summary>Occurrences substituted by a replace. Zero for a whole-file write.</summary>
    public int Replacements { get; init; }
}

/// <summary>One matching line.</summary>
public sealed record WorkspaceMatch
{
    public required WorkspacePath Path { get; init; }

    /// <summary>1-based, so it lines up with what an editor shows.</summary>
    public required int LineNumber { get; init; }

    /// <summary>The line itself, stripped of surrounding whitespace and capped in length.</summary>
    public required string Line { get; init; }
}

/// <summary>What to search for, and where.</summary>
public sealed record WorkspaceSearchQuery
{
    public required string Query { get; init; }

    /// <summary>Subtree to search. Null searches the whole workspace.</summary>
    public WorkspacePath? Path { get; init; }

    /// <summary>
    /// Glob on the file name alone, e.g. <c>*.cs</c>. Null searches every text file.
    /// </summary>
    public string? FilePattern { get; init; }

    /// <summary>
    /// Treats <see cref="Query"/> as a .NET regular expression.
    /// </summary>
    /// <remarks>
    /// Off by default, because a literal substring is what a caller usually means and a pattern
    /// sent by accident then matches nothing instead of everything. A pattern that is honoured
    /// runs under a match timeout: the expression arrives from a model, and a few characters of
    /// nested quantifier are enough to hang a scan on a single line.
    /// </remarks>
    public bool IsRegex { get; init; }

    public bool MatchCase { get; init; }
}

/// <summary>The matches, and an honest account of what the search did not reach.</summary>
public sealed record WorkspaceSearchResult
{
    public required IReadOnlyList<WorkspaceMatch> Matches { get; init; }

    public int FilesScanned { get; init; }

    /// <summary>True when the match cap or the time budget ended the scan early.</summary>
    public bool IsTruncated { get; init; }
}
