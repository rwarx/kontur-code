using System.Text;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Shows the model what is in the project.
/// </summary>
/// <remarks>
/// The first call of almost every task, and the one that decides whether the rest of the turn is
/// aimed at real files or invented ones.
/// </remarks>
public sealed class ListFilesTool : WorkspaceTool
{
    public ListFilesTool(IWorkspaceService workspace)
        : base(workspace)
    {
    }

    public override string Name => "list_files";

    public override string Description =>
        "Lists the files and folders of the open project. Call this before reading or writing anything, "
        + "so that the paths you use are ones that exist. Folders are shown with a trailing '/'. Build "
        + "output and dependency folders such as bin, obj and node_modules are left out, and "
        + "version-control internals and files holding credentials are never listed at all. Prefer "
        + "listing one folder and then another over a recursive listing: a recursive listing of a large "
        + "project stops at a limit, and a listing that stopped tells you nothing about what was past it.";

    public override string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Folder to list, relative to the project root. Omit it, or use '.', for the root itself."
            },
            "recursive": {
              "type": "boolean",
              "description": "Also list everything in the folders underneath. Off by default."
            }
          },
          "required": []
        }
        """;

    public override AgentToolRisk Risk => AgentToolRisk.Read;

    public override async Task<AgentToolResult> ExecuteAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!TryOptionalPath(arguments, "path", out var path, out var failure))
        {
            return failure;
        }

        var recursive = arguments.GetBoolean("recursive");
        var result = await Workspace.ListAsync(path, recursive, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return Refuse(result.Error!);
        }

        var listing = result.Value!;
        var summary = $"{Name} {path}";

        if (listing.Entries.Count == 0)
        {
            return Done($"'{path}' is empty.", summary);
        }

        var text = new StringBuilder()
            .Append(listing.Entries.Count)
            .Append(listing.Entries.Count == 1 ? " entry under '" : " entries under '")
            .Append(path)
            .Append("':");

        foreach (var entry in listing.Entries)
        {
            text.AppendLine().Append(entry.Path.Value);

            // A trailing slash rather than a column of "dir"/"file", which costs a token per line and
            // reads less like the paths the model already knows how to write.
            if (entry.IsDirectory)
            {
                text.Append('/');
            }
            else
            {
                text.Append("  ").Append(FormatSize(entry.Size));
            }
        }

        if (listing.IsTruncated)
        {
            text.AppendLine().Append(
                "The listing stopped at its limit, so this is not all of it. List one of the folders above, "
                + "or use search_files to find what you are after.");
        }

        return Done(text.ToString(), summary);
    }
}
