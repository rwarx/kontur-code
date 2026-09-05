namespace AIClient.Domain.Graph;

/// <summary>
/// The kinds of relationship an edge can assert.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Contains"/> is structural (folder to child, plan to part); the rest are
/// dependencies of increasing specificity. The renderer draws them identically apart from
/// subtle arrow and label treatment - an edge's job is to be secondary to the nodes it joins,
/// and a gallery of line styles would compete with the two things at its ends.
/// </para>
/// <para>
/// The list is short for the same reason the node kinds are: an edge kind has to be chosen by
/// something - often a model - and every extra option is another way for two producers of the
/// same relationship to disagree about what to call it.
/// </para>
/// </remarks>
public enum GraphEdgeKind
{
    /// <summary>The source holds the target: a folder holds a file, a plan holds a part.</summary>
    Contains,

    /// <summary>The source needs the target to work.</summary>
    Depends,

    /// <summary>The source invokes the target at run time.</summary>
    Calls,

    /// <summary>The source realises the contract the target describes.</summary>
    Implements,

    /// <summary>They are connected, and the sender did not say how.</summary>
    Relates,

    /// <summary>The source is a step or plan concerned with the target.</summary>
    Plans,
}

/// <summary>
/// A directed relationship between two nodes.
/// </summary>
/// <remarks>
/// <para>
/// Immutable like the nodes it joins; changes arrive as <see cref="GraphChange"/> records
/// through the <see cref="GraphModel"/>. Direction is meaningful - <c>Contains</c> and
/// <c>Depends</c> read differently one way round than the other - so nothing in the model
/// quietly symmetrises an edge.
/// </para>
/// <para>
/// The id is minted by the creator and unique across the graph. Self-loops are refused at the
/// model rather than here, because a record cannot enforce relationships - only the graph as
/// a whole can, and rejecting one bad edge is worth more than validating every edge object
/// that may never be added.
/// </para>
/// </remarks>
public sealed record GraphEdge
{
    /// <summary>
    /// The edge's identity, stable and unique across the graph.
    /// </summary>
    /// <remarks>
    /// Deterministic id schemes ("c:ws:dir:src") let a producer re-send the same edge twice
    /// and have the second attempt rejected as a duplicate rather than added as a twin - the
    /// cheapest possible way of keeping a refresh idempotent.
    /// </remarks>
    public required string Id { get; init; }

    /// <summary>The node the relationship starts from: the container, the depender, the caller.</summary>
    public required string SourceId { get; init; }

    /// <summary>The node the relationship points at.</summary>
    public required string TargetId { get; init; }

    /// <summary>What the relationship asserts. Drives the arrow's treatment and nothing else.</summary>
    public GraphEdgeKind Kind { get; init; }

    /// <summary>Short text drawn along the edge when the kind alone does not say enough. Null when there is none.</summary>
    public string? Label { get; init; }
}
