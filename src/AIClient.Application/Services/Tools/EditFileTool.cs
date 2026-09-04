using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// The everyday change: swap one exact piece of text for another.
/// </summary>
/// <remarks>
/// Unforgiving by design, and the description says so plainly. A match that is absent or ambiguous is
/// refused rather than guessed at, because an edit that lands in the wrong place looks exactly like one
/// that landed in the right place from the model's side - and the user finds out later, in a file they
/// were not watching.
/// </remarks>
public sealed class EditFileTool : WorkspaceTool
{
    public EditFileTool(IWorkspaceService workspace)
        : base(workspace)
    {
    }

    public override string Name => "edit_file";

    public override string Description =>
        "Changes part of a file by replacing an exact piece of text. This is the tool to use for editing; "
        + "prefer it over write_file, which replaces the whole file. 'find' is matched literally, including "
        + "indentation and blank lines, so copy it from a read of the file rather than retyping it. The "
        + "edit is refused if 'find' appears nowhere, and also if it appears more than once - include the "
        + "surrounding lines until it is unique, or set replace_all to change every occurrence. Line "
        + "endings are handled for you: write plain '\\n' whatever the file uses.";

    public override string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "File to edit, relative to the project root."
            },
            "find": {
              "type": "string",
              "description": "The exact text to replace, copied from the file including its indentation."
            },
            "replace": {
              "type": "string",
              "description": "The text to put in its place. An empty string deletes the matched text."
            },
            "replace_all": {
              "type": "boolean",
              "description": "Replace every occurrence instead of refusing an ambiguous match. Off by default."
            }
          },
          "required": ["path", "find", "replace"]
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

        if (!arguments.TryGetString("find", out var find, out var findError))
        {
            return Refuse(findError);
        }

        if (!arguments.TryGetString("replace", out var replacement, out var replaceError, allowEmpty: true))
        {
            return Refuse(replaceError);
        }

        if (string.Equals(find, replacement, StringComparison.Ordinal))
        {
            return Refuse("'find' and 'replace' are the same text, so this edit would change nothing.");
        }

        var result = await Workspace
            .ReplaceAsync(path, find, replacement, arguments.GetBoolean("replace_all"), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return Refuse(result.Error!);
        }

        var write = result.Value!;
        var replacements = write.Replacements == 1 ? "1 occurrence" : $"{write.Replacements} occurrences";

        return Done(
            $"Replaced {replacements} in '{path}'. It now has "
            + (write.LinesAfter == 1 ? "1 line." : $"{write.LinesAfter} lines.")
            + (write.LinesAfter == write.LinesBefore ? string.Empty : $" It had {write.LinesBefore}."),
            $"{Name} {path}");
    }
}
