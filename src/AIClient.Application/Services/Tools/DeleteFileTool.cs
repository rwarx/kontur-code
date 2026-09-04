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
public sealed class DeleteFileTool : WorkspaceTool
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
}
