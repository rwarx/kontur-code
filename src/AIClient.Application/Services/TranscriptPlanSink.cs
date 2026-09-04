using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services;

/// <summary>
/// The plan sink a host gets when it has installed no canvas: the plan is kept, and nobody pretends it
/// was drawn.
/// </summary>
/// <remarks>
/// <para>
/// Registered as the default so that <see cref="AgentMode.PlanCanvas"/> degrades to
/// <see cref="AgentMode.Plan"/> instead of failing. The plan is written into the transcript by the tool
/// that produced it - a tool result is a persisted row like any other - so it survives the conversation
/// being closed and reopened with or without a canvas to draw on.
/// </para>
/// <para>
/// The note matters more than it looks. Without it a model told to plan for a canvas finishes by telling
/// the user to go and look at a canvas that is not there, and the user reasonably concludes the feature
/// is broken rather than absent.
/// </para>
/// </remarks>
public sealed class TranscriptPlanSink : IAgentPlanSink
{
    private static readonly AgentPlanAcceptance Kept = AgentPlanAcceptance.NotDrawn(
        "There is no canvas in this build, so the plan is in the conversation rather than on one. "
        + "Do not tell the user to look at a canvas; write the plan out for them instead.");

    public bool CanDraw => false;

    public Task<AgentPlanAcceptance> AcceptAsync(
        AgentPlan plan,
        CancellationToken cancellationToken = default) => Task.FromResult(Kept);
}
