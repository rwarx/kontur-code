using AIClient.Domain.Enums;

namespace AIClient.Domain.Entities;

/// <summary>
/// One stored relationship between two nodes.
/// </summary>
/// <remarks>
/// The kind is part of the identity, not a label on it: <c>A depends_on B</c> and <c>A calls B</c>
/// are two facts and both are allowed to exist, which is why the unique index covers
/// (<see cref="FromId"/>, <see cref="ToId"/>, <see cref="Kind"/>) rather than the pair alone.
/// </remarks>
public sealed class GraphEdgeRow
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid FromId { get; set; }

    public Guid ToId { get; set; }

    /// <summary>Canonical kind text, e.g. <c>contains</c> or <c>depends_on</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>What to draw on the edge when the kind alone is not enough.</summary>
    public string? Label { get; set; }

    /// <summary>Ordering among edges leaving the same node, so a listing is stable.</summary>
    public int Order { get; set; }

    public GraphOrigin Origin { get; set; } = GraphOrigin.User;

    public Guid? SourceExecutionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
