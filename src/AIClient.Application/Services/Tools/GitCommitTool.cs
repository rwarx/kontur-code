using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Stages changes and creates a commit. Requires approval.
/// </summary>
public sealed class GitCommitTool : IAgentTool
{
    private readonly IGitService _git;

    public GitCommitTool(IGitService git) => _git = git;

    public string Name => "git_commit";

    public string Description =>
        "Stages all changes and creates a git commit with the given message. The commit message "
        + "should describe what was changed and why. All modified files are staged automatically — "
        + "you do not need to stage them separately first. Requires user approval.";

    public string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "message": {
              "type": "string",
              "description": "The commit message. Use a short summary line, optionally followed by a blank line and a longer description."
            }
          },
          "required": ["message"]
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
                "git_commit: not a repository");
        }

        if (!arguments.TryGetString("message", out var message, out var error))
        {
            return AgentToolResult.Fail(error, "git_commit: missing message");
        }

        // Stage all changes.
        var staged = await _git.StageAsync(null, cancellationToken).ConfigureAwait(false);
        if (!staged.Success)
        {
            return AgentToolResult.Fail(
                $"Could not stage files: {staged.Error}",
                "git_commit: stage failed");
        }

        // Commit.
        var result = await _git.CommitAsync(message, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return AgentToolResult.Fail(
                $"Commit failed: {result.Error}",
                "git_commit: failed");
        }

        var summary = message.Length > 40 ? message[..40] + "…" : message;
        return AgentToolResult.Ok(
            result.Output,
            $"git_commit: {summary}");
    }
}
