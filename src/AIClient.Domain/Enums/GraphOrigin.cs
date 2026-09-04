namespace AIClient.Domain.Enums;

/// <summary>
/// Who put a node or an edge into the graph.
/// </summary>
/// <remarks>
/// This is not bookkeeping. It carries an invariant the whole design leans on: an indexing pass
/// may only add, change and remove things whose origin is <see cref="Indexer"/>. Without it, the
/// second walk of the workspace would quietly delete every relationship a person drew by hand,
/// and the graph would be no more durable than a cache.
/// </remarks>
public enum GraphOrigin
{
    /// <summary>Created by a direct gesture: dragged out, typed in, linked up.</summary>
    User = 0,

    /// <summary>Derived from the workspace by an indexing pass.</summary>
    Indexer = 1,

    /// <summary>Proposed by a model during a chat turn and accepted by the user.</summary>
    Chat = 2,

    /// <summary>Produced by an agent run. The run itself is a node, referenced by the change.</summary>
    Agent = 3,

    /// <summary>Brought in from outside: another project, a previous export.</summary>
    Import = 4,
}
