using System.Diagnostics.CodeAnalysis;
using AIClient.Application.DTOs;
using AIClient.Domain.Models;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Every tool the agent has, and the same set in the shape a provider wants.
/// </summary>
/// <remarks>
/// The two views have to be built from one list or they will drift: a tool offered to the model but
/// not resolvable by name produces a call nothing can answer, and one resolvable but never offered is
/// dead code the model has no way to reach.
/// </remarks>
public interface IAgentToolRegistry
{
    /// <summary>The tools, in the order they are offered to the model.</summary>
    IReadOnlyList<IAgentTool> Tools { get; }

    /// <summary>
    /// The same tools as provider definitions, ready to attach to a request.
    /// </summary>
    /// <remarks>
    /// Built once. The schemas are constants, and rebuilding them on every step of every turn would
    /// re-parse the same text for nothing.
    /// </remarks>
    IReadOnlyList<AIToolDefinition> Definitions { get; }

    /// <summary>
    /// The definitions of the tools that can currently do something in this mode, which is what a
    /// request should carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two filters, and they are different in kind. The mode decides what a run of that kind may call at
    /// all - a plan is offered nothing that writes - and that part is a rule, enforced again when the
    /// call comes back. <see cref="IAgentToolAvailability"/> decides whether a tool the mode allows is
    /// currently switched on, and withholding it is a courtesy to the model rather than a boundary: the
    /// tool refuses the call regardless, and this only saves the step spent discovering that.
    /// </para>
    /// <para>
    /// Differs from <see cref="Definitions"/> only where one of those two applies, so the common case
    /// hands back a prebuilt list and allocates nothing.
    /// </para>
    /// </remarks>
    IReadOnlyList<AIToolDefinition> Available(AgentMode mode = AgentMode.Build);

    /// <summary>
    /// Finds the tool a call names.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, and false rather than an exception for a name nothing matches: a model that
    /// invents a tool has to be told so as a tool result, which is the one thing that stops it
    /// inventing the same one again on the next step.
    /// </remarks>
    bool TryGet(string? name, [NotNullWhen(true)] out IAgentTool? tool);
}
