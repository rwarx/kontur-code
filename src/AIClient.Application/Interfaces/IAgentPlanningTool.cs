namespace AIClient.Application.Interfaces;

/// <summary>
/// Implemented by a tool that belongs to the planning modes rather than to a build.
/// </summary>
/// <remarks>
/// <para>
/// A marker, because there is exactly one question to answer and no data to carry: which side of the
/// mode line the tool sits on. The alternative was a name test in the policy, which works until a
/// second planning tool exists and then fails silently by offering it in the wrong mode.
/// </para>
/// <para>
/// The line runs both ways. A planning tool is withheld from a build - a run that is carrying the work
/// out has no use for recording what it would have done - and the tools that change things are withheld
/// from a plan, which is decided by <see cref="AIClient.Application.DTOs.AgentToolRisk"/> rather than by
/// this.
/// </para>
/// </remarks>
public interface IAgentPlanningTool;
