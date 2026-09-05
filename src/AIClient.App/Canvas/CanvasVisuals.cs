using System.Windows;
using System.Windows.Media;
using AIClient.Domain.Graph;

namespace AIClient.App.Canvas;

/// <summary>
/// One node's retained visual: its drawing, its cached text, and the transform that
/// positions it.
/// </summary>
/// <remarks>
/// <para>
/// The drawing is made in local coordinates centred on (0, 0) at the node's design size,
/// and the node's world position lives on <see cref="Visual"/>'s transform. Moving a node
/// is therefore a transform write rather than a redraw, which is what keeps dragging
/// smooth no matter how expensive the node's contents are.
/// </para>
/// <para>
/// Selection, hover and dimming are content states (they change what is drawn, not where),
/// so they flag the visual dirty and it redraws on the next render pass. Only the nodes
/// whose state actually changed redraw - the whole point of per-node visuals.
/// </para>
/// </remarks>
public sealed class NodeVisual
{
    public required DrawingVisual Visual { get; init; }

    /// <summary>
    /// The node this visual currently shows. Replaced (not mutated) when the graph changes
    /// the node: the visual's drawing reads it on the next render, and the delta between
    /// the old and new node is what decides whether a redraw is needed.
    /// </summary>
    public required GraphNode Node { get; set; }

    /// <summary>Whether the node's drawing no longer matches its state and must be redrawn.</summary>
    public bool IsDirty { get; set; } = true;

    /// <summary>Whether the visual is currently attached to the canvas's visual tree (viewport culling).</summary>
    public bool IsAttached { get; set; }

    public NodeRenderState State { get; set; } = NodeRenderState.Default;
}

/// <summary>The content-determining state of a node drawing; changes force a redraw.</summary>
[Flags]
public enum NodeRenderState
{
    Default = 0,
    Hovered = 1 << 0,
    Selected = 1 << 1,
    /// <summary>Selection exists somewhere on the canvas and this node is not part of it.</summary>
    Dimmed = 1 << 2,
}

/// <summary>
/// One edge's retained visual: a cached curve geometry and a cached polyline used for
/// hit-testing.
/// </summary>
public sealed class EdgeVisual
{
    public required DrawingVisual Visual { get; init; }

    /// <summary>The edge this visual currently shows; replaced when the graph changes it.</summary>
    public required GraphEdge Edge { get; set; }

    public required string SourceId { get; init; }

    public required string TargetId { get; init; }

    /// <summary>The curve as drawn, in world coordinates; rebuilt when either endpoint moves.</summary>
    public PathGeometry? Curve { get; set; }

    /// <summary>Sampled points along the curve, for distance-based hit-testing.</summary>
    public Point[] Samples { get; set; } = [];

    public EdgeRenderState State { get; set; } = EdgeRenderState.Default;

    public bool IsDirty { get; set; } = true;

    public bool IsAttached { get; set; }
}

[Flags]
public enum EdgeRenderState
{
    Default = 0,
    Hovered = 1 << 0,
    Selected = 1 << 1,
    /// <summary>Selection exists and this edge is not incident to any selected node.</summary>
    Dimmed = 1 << 2,
    /// <summary>Incident to a selected node: highlighted as the neighbourhood of the selection.</summary>
    Related = 1 << 3,
}
