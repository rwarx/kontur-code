namespace AIClient.Domain.Graph;

/// <summary>
/// One elementary edit to the graph.
/// </summary>
/// <remarks>
/// <para>
/// Closed, and deliberately so: the private constructor keeps every case in this file, and the
/// compiler can then prove a switch over them is exhaustive. That matters more than it sounds. The
/// same list of mutations is consumed twice - once to produce the next snapshot, once to write
/// rows - and a case missing from one of those two switches is a graph that disagrees with its own
/// storage.
/// </para>
/// <para>
/// Kinds of node and edge are an open set of strings; the ways to change the graph are not. A new
/// kind of thing is data, a new kind of edit is code.
/// </para>
/// </remarks>
public abstract record GraphMutation
{
    private GraphMutation()
    {
    }

    /// <summary>Put a node into the graph.</summary>
    /// <remarks>
    /// An id that is already present is replaced rather than rejected, which keeps re-applying a
    /// change set harmless. Note that this is an upsert by <em>id</em>, not by key: whoever builds
    /// the mutation resolves the canonical key against the current snapshot first, so that a
    /// re-index updates the node a placement is attached to instead of minting a rival.
    /// </remarks>
    public sealed record AddNode(GraphNode Node) : GraphMutation;

    /// <summary>Replace a node wholesale - title, summary, source span, metadata, status.</summary>
    /// <remarks>
    /// Whole-record replacement rather than a field-by-field patch, because a node is an immutable
    /// record: the caller has already built the new value, and a patch would need a way to say
    /// "leave this one alone" for every field it does not mention. The inverse is this same
    /// mutation carrying the old value, which is why undo costs nothing to express.
    /// </remarks>
    public sealed record UpdateNode(GraphNode Node) : GraphMutation;

    /// <summary>Remove a node, and with it every edge that touched it.</summary>
    /// <remarks>
    /// The cascade is not a convenience. An edge with one end missing is exactly what
    /// <see cref="GraphSnapshot"/> drops when it loads, so leaving those rows behind would leave
    /// storage holding relationships that no reader can ever see.
    /// </remarks>
    public sealed record RemoveNode(Guid NodeId) : GraphMutation;

    /// <summary>Put an edge into the graph.</summary>
    /// <remarks>
    /// Doubles as the update, for the same reason there is no <c>UpdateEdge</c>: an edge is two
    /// endpoints, a kind, a label and an ordering hint, so changing one means handing over the
    /// whole thing anyway.
    /// </remarks>
    public sealed record AddEdge(GraphEdge Edge) : GraphMutation;

    public sealed record RemoveEdge(Guid EdgeId) : GraphMutation;
}
