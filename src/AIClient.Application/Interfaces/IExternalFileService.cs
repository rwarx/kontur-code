using AIClient.Application.DTOs;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Reads and writes files outside the open project folder, on the user's explicit say-so.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="IWorkspaceService"/> for the one job that service refuses by
/// design: touching a path outside the workspace root. Everything here takes an absolute path and
/// resolves it directly, so none of the workspace's relative-path guarantees apply and the guard
/// is rebuilt here from scratch - the application's own data directory and every credential-shaped
/// name are refused outright, and writes to operating-system folders on top of that.
/// </para>
/// <para>
/// It is not a way around the sandbox: the tools that call it are gated on a setting that is off
/// until the user turns it on, and each call is put in front of the approval gate. This service is
/// only the mechanism; the decision to reach outside the folder is made above it, per call.
/// </para>
/// </remarks>
public interface IExternalFileService
{
    /// <summary>Reads a window of a text file named by absolute path.</summary>
    Task<WorkspaceResult<ExternalFile>> ReadAsync(
        string path,
        int startLine = 1,
        int? lineCount = null,
        CancellationToken cancellationToken = default);

    /// <summary>Writes a file whole, creating it or replacing what was there.</summary>
    Task<WorkspaceResult<ExternalWrite>> WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces occurrences of a literal string inside a file.</summary>
    Task<WorkspaceResult<ExternalWrite>> ReplaceAsync(
        string path,
        string find,
        string replace,
        bool replaceAll,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the immediate children of a directory named by absolute path.</summary>
    Task<WorkspaceResult<ExternalListing>> ListAsync(
        string path,
        CancellationToken cancellationToken = default);
}
