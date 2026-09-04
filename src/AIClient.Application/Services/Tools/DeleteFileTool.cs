using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Removes one file, or one empty folder.
/// </summary>
/// <remarks>
/// <para>
/// There is no recursive delete anywhere below this, so clearing a tree costs one call - and one
/// approval - per entry. That is the intended friction: it is what stops a single confused step from
/// emptying a repository, and the description says so rather than leaving the model to discover it
/// through a refusal.
/// </para>
/// <para>
/// The entry is measured before it goes, because afterwards there is nothing left to measure. The line
/// this leaves in the transcript is the only record the user has of what was removed.
/// </para>
/// </remarks>
public sealed class DeleteFileTool : WorkspaceTool, IAgentToolPreview
{
    public DeleteFileTool(IWorkspaceService workspace)
        : base(workspace)
    {
    }

    public override string Name => "delete_file";

    public override string Description =>
        "Deletes one file, or one folder that is already empty. There is no recursive delete: to remove a "
        + "folder with things in it, delete each of them first. Deleting cannot be undone from here, so "
        + "read a file before deleting it if you are not certain about it.";

    public override string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "File or empty folder to delete, relative to the project root."
            }
          },
          "required": ["path"]
        }
        """;

    public override AgentToolRisk Risk => AgentToolRisk.Write;

    public override async Task<AgentToolResult> ExecuteAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!TryPath(arguments, "path", out var path, out var failure))
        {
            return failure;
        }

        // A failure here is not reported: the delete is about to produce the authoritative version of
        // the same refusal, and two messages for one problem is one too many.
        var before = await Workspace.StatAsync(path, cancellationToken).ConfigureAwait(false);
        var result = await Workspace.DeleteAsync(path, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return Refuse(result.Error!);
        }

        var entry = before.Success ? before.Value : null;

        var what = entry is null
            ? $"Deleted '{path}'."
            : entry.IsDirectory
                ? $"Deleted the empty folder '{path}'."
                : $"Deleted the file '{path}' ({FormatSize(entry.Size)}).";

        return Done(what, $"{Name} {path}");
    }

    /// <summary>
    /// Shows what is about to be destroyed.
    /// </summary>
    /// <remarks>
    /// The one preview where the contents matter more than the change. A deletion has no diff worth
    /// reading - everything goes - but "delete src/Widget.cs" and the sight of what is in
    /// <c>src/Widget.cs</c> are different decisions, and this is the last moment the file exists.
    /// </remarks>
    public async Task<AgentToolPreview> DescribeAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!TryPath(arguments, "path", out var path, out _))
        {
            return AgentToolPreview.None;
        }

        var entry = await Workspace.StatAsync(path, cancellationToken).ConfigureAwait(false);

        if (!entry.Success)
        {
            return AgentToolPreview.Describe($"Delete {path}, which does not exist");
        }

        if (entry.Value!.IsDirectory)
        {
            // Said plainly because the count decides the answer. An empty folder is a formality; a folder
            // with things in it is a call that will be refused, and the user should not have to approve it
            // to find that out.
            var listing = await Workspace.ListAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            var entries = listing.Success ? listing.Value!.Entries.Count : 0;

            return AgentToolPreview.Describe(
                entries == 0
                    ? $"Delete the empty folder {path}"
                    : $"Delete the folder {path}, which has {entries} entries in it - this will be refused");
        }

        var file = await PeekAsync(path, cancellationToken).ConfigureAwait(false);

        if (file is null)
        {
            return AgentToolPreview.Describe(
                $"Delete {path} ({FormatSize(entry.Value.Size)}), whose contents cannot be shown");
        }

        return AgentToolPreview.Describe(
            file.IsTruncated
                ? $"Delete {path} ({Plural(file.TotalLines)}, {FormatSize(entry.Value.Size)}) - the first "
                    + $"{file.LineCount} of them are shown"
                : $"Delete {path} ({Plural(file.TotalLines)}, {FormatSize(entry.Value.Size)})",
            TextDiff.Unified(file.Content, null, path.ToString()));
    }
}
