namespace AIClient.Domain.Enums;

/// <summary>
/// Whether a graph node still corresponds to something real.
/// </summary>
public enum GraphNodeStatus
{
    /// <summary>Present and confirmed by the last indexing pass.</summary>
    Active = 0,

    /// <summary>
    /// The thing this node described is gone from the workspace.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted, because a node carries work the indexer did not create: its
    /// position on a canvas, the links a person drew from it, the decision recorded against it. A
    /// rename looks exactly like a deletion plus an addition from the outside, and silently
    /// dropping the old node would throw that work away every time a file moved. Removal is a
    /// separate, explicit act.
    /// </remarks>
    Missing = 1,

    /// <summary>Deliberately set aside by the user. Hidden from listings, still queryable.</summary>
    Archived = 2,
}
