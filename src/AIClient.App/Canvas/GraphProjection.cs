using System.Windows;
using AIClient.Domain.Graph;

namespace AIClient.App.Canvas;

/// <summary>
/// The read-side bridge between the graph's snapshots and the canvas: takes two versions
/// and says exactly what changed, in terms the renderer can apply cheaply.
/// </summary>
/// <remarks>
/// <para>
/// The renderer must not re-create a thousand node visuals because one file appeared, and
/// it must not diff a thousand nodes by hand on the UI thread every time the graph service
/// raises a change. This class owns that comparison once, so the scene's work is purely
/// applying the delta: add, remove, move, or change.
/// </para>
/// <para>
/// A node "moved" when its position or size changed; it "changed" when anything else did
/// (title, kind, subtitle - the things a node visual re-renders for). The split matters
/// because moving is a transform update and changing is a content rebuild, an order of
/// magnitude apart in cost while dragging.
/// </para>
/// </remarks>
public static class GraphProjection
{
    /// <summary>What changed between two snapshots, computed in one pass.</summary>
    public readonly record struct Delta(
        IReadOnlyList<GraphNode> AddedNodes,
        IReadOnlyList<GraphNode> RemovedNodeIds,
        IReadOnlyList<GraphNode> MovedNodes,
        IReadOnlyList<GraphNode> ChangedNodes,
        IReadOnlyList<GraphEdge> AddedEdges,
        IReadOnlyList<string> RemovedEdgeIds,
        IReadOnlyList<GraphEdge> ChangedEdges);

    private static readonly Delta Empty = new([], [], [], [], [], [], []);

    public static Delta Diff(GraphSnapshot before, GraphSnapshot after)
    {
        if (before.Version == after.Version)
        {
            return Empty;
        }

        if (before.Nodes.Count == 0 && before.Edges.Count == 0)
        {
            return new Delta(
                after.Nodes,
                [],
                [],
                [],
                after.Edges,
                [],
                []);
        }

        List<GraphNode>? added = null;
        List<GraphNode>? removed = null;
        List<GraphNode>? moved = null;
        List<GraphNode>? changed = null;

        foreach (var node in after.Nodes)
        {
            if (!before.TryGetNode(node.Id, out var prior))
            {
                (added ??= []).Add(node);
                continue;
            }

            if (prior.X != node.X || prior.Y != node.Y || prior.Width != node.Width || prior.Height != node.Height)
            {
                (moved ??= []).Add(node);
            }

            if (prior.Title != node.Title || prior.Subtitle != node.Subtitle
                || prior.Detail != node.Detail || prior.Kind != node.Kind
                || prior.Path != node.Path || prior.Metric != node.Metric)
            {
                (changed ??= []).Add(node);
            }
        }

        foreach (var node in before.Nodes)
        {
            if (!after.TryGetNode(node.Id, out _))
            {
                (removed ??= []).Add(node);
            }
        }

        List<GraphEdge>? addedEdges = null;
        List<string>? removedEdges = null;
        List<GraphEdge>? changedEdges = null;

        foreach (var edge in after.Edges)
        {
            if (!before.TryGetEdge(edge.Id, out var prior))
            {
                (addedEdges ??= []).Add(edge);
            }
            else if (prior.Kind != edge.Kind || prior.Label != edge.Label
                || prior.SourceId != edge.SourceId || prior.TargetId != edge.TargetId)
            {
                (changedEdges ??= []).Add(edge);
            }
        }

        foreach (var edge in before.Edges)
        {
            if (!after.TryGetEdge(edge.Id, out _))
            {
                (removedEdges ??= []).Add(edge.Id);
            }
        }

        return new Delta(
            added ?? [],
            removed ?? [],
            moved ?? [],
            changed ?? [],
            addedEdges ?? [],
            removedEdges ?? [],
            changedEdges ?? []);
    }

    /// <summary>The world rectangle covering every node - what "fit to content" frames.</summary>
    public static Rect ContentBounds(GraphSnapshot snapshot)
    {
        if (snapshot.Nodes.Count == 0)
        {
            return Rect.Empty;
        }

        var bounds = Rect.Empty;

        foreach (var node in snapshot.Nodes)
        {
            var rect = new Rect(
                node.X - node.Width / 2,
                node.Y - node.Height / 2,
                node.Width,
                node.Height);

            bounds = bounds.IsEmpty ? rect : Rect.Union(bounds, rect);
        }

        return bounds;
    }

    /// <summary>The same, for a subset - what "focus selection" frames, with margin.</summary>
    public static Rect SelectionBounds(GraphSnapshot snapshot, IReadOnlyCollection<string> nodeIds)
    {
        if (nodeIds.Count == 0)
        {
            return Rect.Empty;
        }

        var bounds = Rect.Empty;

        foreach (var id in nodeIds)
        {
            if (!snapshot.TryGetNode(id, out var node))
            {
                continue;
            }

            var rect = new Rect(
                node.X - node.Width / 2,
                node.Y - node.Height / 2,
                node.Width,
                node.Height);

            bounds = bounds.IsEmpty ? rect : Rect.Union(bounds, rect);
        }

        bounds.Inflate(60, 60);

        return bounds;
    }
}
