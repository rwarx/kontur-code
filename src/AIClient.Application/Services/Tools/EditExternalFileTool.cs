using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Swaps one exact piece of text for another in a file outside the open project, on the user's say-so.
/// </summary>
/// <remarks>
/// The out-of-project counterpart of <see cref="EditFileTool"/>, and unforgiving in the same way: a
/// match that is absent or ambiguous is refused rather than guessed at. It is a step riskier than
/// its in-project sibling for the reason the other external tools are - the workspace cannot vouch
/// for a path it does not contain - so it is <see cref="AgentToolRisk.Execute"/> and asks on every
/// call where editing inside the project asks once. The application's own data, every
/// credential-shaped name, and the operating system's own folders are refused by the service beneath
/// it whatever the user answers.
/// </remarks>
public sealed class EditExternalFileTool : IAgentTool, IAgentToolPreview, IAgentToolAvailability
{
    private readonly ISettingsService _settings;
    private readonly IExternalFileService _external;

    public EditExternalFileTool(ISettingsService settings, IExternalFileService external)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(external);

        _settings = settings;
        _external = external;
    }

    public string Name => "edit_external_file";

    public string Description =>
        "Changes part of a text file outside the open project by replacing an exact piece of text, given "
        + "the file's absolute path such as 'C:\\Users\\me\\notes.txt'. Prefer it over write_external_file "
        + "for a file that already exists. 'find' is matched literally, including indentation, so copy it "
        + "from a read of the file rather than retyping it. The edit is refused if 'find' appears nowhere, "
        + "and also if it appears more than once - add surrounding lines until it is unique, or set "
        + "replace_all. Reaching outside the project needs the user's approval on every call. This "
        + "application's own data, files that carry credentials, and the operating system's own folders "
        + "are refused however you name them.";

    public string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Absolute path to the file to edit, such as 'C:\\Users\\me\\notes.txt'."
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

    public AgentToolRisk Risk => AgentToolRisk.Execute;

    public bool IsAvailable => Settings.AllowExternalFiles;

    private AgentSettings Settings => _settings.Current.Agent;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!Settings.AllowExternalFiles)
        {
            return AgentToolResult.Fail(
                "Editing files outside the project is switched off. The user can turn it on under "
                + "Settings → Agent, and until they do, only files inside the open folder can be edited.");
        }

        if (!arguments.TryGetString("path", out var path, out var pathError))
        {
            return AgentToolResult.Fail(pathError);
        }

        if (!arguments.TryGetString("find", out var find, out var findError))
        {
            return AgentToolResult.Fail(findError);
        }

        if (!arguments.TryGetString("replace", out var replacement, out var replaceError, allowEmpty: true))
        {
            return AgentToolResult.Fail(replaceError);
        }

        if (string.Equals(find, replacement, StringComparison.Ordinal))
        {
            return AgentToolResult.Fail("'find' and 'replace' are the same text, so this edit would change nothing.");
        }

        var result = await _external
            .ReplaceAsync(path, find, replacement, arguments.GetBoolean("replace_all"), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return AgentToolResult.Fail(result.Error!);
        }

        var write = result.Value!;
        var replacements = write.Replacements == 1 ? "1 occurrence" : $"{write.Replacements} occurrences";

        return AgentToolResult.Ok(
            $"Replaced {replacements} in '{write.Path}'. It now has "
            + (write.LinesAfter == 1 ? "1 line." : $"{write.LinesAfter} lines.")
            + (write.LinesAfter == write.LinesBefore ? string.Empty : $" It had {write.LinesBefore}."),
            $"{Name} {write.Path}");
    }

    /// <summary>
    /// Performs the substitution against a copy of the file to show what it would do, and names the two
    /// ways it would be refused before the user is asked rather than after.
    /// </summary>
    public async Task<AgentToolPreview> DescribeAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!arguments.TryGetString("path", out var path, out _)
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
            : string.Concat(text.AsSpan(0, first), replacement, text.AsSpan(first + find.Length));

        return AgentToolPreview.Describe(
            occurrences == 1 ? $"Edit {path} (1 occurrence)" : $"Edit {path} ({occurrences} occurrences)",
            TextDiff.Unified(text, updated, path));
    }

    private async Task<ExternalFile?> PeekAsync(string path, CancellationToken cancellationToken)
    {
        var read = await _external.ReadAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        return read.Success ? read.Value : null;
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
