namespace AIClient.Domain.Interfaces;

/// <summary>
/// First-class git operations for the agent and the workspace. Implementations run
/// git through a process runner and never through a shell.
/// </summary>
/// <remarks>
/// All paths are workspace-relative. The service resolves them against the workspace root
/// and refuses anything outside it, the same way <c>IWorkspaceService</c> does for file tools.
/// </remarks>
public interface IGitService
{
    /// <summary>Whether the workspace root is inside a git repository.</summary>
    Task<bool> IsRepositoryAsync(CancellationToken cancellationToken);

    /// <summary>Returns the current repository status.</summary>
    Task<GitStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Returns the unified diff for staged changes, or for all changes if nothing is staged.</summary>
    Task<GitDiff> GetDiffAsync(CancellationToken cancellationToken);

    /// <summary>Returns the diff between two refs (commits, branches, tags).</summary>
    Task<GitDiff> GetDiffAsync(string fromRef, string toRef, CancellationToken cancellationToken);

    /// <summary>Returns the diff for a specific file.</summary>
    Task<GitDiff> GetFileDiffAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>Returns the commit history for a file, limited to <paramref name="maxCount"/>.</summary>
    Task<IReadOnlyList<GitCommit>> GetFileHistoryAsync(string filePath, int maxCount, CancellationToken cancellationToken);

    /// <summary>Returns the recent commit history for the repository.</summary>
    Task<IReadOnlyList<GitCommit>> GetHistoryAsync(int maxCount, CancellationToken cancellationToken);

    /// <summary>Returns the current branch name and tracking info.</summary>
    Task<GitBranchInfo> GetBranchInfoAsync(CancellationToken cancellationToken);

    /// <summary>Returns all local branches.</summary>
    Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(CancellationToken cancellationToken);

    /// <summary>Creates and checks out a new branch from the current HEAD.</summary>
    Task<GitResult> CreateBranchAsync(string branchName, CancellationToken cancellationToken);

    /// <summary>Checks out an existing branch.</summary>
    Task<GitResult> CheckoutAsync(string branchName, CancellationToken cancellationToken);

    /// <summary>Stages the specified files (or all tracked changes if empty).</summary>
    Task<GitResult> StageAsync(IReadOnlyList<string>? filePaths, CancellationToken cancellationToken);

    /// <summary>Creates a commit with the given message.</summary>
    Task<GitResult> CommitAsync(string message, CancellationToken cancellationToken);

    /// <summary>Reverts the last commit, keeping the changes in the working tree.</summary>
    Task<GitResult> RevertLastAsync(CancellationToken cancellationToken);

    /// <summary>Reverts a specific commit by SHA.</summary>
    Task<GitResult> RevertAsync(string commitSha, CancellationToken cancellationToken);

    /// <summary>Shows the details of a commit.</summary>
    Task<GitCommit?> GetCommitAsync(string commitSha, CancellationToken cancellationToken);
}

/// <summary>Result of a git operation that does not return structured data.</summary>
public sealed record GitResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public string? Error { get; init; }

    public static GitResult Ok(string output = "") => new() { Success = true, Output = output };
    public static GitResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>Current state of the working tree and staging area.</summary>
public sealed record GitStatus
{
    public string Branch { get; init; } = string.Empty;
    public string? UpstreamBranch { get; init; }
    public bool IsClean { get; init; }
    public IReadOnlyList<GitFileStatus> Files { get; init; } = [];
}

/// <summary>Status of one file in the working tree.</summary>
public sealed record GitFileStatus
{
    public string Path { get; init; } = string.Empty;
    public GitFileState Staged { get; init; }
    public GitFileState Unstaged { get; init; }
}

/// <summary>State flags for a file.</summary>
[Flags]
public enum GitFileState
{
    None = 0,
    Added = 1,
    Modified = 2,
    Deleted = 4,
    Renamed = 8,
    Copied = 16,
    Untracked = 32,
    Conflicted = 64,
}

/// <summary>A unified diff for one or more files.</summary>
public sealed record GitDiff
{
    public string RawDiff { get; init; } = string.Empty;
    public IReadOnlyList<GitFileDiff> Files { get; init; } = [];
}

/// <summary>Diff stats for one file.</summary>
public sealed record GitFileDiff
{
    public string Path { get; init; } = string.Empty;
    public string? OldPath { get; init; }
    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }
}

/// <summary>A single commit.</summary>
public sealed record GitCommit
{
    public string Sha { get; init; } = string.Empty;
    public string ShortSha { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public DateTimeOffset Date { get; init; }
    public IReadOnlyList<string> ParentShas { get; init; } = [];
}

/// <summary>Information about a branch.</summary>
public sealed record GitBranchInfo
{
    public string Name { get; init; } = string.Empty;
    public bool IsCurrent { get; init; }
    public string? TrackingBranch { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
}
