namespace AIClient.Domain.Enums;

/// <summary>
/// Where a change set stands.
/// </summary>
/// <remarks>
/// Every change passes through this, including the ones a person makes by hand, so that a model's
/// suggestion and a drag of the mouse are the same kind of object. A direct edit is recorded as
/// <see cref="Applied"/> at once; a model's is written as <see cref="Proposed"/> and waits for a
/// decision.
/// </remarks>
public enum GraphChangeState
{
    /// <summary>On record, not in the graph. What a canvas draws as ghosts awaiting a decision.</summary>
    Proposed = 0,

    /// <summary>In the graph, and revertable for as long as its inverse is on record.</summary>
    Applied = 1,

    /// <summary>A proposal that was turned down. Kept, so the same suggestion is not made twice.</summary>
    Discarded = 2,

    /// <summary>Was applied, then undone by applying its inverse.</summary>
    Reverted = 3,
}
