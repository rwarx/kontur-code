using System.Text;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Shows the current git status: branch, tracked/untracked changes, staged/unstaged.
/// </summary>
public sealed class GitStatusTool : IAgentTool
{
    private readonly IGitService _git;

    public GitStatusTool(IGitService git) => _git = git;

    public string Name => "git_status";

    public string Description =>
        "Shows the current git status of the project: branch, ahead/behind tracking, and every "
        + "changed file with its staged and unstaged state. Use this before committing, checking "
        + "diffs, or switching branches to understand the current state of the repository.";

    public string ParametersJsonSchema => """{"type":"object","properties":{}}""";

    public AgentToolRisk Risk => AgentToolRisk.Read;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!await _git.IsRepositoryAsync(cancellationToken).ConfigureAwait(false))
        {
            return AgentToolResult.Fail(
                "The project folder is not a git repository. Initialise one with 'git init' first.",
                "git_status: not a repository");
        }

        var status = await _git.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        var text = new StringBuilder();
        text.Append("Branch: ").Append(status.Branch);

        if (status.UpstreamBranch is not null)
        {
            text.Append(" (tracking ").Append(status.UpstreamBranch).Append(')');
        }

        if (status.IsClean)
        {
            text.AppendLine().Append("Working tree is clean — nothing to commit.");
            return AgentToolResult.Ok(text.ToString(), $"git_status: {status.Branch}, clean");
        }

        text.AppendLine().Append(status.Files.Count).Append(" changed file(s):");

        foreach (var file in status.Files)
        {
            text.AppendLine().Append(FormatState(file.Staged)).Append(' ').Append(FormatState(file.Unstaged)).Append("  ").Append(file.Path);
        }

        text.AppendLine()
            .Append("Staged = ready to commit. Unstaged = not yet staged. ")
            .Append("Use 'git_diff' to see what changed, 'git_commit' to commit staged changes.");

        return AgentToolResult.Ok(text.ToString(), $"git_status: {status.Branch}, {status.Files.Count} changed");
    }

    private static string FormatState(GitFileState state) => state switch
    {
        GitFileState.Added => "A",
        GitFileState.Modified => "M",
        GitFileState.Deleted => "D",
        GitFileState.Renamed => "R",
        GitFileState.Copied => "C",
        GitFileState.Untracked => "?",
        GitFileState.Conflicted => "U",
        _ => ".",
    };
}
