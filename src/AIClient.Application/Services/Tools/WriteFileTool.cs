using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Writes a file whole: the way a new file is created, and the last resort for changing one.
/// </summary>
/// <remarks>
/// Described to the model as second choice on purpose. A whole-file write of a file that already exists
/// throws away everything the model did not think to reproduce, and a model working from a truncated
/// read will do exactly that. <c>edit_file</c> cannot make that mistake, so the description sends the
/// model there first and this tool is left for new files and genuine rewrites.
/// </remarks>
public sealed class WriteFileTool : WorkspaceTool
{
    public WriteFileTool(IWorkspaceService workspace)
        : base(workspace)
    {
    }

    public override string Name => "write_file";

    public override string Description =>
        "Creates a file, or replaces one entirely. Use it for a new file. To change part of a file that "
        + "already exists, use edit_file instead: this tool replaces every line, so anything you leave out "
        + "of content is deleted. If you do rewrite a file, read it first and include all of it. The "
        + "file's existing line endings and byte-order mark are kept, so write plain '\\n' and do not "
        + "worry about matching them. Folders in the path are created as needed.";

    public override string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "File to write, relative to the project root."
            },
            "content": {
              "type": "string",
              "description": "The complete new contents of the file. An empty string empties the file."
            }
          },
          "required": ["path", "content"]
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

        if (!arguments.TryGetString("content", out var content, out var contentError, allowEmpty: true))
        {
            return Refuse(contentError);
        }

        var result = await Workspace.WriteAsync(path, content, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return Refuse(result.Error!);
        }

        var write = result.Value!;

        // The two outcomes are worth telling apart in both directions: the model needs to know whether it
        // created something, and the user needs to know whether something was overwritten.
        return write.Created
            ? Done(
                $"Created '{path}' with {Plural(write.LinesAfter)} ({FormatSize(write.Size)}).",
                $"{Name} {path} (new)")
            : Done(
                $"Replaced the contents of '{path}': {Plural(write.LinesBefore)} became "
                + $"{Plural(write.LinesAfter)} ({FormatSize(write.Size)}).",
                $"{Name} {path}");
    }

    private static string Plural(int lines) => lines == 1 ? "1 line" : $"{lines} lines";
}
