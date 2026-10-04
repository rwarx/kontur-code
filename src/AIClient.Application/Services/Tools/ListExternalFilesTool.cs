using System.Text;
using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Lists the immediate children of a directory outside the open project.
/// </summary>
/// <remarks>
/// The out-of-project counterpart of listing inside the workspace, and gated the same way as
/// <see cref="ReadExternalFileTool"/>: an absolute path the workspace cannot vouch for, so it asks
/// the user once. It shows one level, not a whole tree - a walk of an arbitrary folder on the disk
/// could be enormous - and it leaves out the noise folders and every credential-shaped name.
/// </remarks>
public sealed class ListExternalFilesTool : IAgentTool, IAgentToolAvailability
{
    private readonly ISettingsService _settings;
    private readonly IExternalFileService _external;

    public ListExternalFilesTool(ISettingsService settings, IExternalFileService external)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(external);

        _settings = settings;
        _external = external;
    }

    public string Name => "list_external_files";

    public string Description =>
        "Lists the files and folders directly inside a directory outside the open project, given its "
        + "absolute path such as 'C:\\Users\\me'. It shows one level only, not a whole tree. Reaching "
        + "outside the project needs the user's approval. Build output, dependency folders and "
        + "credential-carrying names are left out, and this application's own data is refused.";

    public string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Absolute path to the directory to list, such as 'C:\\Users\\me'."
            }
          },
          "required": ["path"]
        }
        """;

    /// <summary>
    /// <see cref="AgentToolRisk.Execute"/>, not <c>Write</c>. See the note on
    /// <see cref="ReadExternalFileTool"/>: a remembered approval for a listing is a remembered
    /// approval to enumerate the disk, which is the first half of reading it.
    /// </summary>
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
                "Reading files outside the project is switched off. The user can turn it on under "
                + "Settings → Agent, and until they do, only the open folder can be listed.");
        }

        if (!arguments.TryGetString("path", out var path, out var pathError))
        {
            return AgentToolResult.Fail(pathError);
        }

        var result = await _external.ListAsync(path, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return AgentToolResult.Fail(result.Error!);
        }

        var listing = result.Value!;
        var summary = $"{Name} {listing.Path}";

        if (listing.Entries.Count == 0)
        {
            return AgentToolResult.Ok($"'{listing.Path}' is empty.", summary);
        }

        var text = new StringBuilder();
        text.Append(listing.Path).Append(", ").Append(listing.Entries.Count)
            .Append(listing.Entries.Count == 1 ? " entry:" : " entries:");

        foreach (var entry in listing.Entries)
        {
            text.AppendLine();

            if (entry.IsDirectory)
            {
                text.Append("[dir]  ").Append(entry.Name);
            }
            else
            {
                text.Append("[file] ").Append(entry.Name).Append(" (").Append(FormatSize(entry.Size)).Append(')');
            }
        }

        if (listing.IsTruncated)
        {
            text.AppendLine().AppendLine().Append(
                "[more entries than the limit for one listing - name a subfolder to see further in]");
        }

        return AgentToolResult.Ok(text.ToString(), summary);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
