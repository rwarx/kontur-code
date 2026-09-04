using AIClient.Application.DTOs;
using AIClient.Domain.Workspace;

namespace AIClient.Application.Interfaces;

/// <summary>
/// The only way anything in this application reaches the user's files.
/// </summary>
/// <remarks>
/// <para>
/// One folder is open at a time and every path is relative to it. That is the whole contract: a
/// caller cannot name an absolute path, cannot climb out with <c>..</c>, and cannot follow a link
/// to somewhere else, because a <see cref="WorkspacePath"/> is the only thing these methods
/// accept and the implementation re-checks containment against the disk before every operation.
/// </para>
/// <para>
/// Failures come back as <see cref="WorkspaceResult{T}"/> rather than as exceptions. The caller
/// above this one is an agent step whose result text goes to a language model, and a refusal that
/// explains itself is the difference between the model fixing its next call and retrying the same
/// one until the step budget is gone.
/// </para>
/// </remarks>
public interface IWorkspaceService
{
    /// <summary>Absolute path of the open folder, or null when none is open.</summary>
    string? Root { get; }

    bool IsOpen { get; }

    /// <summary>Raised after the root changes, carrying the new root or null.</summary>
    event EventHandler<string?>? RootChanged;

    /// <summary>
    /// Opens a folder as the workspace, or explains why it will not be opened.
    /// </summary>
    /// <remarks>
    /// The choice is the user's, so the refusals are few and blunt: a drive root, a system
    /// folder, or anything overlapping this application's own data directory - which holds the
    /// encrypted API keys and the conversation database.
    /// </remarks>
    Task<WorkspaceResult<string>> OpenAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>Closes the workspace, so nothing can be read or written until one is opened again.</summary>
    Task CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists one directory, or the whole subtree under it.</summary>
    /// <remarks>
    /// A recursive walk never descends into a link, so a junction pointing at a large tree costs
    /// one entry rather than a traversal of it.
    /// </remarks>
    Task<WorkspaceResult<WorkspaceListing>> ListAsync(
        WorkspacePath path,
        bool recursive = false,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a text file, or a window of lines from it.</summary>
    /// <param name="startLine">1-based first line to return.</param>
    /// <param name="lineCount">Lines to return, or null for the rest of the file.</param>
    Task<WorkspaceResult<WorkspaceFile>> ReadAsync(
        WorkspacePath path,
        int startLine = 1,
        int? lineCount = null,
        CancellationToken cancellationToken = default);

    /// <summary>Metadata for one entry, without reading it.</summary>
    Task<WorkspaceResult<WorkspaceEntry>> StatAsync(
        WorkspacePath path,
        CancellationToken cancellationToken = default);

    /// <summary>Writes a file whole, creating it or replacing its contents.</summary>
    /// <remarks>
    /// Written to a temporary file and moved into place, so an interruption leaves the previous
    /// contents rather than half of the new ones. An existing file's line endings and byte-order
    /// mark are preserved: a one-line change that silently rewrites every line ending produces a
    /// diff nobody can review.
    /// </remarks>
    Task<WorkspaceResult<WorkspaceWrite>> WriteAsync(
        WorkspacePath path,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Substitutes an exact piece of text inside a file.
    /// </summary>
    /// <remarks>
    /// The everyday edit, and deliberately unforgiving. <paramref name="find"/> is matched
    /// literally, and matching nothing or matching several times without
    /// <paramref name="replaceAll"/> are both refusals: an edit that lands in the wrong place is
    /// far more expensive than one that has to be retried with more surrounding context. The
    /// search text's own line endings are re-cast to the file's before matching, so text copied
    /// out of one file and back into another still matches.
    /// </remarks>
    Task<WorkspaceResult<WorkspaceWrite>> ReplaceAsync(
        WorkspacePath path,
        string find,
        string replacement,
        bool replaceAll = false,
        CancellationToken cancellationToken = default);

    /// <summary>Searches text files for a literal substring, or for a pattern.</summary>
    Task<WorkspaceResult<WorkspaceSearchResult>> SearchAsync(
        WorkspaceSearchQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a directory, and any missing directory above it.</summary>
    Task<WorkspaceResult<WorkspaceEntry>> CreateDirectoryAsync(
        WorkspacePath path,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes one file, or one empty directory.</summary>
    /// <remarks>
    /// There is no recursive delete, on purpose. Removing a tree therefore costs one call and one
    /// approval per file, which is exactly the friction that stops a single confused step from
    /// emptying a repository.
    /// </remarks>
    Task<WorkspaceResult<WorkspacePath>> DeleteAsync(
        WorkspacePath path,
        CancellationToken cancellationToken = default);

    /// <summary>Moves or renames a file or directory. Refuses to overwrite an existing target.</summary>
    Task<WorkspaceResult<WorkspacePath>> MoveAsync(
        WorkspacePath from,
        WorkspacePath to,
        CancellationToken cancellationToken = default);
}
