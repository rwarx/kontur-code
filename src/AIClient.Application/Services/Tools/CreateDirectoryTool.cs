using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Makes a folder, and the folders above it.
/// </summary>
/// <remarks>
/// Rarely needed on its own, because <c>write_file</c> creates the folders a new file sits in. It earns
/// its place for the one case that has no file yet - laying out a project's directories before there is
/// anything to put in them - and for making the intent visible in the transcript when it does happen.
/// </remarks>
public sealed class CreateDirectoryTool : WorkspaceTool, IAgentToolPreview
{
    public CreateDirectoryTool(IWorkspaceService workspace)
        : base(workspace)
    {
    }

    public override string Name => "create_directory";

    public override string Description =>
        "Creates a folder, along with any folder above it that does not exist yet. You do not need this "
        + "before write_file, which creates the folders its path needs; use it to lay out a structure "
        + "that has no files in it yet. A folder that already exists is left alone.";

    public override string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Folder to create, relative to the project root, such as 'src/Domain/Entities'."
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

        // Asked about first so the answer can say whether anything actually changed. Creating a folder
        // that is already there succeeds either way, and a model told "created" for a folder that was
        // already full of files will reason about an empty one.
        var existing = await Workspace.StatAsync(path, cancellationToken).ConfigureAwait(false);

        if (existing.Success && existing.Value!.IsDirectory)
        {
            return Done($"'{path}' already exists, so nothing was created.", $"{Name} {path} (existed)");
        }

        var result = await Workspace.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);

        return result.Success
            ? Done($"Created the folder '{path}'.", $"{Name} {path}")
            : Refuse(result.Error!);
    }

    /// <summary>
    /// Names the folder, and says when there would be nothing to do.
    /// </summary>
    /// <remarks>
    /// The least interesting preview of the five, and still worth writing. A dialog whose only line is
    /// the tool's name teaches the user that the dialog is not worth reading, which is the habit that
    /// makes the next approval - a write over something that mattered - go through unread.
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

        var existing = await Workspace.StatAsync(path, cancellationToken).ConfigureAwait(false);

        if (!existing.Success)
        {
            return AgentToolPreview.Describe($"Create the folder {path}");
        }

        return AgentToolPreview.Describe(
            existing.Value!.IsDirectory
                ? $"Create the folder {path} - it already exists, so nothing will change"
                : $"Create the folder {path} - a file of that name is already there, so this will be refused");
    }
}
