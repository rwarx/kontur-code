using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Renames something, or moves it somewhere else in the project.
/// </summary>
/// <remarks>
/// One call rather than the read-write-delete a model would otherwise improvise, which matters because
/// that improvisation loses the file if it stops in the middle. Refusing to overwrite is the other half:
/// a move onto an existing path is how a rename with a stale destination quietly destroys a file.
/// </remarks>
public sealed class MoveFileTool : WorkspaceTool, IAgentToolPreview
{
    public MoveFileTool(IWorkspaceService workspace)
        : base(workspace)
    {
    }

    public override string Name => "move_file";

    public override string Description =>
        "Moves or renames a file or a folder, in one step. Use it instead of writing a copy and deleting "
        + "the original. Folders in the destination path are created as needed. The move is refused if "
        + "something already exists at the destination: delete that first if it is meant to be replaced. "
        + "Nothing inside the moved file changes, so references to it elsewhere in the project will need "
        + "updating - search for the old name afterwards.";

    public override string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "from": {
              "type": "string",
              "description": "The file or folder to move, relative to the project root."
            },
            "to": {
              "type": "string",
              "description": "Where it should end up, relative to the project root. Include the file name."
            }
          },
          "required": ["from", "to"]
        }
        """;

    public override AgentToolRisk Risk => AgentToolRisk.Write;

    public override async Task<AgentToolResult> ExecuteAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!TryPath(arguments, "from", out var from, out var fromFailure))
        {
            return fromFailure;
        }

        if (!TryPath(arguments, "to", out var to, out var toFailure))
        {
            return toFailure;
        }

        // Caught here rather than below, where the destination existing is what would be reported: true,
        // but it reads as a collision with some other file instead of as the same path twice.
        if (from == to)
        {
            return Refuse($"'from' and 'to' are both '{from}', so there is nothing to move.");
        }

        var result = await Workspace.MoveAsync(from, to, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return Refuse(result.Error!);
        }

        return Done(
            from.Parent == to.Parent
                ? $"Renamed '{from}' to '{to.Name}'."
                : $"Moved '{from}' to '{to}'.",
            $"{Name} {from} -> {to}");
    }

    /// <summary>
    /// Names both ends, and warns about the destination that is already taken.
    /// </summary>
    /// <remarks>
    /// There is no diff to show: nothing inside the file changes. What the user needs instead is the
    /// distinction between a rename and a move to another folder, and whether something is already
    /// sitting where this is going - the case where a rename with a stale destination would otherwise
    /// look like an ordinary one.
    /// </remarks>
    public async Task<AgentToolPreview> DescribeAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!TryPath(arguments, "from", out var from, out _)
            || !TryPath(arguments, "to", out var to, out _))
        {
            return AgentToolPreview.None;
        }

        var source = await Workspace.StatAsync(from, cancellationToken).ConfigureAwait(false);
        var what = source.Success && source.Value!.IsDirectory ? "the folder " : string.Empty;

        var move = from.Parent == to.Parent
            ? $"Rename {what}{from} to {to.Name}"
            : $"Move {what}{from} to {to}";

        var destination = await Workspace.StatAsync(to, cancellationToken).ConfigureAwait(false);

        if (destination.Success)
        {
            return AgentToolPreview.Describe($"{move} - something is already there, so this will be refused");
        }

        return AgentToolPreview.Describe(
            source.Success ? move : $"{move} - {from} does not exist, so this will be refused");
    }
}
