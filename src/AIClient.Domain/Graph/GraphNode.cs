namespace AIClient.Domain.Graph;

/// <summary>
/// What a node on the canvas represents.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately wide: workspace files land on the canvas with the same type as plan parts and
/// future agent/model nodes, so the renderer deals with one shape of thing rather than one per
/// source. The kinds drive colour and iconography in the presentation layer, which is why they
/// are descriptive rather than structural - the domain itself never branches on them except to
/// sort and to count.
/// </para>
/// <para>
/// The vocabulary is shared with the plan kinds on purpose. A plan part and a workspace file
/// that answer the same description should answer to the same node kind, so a canvas holding
/// both looks like one drawing rather than two glued together. Translating a plan's kinds into
/// these happens at the application boundary; the domain does not know that plans exist.
/// </para>
/// </remarks>
public enum GraphNodeKind
{
    /// <summary>A free-standing annotation the user wrote, anchored nowhere but the canvas.</summary>
    Note,

    /// <summary>A real file, or a synthetic part that is best drawn as one.</summary>
    File,

    /// <summary>A directory, or the workspace root itself.</summary>
    Folder,

    /// <summary>A unit of code with a name: a class, a project, a package.</summary>
    Module,

    /// <summary>Something long-lived that other parts call.</summary>
    Service,

    /// <summary>A contract rather than an implementation.</summary>
    Interface,

    /// <summary>A table, an entity, a schema, a file format.</summary>
    Data,

    /// <summary>A screen, a page, a component the user sees.</summary>
    View,

    /// <summary>A test, or a tree of them.</summary>
    Test,

    /// <summary>Part of a plan, when a plan is drawn rather than built.</summary>
    Plan,

    /// <summary>A unit of work on the timeline: a step, a build, a run.</summary>
    Task,

    /// <summary>An assistant or model taking part in the work.</summary>
    Agent,

    /// <summary>A model or provider the work depends on.</summary>
    Model,

    /// <summary>Something the project depends on but does not contain.</summary>
    External,
}

/// <summary>
/// One node of the graph: a thing, and where it sits on the canvas.
/// </summary>
/// <remarks>
/// <para>
/// Immutable, like everything in the graph's data. The only way anything changes is a
/// <see cref="GraphChange"/> applied through the <see cref="GraphModel"/>, so a node that is
/// being drawn is a node that will still be there on the next frame - and "undo" is the same
/// code path as "load", because both replace one immutable set of nodes with another.
/// </para>
/// <para>
/// Position and size live here rather than in a separate layout store so that a snapshot is a
/// complete, persistable unit: undo, save and restore are then the same operation instead of
/// three that can disagree.
/// </para>
/// <para>
/// Ids are stable strings minted by whoever creates the node ("ws", "dir:src", "plan:...").
/// Two nodes with the same id are the same node; the model rejects a second node that claims
/// an id already in use, and everything from the timeline to the context builder refers to
/// nodes by id alone.
/// </para>
/// </remarks>
public sealed record GraphNode
{
    /// <summary>
    /// The node's identity, minted by its creator and stable for as long as the node exists.
    /// </summary>
    /// <remarks>
    /// Free-form but never empty: the model refuses to add a node without an id, because every
    /// later operation - moving, updating, removing, wiring an edge - reaches a node through
    /// this string.
    /// </remarks>
    public required string Id { get; init; }

    /// <summary>What this node is drawn as; drives colour and iconography, nothing structural.</summary>
    public GraphNodeKind Kind { get; init; }

    /// <summary>
    /// The one line the node is drawn and referred to by.
    /// </summary>
    /// <remarks>
    /// Required rather than optional because an untitled box is a thing nobody can talk about:
    /// the timeline, the context block and the outline view would all fall back to an id that
    /// was never meant for reading.
    /// </remarks>
    public required string Title { get; init; }

    /// <summary>A second, dimmer line under the title: a kind, a count, a hint. Null when there is none.</summary>
    public string? Subtitle { get; init; }

    /// <summary>Longer text shown when the node is opened. Null when there is none.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// A workspace-relative path when this node is a real thing on disk; null otherwise
    /// (plan parts and other synthetic nodes).
    /// </summary>
    /// <remarks>
    /// Carried rather than looked up from elsewhere so the node is self-describing: the
    /// context builder can name a file's location without asking the workspace to resolve it,
    /// and clicking a node can open the file without a second index to keep in step.
    /// </remarks>
    public string? Path { get; init; }

    /// <summary>
    /// Canvas position in world coordinates: the node's centre, so a node is a point plus a
    /// size to everything that lays one out or avoids one.
    /// </summary>
    /// <remarks>
    /// The renderer never writes it; layout does, through changes. Keeping it here rather
    /// than in a view model is what makes a saved canvas come back the way it was left.
    /// </remarks>
    public double X { get; init; }

    /// <summary>Canvas position in world coordinates: the node's centre. See <see cref="X"/>.</summary>
    public double Y { get; init; }

    /// <summary>The node's drawn width. Positive and finite; the model refuses anything else.</summary>
    public double Width { get; init; } = 200;

    /// <summary>The node's drawn height. Positive and finite; the model refuses anything else.</summary>
    public double Height { get; init; } = 64;

    /// <summary>
    /// Node-specific counter (dependencies, lines, parts) shown as metadata; presentation
    /// formats it, nobody computes from it.
    /// </summary>
    /// <remarks>
    /// Optional and informational. The graph never reads it back, which is what allows the
    /// indexer to put a file count on a folder and the plan sink to put a part count on a plan
    /// without either knowing about the other.
    /// </remarks>
    public int? Metric { get; init; }
}
