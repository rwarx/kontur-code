using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Writes one file whole outside the open project, on the user's say-so.
/// </summary>
/// <remarks>
/// The out-of-project counterpart of <see cref="WriteFileTool"/>, and a step riskier than that
/// in-project sibling for the reason the external reads are: the workspace cannot vouch for a path
/// it does not contain. Where writing inside the project is <see cref="AgentToolRisk.Write"/> -
/// asked once and remembered for the run - this is <see cref="AgentToolRisk.Execute"/> and asks
/// every time, because a write to an arbitrary place on the disk is not a thing to wave through by
/// habit. The application's own data, every credential-shaped name, and the operating system's own
/// folders are refused by the service beneath it whatever the user answers.
/// </remarks>
public sealed class WriteExternalFileTool : IAgentTool, IAgentToolPreview, IAgentToolAvailability
{
    private readonly ISettingsService _settings;
    private readonly IExternalFileService _external;

    public WriteExternalFileTool(ISettingsService settings, IExternalFileService external)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(external);

        _settings = settings;
        _external = external;
    }

    public string Name => "write_external_file";

    public string Description =>
        "Creates or entirely replaces a text file outside the open project, given its absolute path "
        + "such as 'C:\\Users\\me\\notes.txt'. Like write_file it replaces every line, so include the "
        + "whole content; to change part of a file that already exists, prefer edit_external_file. "
        + "Reaching outside the project needs the user's approval on every call, so write only what you "
        + "must and say why. This application's own data, files that carry credentials, and the "
        + "operating system's own folders are refused however you name them.";

    public string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Absolute path to the file to write, such as 'C:\\Users\\me\\notes.txt'."
            },
            "content": {
              "type": "string",
              "description": "The complete new contents of the file. An empty string empties the file."
            }
          },
          "required": ["path", "content"]
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
                "Writing files outside the project is switched off. The user can turn it on under "
                + "Settings → Agent, and until they do, only files inside the open folder can be written.");
        }

        if (!arguments.TryGetString("path", out var path, out var pathError))
        {
            return AgentToolResult.Fail(pathError);
        }

        if (!arguments.TryGetString("content", out var content, out var contentError, allowEmpty: true))
        {
            return AgentToolResult.Fail(contentError);
        }

        var result = await _external.WriteAsync(path, content, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return AgentToolResult.Fail(result.Error!);
        }

        var write = result.Value!;

        return write.Created
            ? AgentToolResult.Ok(
                $"Created '{write.Path}' with {Plural(write.LinesAfter)} ({FormatSize(write.Size)}).",
                $"{Name} {write.Path} (new)")
            : AgentToolResult.Ok(
                $"Replaced the contents of '{write.Path}': {Plural(write.LinesBefore)} became "
                + $"{Plural(write.LinesAfter)} ({FormatSize(write.Size)}).",
                $"{Name} {write.Path}");
    }

    /// <summary>
    /// Says whether this is a new file or a rewrite, and shows what a rewrite would lose.
    /// </summary>
    /// <remarks>
    /// The same forecast <see cref="WriteFileTool"/> gives, over the external read instead of the
    /// workspace one. A peek that comes back empty is treated as a new file rather than reported as an
    /// error, because the write itself may well succeed and a refusal in the approval dialog that the
    /// call does not make would be the wrong thing to show.
    /// </remarks>
    public async Task<AgentToolPreview> DescribeAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!arguments.TryGetString("path", out var path, out _)
            || !arguments.TryGetString("content", out var content, out _, allowEmpty: true))
        {
            return AgentToolPreview.None;
        }

        var lines = TextDiff.CountLines(content);
        var existing = await PeekAsync(path, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            return AgentToolPreview.Describe(
                $"Create {path} ({Plural(lines)})",
                TextDiff.Unified(null, content, path));
        }

        if (existing.IsTruncated)
        {
            return AgentToolPreview.Describe(
                $"Overwrite {path} entirely - {Plural(existing.TotalLines)}, too long to compare here");
        }

        return AgentToolPreview.Describe(
            $"Overwrite {path} ({Plural(existing.TotalLines)} become {Plural(lines)})",
            TextDiff.Unified(existing.Content, content, path));
    }

    private async Task<ExternalFile?> PeekAsync(string path, CancellationToken cancellationToken)
    {
        var read = await _external.ReadAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        return read.Success ? read.Value : null;
    }

    private static string Plural(int lines) => lines == 1 ? "1 line" : $"{lines} lines";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
