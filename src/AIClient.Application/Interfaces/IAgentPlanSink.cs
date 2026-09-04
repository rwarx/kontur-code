using AIClient.Application.DTOs;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Where a finished plan goes, when there is somewhere for it to go.
/// </summary>
/// <remarks>
/// <para>
/// The seam that keeps <see cref="AgentMode.PlanCanvas"/> from depending on a canvas. Application
/// produces plans; whether anything draws them is the host's business, exactly as with
/// <see cref="IAgentApproval"/> - and for the same reason: a layer that cannot see the UI must still be
/// able to ask it for something.
/// </para>
/// <para>
/// A plan is worth having with no canvas at all. It is in the transcript either way, which is why the
/// default implementation is not a stub that throws but an honest answer: recorded, not drawn.
/// </para>
/// </remarks>
public interface IAgentPlanSink
{
    /// <summary>
    /// Whether a plan handed over now would actually be drawn.
    /// </summary>
    /// <remarks>
    /// Read before the plan is asked for, so the model can be told the truth about what will happen to
    /// it. Telling a user to look at a canvas that is not there is worse than not mentioning one.
    /// </remarks>
    bool CanDraw { get; }

    /// <summary>
    /// Takes a plan, and says what became of it.
    /// </summary>
    /// <remarks>
    /// Failure is reported as <see cref="AgentPlanAcceptance.Drawn"/> false with a note, not by
    /// throwing. A canvas that cannot be drawn on must not lose the plan.
    /// </remarks>
    Task<AgentPlanAcceptance> AcceptAsync(AgentPlan plan, CancellationToken cancellationToken = default);
}
