using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services;

/// <summary>
/// The gate a host gets when it has not installed one: reads pass, everything else is refused.
/// </summary>
/// <remarks>
/// <para>
/// Registered as the default so that forgetting to wire up the real gate produces an agent that can
/// look at the project and say what it would do, rather than one that quietly rewrites files with
/// nobody's permission. The failure mode of a missing dialog should be a harmless assistant, not a
/// silent one with write access.
/// </para>
/// <para>
/// Reads are allowed rather than refused because the loop does not ask about them at all - the
/// question would never be put in the first place. Answering it permissively keeps this class honest
/// about the policy it stands in for instead of inventing a stricter one that no real gate uses.
/// </para>
/// </remarks>
public sealed class DenyingAgentApproval : IAgentApproval
{
    private const string Explanation =
        "The user was not asked, because this build has no way to ask them. "
        + "Reading and searching are available; describe the change you would make instead of making it.";

    public Task<AgentApprovalDecision> RequestAsync(
        AgentApprovalRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(request.Risk == AgentToolRisk.Read
            ? AgentApprovalDecision.Allow()
            : AgentApprovalDecision.Deny(Explanation));
}
