using System.Text;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Shows the unified diff of staged changes, or all unstaged changes if nothing is staged.
/// Can also diff between two refs or show the diff for a specific file.
/// </summary>
public sealed class GitDiffTool : IAgentTool
{
    private readonly IGitService _git;

    public GitDiffTool(IGitService git) => _git = git;

    public string Name => "git_diff";

    public string Description =>
        "Shows the diff of changes in the project. Without arguments, it shows staged changes "
        + "(ready to commit) or, if nothing is staged, all unstaged changes. Pass a file path "
        + "to see the diff for one file. Pass two refs (commit hashes, branch names) to compare "
        + "them. The diff is unified format, showing exactly what was added and removed.";

    public string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "file": {
              "type": "string",
              "description": "Path of a single file to diff, relative to the project root."
            },
            "from_ref": {
              "type": "string",
              "description": "First ref to compare (commit hash or branch name). Requires to_ref."
            },
            "to_ref": {
              "type": "string",
              "description": "Second ref to compare (commit hash or branch name). Requires from_ref."
            }
          },
          "required": []
        }
        """;

    public AgentToolRisk Risk => AgentToolRisk.Read;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!await _git.IsRepositoryAsync(cancellationToken).ConfigureAwait(false))
        {
            return AgentToolResult.Fail(
                "The project folder is not a git repository.",
                "git_diff: not a repository");
        }

        var file = arguments.GetString("file");
        var fromRef = arguments.GetString("from_ref");
        var toRef = arguments.GetString("to_ref");

        GitDiff diff;

        if (file is not null)
        {
            diff = await _git.GetFileDiffAsync(file, cancellationToken).ConfigureAwait(false);
        }
        else if (fromRef is not null && toRef is not null)
        {
            diff = await _git.GetDiffAsync(fromRef, toRef, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            diff = await _git.GetDiffAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(diff.RawDiff))
        {
            return AgentToolResult.Ok(
                "No changes to show.",
                file is not null ? $"git_diff {file}: no changes" : "git_diff: no changes");
        }

        // Truncate very large diffs to keep the model context manageable.
        var output = diff.RawDiff;
        if (output.Length > 50_000)
        {
            output = output[..50_000]
                + "\n\n... Diff truncated at 50,000 characters. Pass a specific file path to see a smaller diff.";
        }

        var summary = file is not null
            ? $"git_diff {file}"
            : fromRef is not null
                ? $"git_diff {fromRef}..{toRef}"
                : "git_diff (working tree)";

        return AgentToolResult.Ok(output, summary);
    }
}
