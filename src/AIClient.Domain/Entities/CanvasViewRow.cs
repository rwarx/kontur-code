namespace AIClient.Domain.Entities;

/// <summary>
/// One saved way of looking at the graph: which part of it, from where, at what zoom.
/// </summary>
/// <remarks>
/// <para>
/// Everything in this table and the two beside it is spatial. Delete all three and not one fact
/// about the project is lost - the nodes, the relationships, the decisions and the history are in
/// the graph tables, and the next time a view opens the layout is computed again. That is the whole
/// content of "Canvas is a projection", expressed as a property of the schema rather than as a
/// comment: there is no column here that anything could reason about.
/// </para>
/// <para>
/// <see cref="RootNodeId"/> is how levels of abstraction work. Stepping inside a component opens a
/// view rooted at it, rather than adding depth to one endless surface. The foreign key clears
/// itself if that node is ever deleted, which leaves a view of the whole graph instead of a view
/// of nothing.
/// </para>
/// </remarks>
public sealed class CanvasViewRow
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    /// <summary>The node this view is scoped to, or null for the whole graph.</summary>
    public Guid? RootNodeId { get; set; }

    /// <summary>How many hops out from the root to draw.</summary>
    public int Depth { get; set; } = 2;

    public double PanX { get; set; }

    public double PanY { get; set; }

    /// <summary>1.0 is actual size. Clamped by the surface, stored as given.</summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>Which automatic arrangement to fall back on for nodes with no placement.</summary>
    public string LayoutMode { get; set; } = "tree";

    /// <summary>True for the view the shell opens on start.</summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
