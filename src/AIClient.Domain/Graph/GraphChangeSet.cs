namespace AIClient.Domain.Graph;

/// <summary>
/// Who produced a change set - decides how the timeline describes it and how much it is
/// trusted.
/// </summary>
/// <remarks>
/// Recorded with every change set rather than inferred afterwards, because the answer is
/// only knowable at the moment of sending: an undo applies the same changes as the run it
/// reverses, but nobody should read them the same way.
/// </remarks>
public enum GraphChangeOrigin
{
    /// <summary>The user, dragging or typing on the canvas. Shown as their own doing.</summary>
    User,

    /// <summary>A model, proposing changes through a tool call. Shown as a proposal, and previewable.</summary>
    Agent,

    /// <summary>The workspace indexer, reflecting what the disk says. Shown as background fact.</summary>
    Indexer,

    /// <summary>A layout pass, repositioning nodes. Shown quietly, if at all.</summary>
    Layout,

    /// <summary>An undo, restoring an earlier snapshot.</summary>
    Undo,

    /// <summary>A redo, re-applying a snapshot an undo removed.</summary>
    Redo,
}

/// <summary>
/// One step of a change set.
/// </summary>
/// <remarks>
/// A closed hierarchy: the model switches on these, and an unknown kind is a compile error
/// rather than a runtime surprise. The alternative - a kind enum plus a bag of optional
/// properties - puts every mistake one layer later, where it becomes a node that was moved
/// by zero because somebody forgot which field to read.
/// </remarks>
public abstract record GraphChange;

/// <summary>
/// Puts a node on the canvas.
/// </summary>
/// <param name="Node">The node to add, complete with its position.</param>
/// <remarks>
/// Rejected when the id is already in use, when the node has no title, or when its size is
/// zero, negative or not a number; see <see cref="GraphModel"/> for the exact refusals.
/// </remarks>
public sealed record AddNode(GraphNode Node) : GraphChange;

/// <summary>
/// Rewrites the descriptive fields of a node that is already there.
/// </summary>
/// <param name="NodeId">The node to update, by id.</param>
/// <param name="Title">The new title, or null to leave it unchanged.</param>
/// <param name="Subtitle">The new subtitle, or null to leave it unchanged.</param>
/// <param name="Detail">The new detail, or null to leave it unchanged.</param>
/// <param name="Kind">The new kind, or null to leave it unchanged.</param>
/// <remarks>
/// <para>
/// A null member means "leave it alone", and that is the whole protocol - which leaves one
/// thing it cannot say: "clear this value". Clearing a subtitle means removing the node and
/// adding it back. That limitation is accepted on purpose rather than fixed with a
/// sentinel, because a sentinel ("empty string means clear, but only sometimes") has to be
/// remembered by every producer of a change, while remove-and-add is already the honest
/// way to replace a node outright.
/// </para>
/// <para>
/// Position is deliberately absent: moving is a separate change (<see cref="MoveNode"/>),
/// because the timeline wants to describe "renamed" and "moved" differently even when the
/// same author did both in the same breath.
/// </para>
/// </remarks>
public sealed record UpdateNode(
    string NodeId,
    string? Title = null,
    string? Subtitle = null,
    string? Detail = null,
    GraphNodeKind? Kind = null) : GraphChange;

/// <summary>
/// Moves a node to a new canvas position.
/// </summary>
/// <param name="NodeId">The node to move, by id.</param>
/// <param name="X">The new world X coordinate: the node's centre.</param>
/// <param name="Y">The new world Y coordinate: the node's centre.</param>
/// <remarks>
/// Rejected when the node does not exist or the position is not a number. Everything else
/// is legal - including positions that overlap other nodes, because an overlap is
/// sometimes exactly what a drag looks like mid-flight, and resolving it is layout's job.
/// </remarks>
public sealed record MoveNode(string NodeId, double X, double Y) : GraphChange;

/// <summary>
/// Takes a node off the canvas, together with every edge joined to it.
/// </summary>
/// <param name="NodeId">The node to remove, by id.</param>
/// <remarks>
/// Removing a node that is not there is applied as a successful no-op, so an indexer
/// refresh and an agent edit that both delete the same file do not race each other into
/// an error. The incident edges go with the node: an edge joining a node that is not
/// there is not a relationship, it is leftover wiring.
/// </remarks>
public sealed record RemoveNode(string NodeId) : GraphChange;

/// <summary>
/// Joins two nodes that are already on the canvas.
/// </summary>
/// <param name="Edge">The edge to add, complete with its direction and kind.</param>
/// <remarks>
/// Rejected when the id is in use, when either endpoint is missing, or when the edge joins
/// a node to itself; see <see cref="GraphModel"/> for the exact refusals.
/// </remarks>
public sealed record AddEdge(GraphEdge Edge) : GraphChange;

/// <summary>
/// Rewrites the kind or label of an edge that is already there.
/// </summary>
/// <param name="EdgeId">The edge to update, by id.</param>
/// <param name="Kind">The new kind, or null to leave it unchanged.</param>
/// <param name="Label">The new label, or null to leave it unchanged.</param>
/// <remarks>
/// Null means "leave it alone"; clearing a label means removing the edge and adding it
/// back, for the same reason as on <see cref="UpdateNode"/>.
/// </remarks>
public sealed record UpdateEdge(string EdgeId, GraphEdgeKind? Kind = null, string? Label = null) : GraphChange;

/// <summary>
/// Takes an edge off the canvas.
/// </summary>
/// <param name="EdgeId">The edge to remove, by id.</param>
/// <remarks>
/// Removing an edge that is not there is applied as a successful no-op - the same
/// idempotence rule as <see cref="RemoveNode"/>, for the same reason.
/// </remarks>
public sealed record RemoveEdge(string EdgeId) : GraphChange;

/// <summary>
/// A titled bundle of changes to be applied to the graph as one step.
/// </summary>
/// <remarks>
/// <para>
/// This is the unit the timeline shows and the agent pipeline proposes: AI produces a
/// <see cref="GraphChangeSet"/>, the application previews it, the user accepts or rejects
/// it, the <see cref="GraphModel"/> applies it, and the timeline remembers it. Keeping the
/// unit the same all the way along that chain is what lets an undo be one step rather than
/// a guess about how many changes belonged together.
/// </para>
/// <para>
/// Changes are applied in order, so a set can create a node and then wire edges to it. The
/// set is best-effort, not transactional: a change that cannot be applied is rejected with
/// a reason and the rest still land, because a model-authored set that is nine tenths
/// sensible is worth nine tenths.
/// </para>
/// </remarks>
public sealed record GraphChangeSet
{
    /// <summary>
    /// One line naming the whole change set, as the timeline will show it.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>What the change set is for, in a sentence or two. Null when the title says it all.</summary>
    public string? Description { get; init; }

    /// <summary>Who produced the change set; see <see cref="GraphChangeOrigin"/>.</summary>
    public GraphChangeOrigin Origin { get; init; }

    /// <summary>The steps, in the order they should be applied. May be empty - an honest no-op.</summary>
    public IReadOnlyList<GraphChange> Changes { get; init; } = [];
}
