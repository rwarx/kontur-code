using System.Diagnostics.CodeAnalysis;
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
    /// The definitions of the tools that can currently do something, which is what a request should
    /// carry.
    /// </summary>
    /// <remarks>
    /// Differs from <see cref="Definitions"/> only when a tool implements
    /// <see cref="IAgentToolAvailability"/> and is currently switched off, so in the ordinary case the
    /// same prebuilt list comes back and nothing is allocated. Withholding a tool is a courtesy to the
    /// model rather than a security boundary: the tool refuses the call regardless, and this only saves
    /// the step spent discovering that.
    /// </remarks>
    IReadOnlyList<AIToolDefinition> Available();

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
