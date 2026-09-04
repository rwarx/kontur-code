using System.Text;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Hands the model the contents of one text file.
/// </summary>
/// <remarks>
/// The text comes back exactly as it is on disk, with no line numbers down the side. Numbering would
/// help the model point at a line and hurt it everywhere else: <c>edit_file</c> matches literally, and
/// a model quoting text it was shown with numbers on it produces an edit that can never match. The
/// header carries the line range instead, which is enough to reason about position without putting
/// anything in front of the text itself.
/// </remarks>
public sealed class ReadFileTool : WorkspaceTool
{
    public ReadFileTool(IWorkspaceService workspace)
        : base(workspace)
    {
    }

    public override string Name => "read_file";

    public override string Description =>
        "Reads a text file from the open project. The contents come back exactly as they are on disk, "
        + "with no line numbers added, so text from a read can be pasted straight into edit_file. Read a "
        + "file before editing it. Large files are refused rather than truncated silently - use "
        + "start_line and line_count to work through one in pieces, or search_files to find the part you "
        + "need. Binary files cannot be read.";

    public override string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "File to read, relative to the project root, such as 'src/Program.cs'."
            },
            "start_line": {
              "type": "integer",
              "description": "First line to return, counting from 1. Defaults to the start of the file."
            },
            "line_count": {
              "type": "integer",
              "description": "How many lines to return. Defaults to the rest of the file."
            }
          },
          "required": ["path"]
        }
        """;

    public override AgentToolRisk Risk => AgentToolRisk.Read;

    public override async Task<AgentToolResult> ExecuteAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!TryPath(arguments, "path", out var path, out var failure))
        {
            return failure;
        }

        if (!arguments.TryGetInt32("start_line", out var startLine, out var startError))
        {
            return Refuse(startError);
        }

        if (!arguments.TryGetInt32("line_count", out var lineCount, out var countError))
        {
            return Refuse(countError);
        }

        var result = await Workspace
            .ReadAsync(path, startLine ?? 1, lineCount, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return Refuse(result.Error!);
        }

        var file = result.Value!;
        var summary = $"{Name} {path}";

        if (file.TotalLines == 0)
        {
            return Done($"'{path}' is empty.", summary);
        }

        var whole = file.FirstLine == 1 && file.LineCount == file.TotalLines;
        var text = new StringBuilder();

        if (whole)
        {
            text.Append(path).Append(", ").Append(file.TotalLines).Append(" lines:");
        }
        else
        {
            text.Append(path)
                .Append(", lines ")
                .Append(file.FirstLine)
                .Append('-')
                .Append(file.FirstLine + file.LineCount - 1)
                .Append(" of ")
                .Append(file.TotalLines)
                .Append(':');

            summary = $"{summary} ({file.FirstLine}-{file.FirstLine + file.LineCount - 1})";
        }

        text.AppendLine().Append(file.Content);

        if (file.IsTruncated)
        {
            text.AppendLine().AppendLine().Append(
                "[cut off at the size limit for one result - ask for fewer lines to see the rest]");
        }

        return Done(text.ToString(), summary);
    }
}
