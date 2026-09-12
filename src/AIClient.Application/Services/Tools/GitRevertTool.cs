using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Reverts the last commit or a specific commit, keeping changes in the working tree.
/// Requires approval. Always creates a new commit rather than rewriting history.
/// </summary>
public sealed class GitRevertTool : IAgentTool
{
    private readonly IGitService _git;

    public GitRevertTool(IGitService git) => _git = git;

    public string Name => "git_revert";

    public string Description =>
        "Reverts a commit by creating a new commit that undoes its changes. The original commit "
        + "is preserved in history. Without arguments, reverts the most recent commit. Pass a "
        + "commit SHA to revert a specific commit. The reverted changes are kept in the working "
        + "tree for review. Requires user approval.";

    public string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "commit": {
              "type": "string",
              "description": "The commit SHA to revert. Omit to revert the last commit."
            }
          },
          "required": []
        }
        """;

    public AgentToolRisk Risk => AgentToolRisk.Write;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!await _git.IsRepositoryAsync(cancellationToken).ConfigureAwait(false))
        {
            return AgentToolResult.Fail(
                "The project folder is not a git repository.",
                "git_revert: not a repository");
        }

        var commitSha = arguments.GetString("commit");

        GitResult result;

        if (commitSha is not null)
        {
            // Show the commit being reverted for context.
            var commit = await _git.GetCommitAsync(commitSha, cancellationToken).ConfigureAwait(false);
            if (commit is null)
            {
                return AgentToolResult.Fail(
                    $"Commit '{commitSha}' not found.",
                    "git_revert: commit not found");
            }

            result = await _git.RevertAsync(commitSha, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                return AgentToolResult.Fail(
                    $"Could not revert commit {commit.ShortSha}: {result.Error}",
                    "git_revert: failed");
            }

            return AgentToolResult.Ok(
                $"Reverted commit {commit.ShortSha}: {commit.Message}\n\n{result.Output}",
                $"git_revert: {commit.ShortSha}");
        }
        else
        {
            result = await _git.RevertLastAsync(cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                return AgentToolResult.Fail(
                    $"Could not revert the last commit: {result.Error}",
                    "git_revert: failed");
            }

            return AgentToolResult.Ok(
                $"Reverted the last commit.\n\n{result.Output}",
                "git_revert: last commit");
        }
    }
}
