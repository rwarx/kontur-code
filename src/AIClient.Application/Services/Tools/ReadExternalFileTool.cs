using System.Text;
using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Reads one text file from outside the open project, on the user's say-so.
/// </summary>
/// <remarks>
/// The out-of-project counterpart of <see cref="ReadFileTool"/>. It takes an absolute path rather
/// than a project-relative one, and it is a step riskier for it: the workspace cannot vouch for a
/// path it does not contain, so this is <see cref="AgentToolRisk.Write"/> and asks the user once,
/// where reading inside the project asks for nothing. The application's own data and any
/// credential-shaped file are refused by the service beneath it whatever the answer.
/// </remarks>
public sealed class ReadExternalFileTool : IAgentTool, IAgentToolAvailability
{
    private readonly ISettingsService _settings;
    private readonly IExternalFileService _external;

    public ReadExternalFileTool(ISettingsService settings, IExternalFileService external)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(external);

        _settings = settings;
        _external = external;
    }

    public string Name => "read_external_file";

    public string Description =>
        "Reads a text file from outside the open project, given its absolute path such as "
        + "'C:\\Users\\me\\notes.txt'. Use it only for a file the project needs but does not contain - a "
        + "config in the user's home folder, a file in a sibling repository. Reaching outside the project "
        + "needs the user's approval, so read only what you need and say why. Files that carry credentials, "
        + "and this application's own data, are refused however you name them. Large or binary files are "
        + "refused; use start_line and line_count to work through a big file in pieces.";

    public string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Absolute path to the file, such as 'C:\\Users\\me\\notes.txt'."
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

    public AgentToolRisk Risk => AgentToolRisk.Write;

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
                "Reading files outside the project is switched off. The user can turn it on under "
                + "Settings → Agent, and until they do, only files inside the open folder can be read.");
        }

        if (!arguments.TryGetString("path", out var path, out var pathError))
        {
            return AgentToolResult.Fail(pathError);
        }

        if (!arguments.TryGetInt32("start_line", out var startLine, out var startError))
        {
            return AgentToolResult.Fail(startError);
        }

        if (!arguments.TryGetInt32("line_count", out var lineCount, out var countError))
        {
            return AgentToolResult.Fail(countError);
        }

        var result = await _external.ReadAsync(path, startLine ?? 1, lineCount, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return AgentToolResult.Fail(result.Error!);
        }

        var file = result.Value!;
        var summary = $"{Name} {file.Path}";

        if (file.TotalLines == 0)
        {
            return AgentToolResult.Ok($"'{file.Path}' is empty.", summary);
        }

        if (file.LineCount == 0)
        {
            return AgentToolResult.Ok(
                $"'{file.Path}' has {file.TotalLines} lines; the requested range starts past the end.", summary);
        }

        var whole = file.FirstLine == 1 && file.LineCount == file.TotalLines;
        var text = new StringBuilder();

        if (whole)
        {
            text.Append(file.Path).Append(", ").Append(file.TotalLines).Append(" lines:");
        }
        else
        {
            var last = file.FirstLine + file.LineCount - 1;
            text.Append(file.Path).Append(", lines ").Append(file.FirstLine).Append('-').Append(last)
                .Append(" of ").Append(file.TotalLines).Append(':');
            summary = $"{summary} ({file.FirstLine}-{last})";
        }

        text.AppendLine().Append(file.Content);

        if (file.IsTruncated)
        {
            text.AppendLine().AppendLine().Append(
                "[cut off at the size limit for one result - ask for fewer lines to see the rest]");
        }

        return AgentToolResult.Ok(text.ToString(), summary);
    }
}
