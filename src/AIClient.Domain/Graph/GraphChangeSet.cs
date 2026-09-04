using AIClient.Domain.Enums;

namespace AIClient.Domain.Graph;

/// <summary>
/// An ordered batch of mutations, and the only way the graph ever changes.
/// </summary>
/// <remarks>
/// <para>
/// One object retires three separate mechanisms. A model's suggestion is a change set that has not
/// been applied, so proposing needs no machinery of its own. Undo is applying <see cref="Inverse"/>,
/// so neither does that. And the timeline is these, in order - which means history cannot drift
/// from what happened, because it is the same record that caused it.
/// </para>
/// <para>
/// Ordered, because mutations lean on each other: an edge cannot be added before its endpoints
/// exist, and a node cannot be removed before the edges that touch it.
/// </para>
/// </remarks>
public sealed record GraphChangeSet
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>One line in the user's terms. This is what a timeline entry says.</summary>
    public required string Summary { get; init; }

    public required IReadOnlyList<GraphMutation> Mutations { get; init; }

    /// <summary>Who is asking, which decides what the change is allowed to touch.</summary>
    public GraphOrigin Origin { get; init; } = GraphOrigin.User;

    public GraphChangeState State { get; init; } = GraphChangeState.Proposed;

    /// <summary>The agent run behind this change, when there was one.</summary>
    public Guid? SourceExecutionId { get; init; }

    /// <summary>What to apply to get back to the state before this one. Empty until applied.</summary>
    /// <remarks>
    /// Computed when the change is applied rather than when it is written, because inverting
    /// "remove this node" needs the node - and every edge that touched it - and only the graph as it
    /// stood at that moment knows them.
    /// </remarks>
    public IReadOnlyList<GraphMutation> Inverse { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? AppliedAt { get; init; }

    public bool CanRevert => State == GraphChangeState.Applied && Inverse.Count > 0;

    public static GraphChangeSet Create(
        string summary,
        GraphOrigin origin,
        params IReadOnlyList<GraphMutation> mutations) =>
        new() { Summary = summary, Origin = origin, Mutations = mutations };
}
