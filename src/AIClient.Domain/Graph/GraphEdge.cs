using AIClient.Domain.Enums;

namespace AIClient.Domain.Graph;

/// <summary>
/// A directed, typed relationship between two nodes.
/// </summary>
/// <remarks>
/// This is the semantic half of the model and it has no geometry. "AuthService depends on
/// UserRepository" belongs here; "AuthService sits to the left of UserRepository" is a canvas
/// placement and is stored somewhere else entirely. Keeping the two apart is what stops a tidy-up
/// of a diagram from changing what the project means.
/// </remarks>
public sealed record GraphEdge
{
    public required Guid Id { get; init; }

    public required Guid FromId { get; init; }

    public required Guid ToId { get; init; }

    public required GraphEdgeKind Kind { get; init; }

    /// <summary>Why the relationship exists, when that is not obvious from the kind.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Ordering hint among edges of the same kind out of the same node, used where sequence
    /// matters - the steps of a pipeline, the members of a component.
    /// </summary>
    public int Order { get; init; }

    public GraphOrigin Origin { get; init; } = GraphOrigin.User;

    public Guid? SourceExecutionId { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool IsIndexerOwned => Origin == GraphOrigin.Indexer;

    /// <summary>The other end, given one end. Null when the node is not an endpoint at all.</summary>
    public Guid? Other(Guid nodeId) => nodeId == FromId ? ToId : nodeId == ToId ? FromId : null;
}
