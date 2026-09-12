using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Creates a new branch and switches to it, or switches to an existing branch. Requires approval.
/// </summary>
public sealed class GitCheckoutTool : IAgentTool
{
    private readonly IGitService _git;

    public GitCheckoutTool(IGitService git) => _git = git;

    public string Name => "git_checkout";

    public string Description =>
        "Creates a new branch from the current HEAD and switches to it. Use this to isolate work "
        + "on a task or feature. The branch name should be descriptive (e.g. 'fix/login-validation'). "
        + "If a branch with that name already exists, it will be checked out instead. Requires user approval.";

    public string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "branch": {
              "type": "string",
              "description": "Name of the branch to create and switch to, or an existing branch to switch to."
            }
          },
          "required": ["branch"]
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
                "git_checkout: not a repository");
        }

        if (!arguments.TryGetString("branch", out var branchName, out var error))
        {
            return AgentToolResult.Fail(error, "git_checkout: missing branch name");
        }

        // Check if the branch already exists.
        var branches = await _git.GetBranchesAsync(cancellationToken).ConfigureAwait(false);
        var exists = branches.Any(b =>
            string.Equals(b.Name, branchName, StringComparison.OrdinalIgnoreCase));

        GitResult result;

        if (exists)
        {
            result = await _git.CheckoutAsync(branchName, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await _git.CreateBranchAsync(branchName, cancellationToken).ConfigureAwait(false);
        }

        if (!result.Success)
        {
            var action = exists ? "switch to" : "create";
            return AgentToolResult.Fail(
                $"Could not {action} branch '{branchName}': {result.Error}",
                "git_checkout: failed");
        }

        var verb = exists ? "Switched to" : "Created and switched to";
        return AgentToolResult.Ok(
            $"{verb} branch '{branchName}'.",
            $"git_checkout: {branchName}");
    }
}
