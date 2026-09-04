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
public sealed class EditFileTool : WorkspaceTool, IAgentToolPreview
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

    /// <summary>
    /// Performs the substitution against a copy of the file to show what it would do.
    /// </summary>
    /// <remarks>
    /// The two ways this edit is refused - a match that is absent, and one that is ambiguous - are
    /// reported as the summary rather than left to the execution. It is the difference between a user
    /// approving a call and then watching it fail, and knowing before they answer that the model has
    /// copied the text wrongly.
    /// </remarks>
    public async Task<AgentToolPreview> DescribeAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!TryPath(arguments, "path", out var path, out _)
            || !arguments.TryGetString("find", out var rawFind, out _)
            || !arguments.TryGetString("replace", out var rawReplace, out _, allowEmpty: true))
        {
            return AgentToolPreview.None;
        }

        var existing = await PeekAsync(path, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            return AgentToolPreview.Describe($"Edit {path}, which cannot be read");
        }

        // Compared with line endings levelled, the way the workspace does it before matching. A file
        // saved with CRLF would otherwise match nothing the model copied out of a read and every preview
        // over such a file would claim the edit was about to fail.
        var text = Level(existing.Content);
        var find = Level(rawFind);
        var occurrences = Count(text, find);

        if (occurrences == 0)
        {
            return AgentToolPreview.Describe(
                existing.IsTruncated
                    ? $"Edit {path} - the text to replace is not in the part of the file that was read"
                    : $"Edit {path} - the text to replace is not in the file, so this will be refused");
        }

        var all = arguments.GetBoolean("replace_all");

        if (occurrences > 1 && !all)
        {
            return AgentToolPreview.Describe(
                $"Edit {path} - that text appears {occurrences} times, so this will be refused as ambiguous");
        }

        var replacement = Level(rawReplace);
        var first = text.IndexOf(find, StringComparison.Ordinal);
        var updated = all
            ? text.Replace(find, replacement, StringComparison.Ordinal)
            : string.Concat(
                text.AsSpan(0, first),
                replacement,
                text.AsSpan(first + find.Length));

        return AgentToolPreview.Describe(
            occurrences == 1
                ? $"Edit {path} (1 occurrence)"
                : $"Edit {path} ({occurrences} occurrences)",
            TextDiff.Unified(text, updated, path.ToString()));
    }

    private static string Level(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static int Count(string text, string find)
    {
        var occurrences = 0;
        var at = text.IndexOf(find, StringComparison.Ordinal);

        while (at >= 0)
        {
            occurrences++;
            at = text.IndexOf(find, at + find.Length, StringComparison.Ordinal);
        }

        return occurrences;
    }
}
