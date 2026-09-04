using AIClient.Application.DTOs;
using AIClient.Domain.Graph;

namespace AIClient.Application.Services;

/// <summary>
/// Turns containment into coordinates: a left-to-right tree, parents centred on their children.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately arithmetic rather than an engine. A force-directed simulation looks impressive on a
/// demo graph and is useless here for two reasons: it is not deterministic, so the same project
/// opens differently twice and nobody can build a spatial memory of it; and it ignores the one
/// relation the user already understands, which is what contains what. A tidy tree over
/// <c>Contains</c> and <c>Groups</c> reads like the thing being modelled, and it can be recomputed
/// identically on any machine.
/// </para>
/// <para>
/// Two entry points, because the two situations are different. <see cref="Arrange"/> is the
/// explicit "Auto Layout" gesture and moves everything the user has not pinned.
/// <see cref="PlaceMissing"/> runs after indexing and touches only nodes that have never had a
/// position - a re-index must never rearrange a surface somebody has organised.
/// </para>
/// </remarks>
public static class CanvasLayout
{
    /// <summary>
    /// The version of this arithmetic. Raise it whenever a change here would give an existing
    /// project a different shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stored positions outlive the code that computed them, and <see cref="PlaceMissing"/> is
    /// careful never to move a node that already has one. Both are right on their own and together
    /// they strand a surface: a project indexed while siblings ran in a single column stays a strip
    /// several screens tall long after the layout learned to wrap, because nothing ever asks it to
    /// think again. The canvas compares this number against
    /// <c>CanvasSettings.LayoutRevision</c> and, when it has moved, arranges once - which is the
    /// smallest thing that can be done about it and the only one that needs no schema.
    /// </para>
    /// <para>
    /// Revision 1 was a column per parent. Revision 2 wraps a long run of siblings into a block.
    /// </para>
    /// </remarks>
    public const int Revision = 2;

    /// <summary>
    /// The canonical position of every node in the graph, keeping pinned placements where they are.
    /// </summary>
    public static IReadOnlyList<CanvasPlacement> Arrange(
        GraphSnapshot graph,
        IEnumerable<CanvasPlacement>? existing = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var pinned = new Dictionary<Guid, CanvasPlacement>();

        if (existing is not null)
        {
            foreach (var placement in existing)
            {
                if (placement.IsPinned)
                {
                    pinned[placement.NodeId] = placement;
                }
            }
        }

        var computed = Compute(graph);

        if (pinned.Count == 0)
        {
            return computed;
        }

        var result = new List<CanvasPlacement>(computed.Count);

        foreach (var placement in computed)
        {
            result.Add(pinned.TryGetValue(placement.NodeId, out var kept) ? kept : placement);
        }

        return result;
    }

    /// <summary>
    /// Positions for nodes that have none yet, leaving every existing placement untouched.
    /// </summary>
    /// <remarks>
    /// A new node takes the position the full layout would have given it, which can land it on top
    /// of something the user dragged elsewhere. That is the right trade: an overlap is visible and
    /// fixed with one drag, whereas rearranging the surface to avoid it would silently undo work.
    /// </remarks>
    public static IReadOnlyList<CanvasPlacement> PlaceMissing(
        GraphSnapshot graph,
        IEnumerable<CanvasPlacement> existing)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(existing);

        var known = existing.Select(placement => placement.NodeId).ToHashSet();

        if (known.Count == 0)
        {
            return Compute(graph);
        }

        return [.. Compute(graph).Where(placement => !known.Contains(placement.NodeId))];
    }

    /// <summary>The rectangle a set of placements spans, for fitting the camera to it.</summary>
    public static CanvasBounds BoundsOf(IEnumerable<CanvasPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);

        return CanvasBounds.Around(placements.Select(placement => placement.Bounds));
    }

    /// <summary>
    /// One placement for every node in the graph: trees first, then whatever containment never
    /// reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three passes over each tree. The first walks containment, splitting every node's children as
    /// it goes into those that contain nothing and those that do; the second measures how tall each
    /// subtree needs to be; the third gives each subtree a band of exactly that height and fills it.
    /// Bands never overlap, so nothing has to be pushed out of the way afterwards.
    /// </para>
    /// <para>
    /// The split is what keeps the picture on a screen. A folder's files become a block a few columns
    /// wide, while its subfolders stay a column, so a project of two hundred files is a shape roughly
    /// as tall as it is wide instead of a ribbon sixty times taller than it is wide.
    /// </para>
    /// <para>
    /// Depth of nesting is unbounded once a person may draw <c>Groups</c> edges by hand, so every
    /// pass uses an explicit stack. Recursion here would mean a graph with a long chain crashes the
    /// application rather than drawing a long chain.
    /// </para>
    /// </remarks>
    private static List<CanvasPlacement> Compute(GraphSnapshot graph)
    {
        var placements = new List<CanvasPlacement>(graph.NodeCount);
        var visited = new HashSet<Guid>();
        var cursor = 0.0;

        var roots = graph.Roots();

        // Claimed up front so that one top-level tree cannot absorb another as a child - a node
        // grouped by hand may well have a parent and still deserve to be its own root.
        foreach (var root in roots)
        {
            visited.Add(root.Id);
        }

        foreach (var root in roots)
        {
            var tree = Walk(graph, root.Id, visited);
            var heights = Measure(tree);

            Fill(tree, heights, cursor, placements);

            // A wider gap between one top-level tree and the next than between siblings, so the
            // separation is visible without a frame around it.
            cursor += heights[tree.Root] + CanvasMetrics.LevelGap;
        }

        AppendOrphans(graph, visited, cursor, placements);

        return placements;
    }

    /// <summary>The horizontal distance from one containment level to the next.</summary>
    private const double Column = CanvasMetrics.NodeWidth + CanvasMetrics.LevelGap;

    /// <summary>The horizontal distance from one wrapped column of siblings to the next.</summary>
    private const double WrappedColumn = CanvasMetrics.NodeWidth + CanvasMetrics.SiblingGap;

    /// <summary>One top-level tree, flattened: who is under whom, how deep, and in what order.</summary>
    /// <remarks>
    /// <see cref="Order"/> holds only the nodes that contain something. A leaf is placed by its parent
    /// as part of a block and never needs a band of its own, so it appears in exactly one
    /// <see cref="Leaves"/> list and nowhere else.
    /// </remarks>
    private sealed record Tree(
        Guid Root,
        List<Guid> Order,
        Dictionary<Guid, List<Guid>> Leaves,
        Dictionary<Guid, List<Guid>> Branches,
        Dictionary<Guid, int> Depths);

    /// <summary>Reads one tree out of the graph, claiming every node it reaches.</summary>
    private static Tree Walk(GraphSnapshot graph, Guid root, HashSet<Guid> visited)
    {
        var order = new List<Guid> { };
        var leaves = new Dictionary<Guid, List<Guid>>();
        var branches = new Dictionary<Guid, List<Guid>>();
        var depths = new Dictionary<Guid, int> { [root] = 0 };
        var stack = new Stack<Guid>();

        stack.Push(root);

        // Pre-order, children pushed reversed, so a parent is always recorded before its children and
        // the sequence reads down the screen. Both later passes depend on that.
        while (stack.Count > 0)
        {
            var id = stack.Pop();

            order.Add(id);

            var mine = new List<Guid>();
            var nested = new List<Guid>();

            foreach (var child in graph.Children(id))
            {
                if (!visited.Add(child.Id))
                {
                    continue;
                }

                depths[child.Id] = depths[id] + 1;

                (graph.Children(child.Id).Count == 0 ? mine : nested).Add(child.Id);
            }

            leaves[id] = mine;
            branches[id] = nested;

            for (var i = nested.Count - 1; i >= 0; i--)
            {
                stack.Push(nested[i]);
            }
        }

        return new Tree(root, order, leaves, branches, depths);
    }

    /// <summary>How tall a band each subtree needs.</summary>
    private static Dictionary<Guid, double> Measure(Tree tree)
    {
        var heights = new Dictionary<Guid, double>(tree.Order.Count);

        // Backwards over a pre-order walk is bottom-up: every subtree a node needs has already been
        // measured by the time the node itself is reached.
        for (var i = tree.Order.Count - 1; i >= 0; i--)
        {
            var id = tree.Order[i];

            var block = BlockHeight(tree.Leaves[id].Count);
            var stacked = 0.0;

            foreach (var branch in tree.Branches[id])
            {
                stacked += heights[branch] + CanvasMetrics.SiblingGap;
            }

            if (stacked > 0)
            {
                stacked -= CanvasMetrics.SiblingGap;
            }

            var inner = block + stacked;

            if (block > 0 && stacked > 0)
            {
                inner += CanvasMetrics.SiblingGap;
            }

            // A node is at least its own height even with nothing under it.
            heights[id] = Math.Max(CanvasMetrics.NodeHeight, inner);
        }

        return heights;
    }

    /// <summary>Places one tree into a band starting at <paramref name="top"/>.</summary>
    private static void Fill(
        Tree tree,
        Dictionary<Guid, double> heights,
        double top,
        List<CanvasPlacement> placements)
    {
        var stack = new Stack<(Guid Id, double Top)>();

        stack.Push((tree.Root, top));

        while (stack.Count > 0)
        {
            var (id, bandTop) = stack.Pop();

            var leaves = tree.Leaves[id];
            var branches = tree.Branches[id];
            var column = tree.Depths[id] * Column;

            // Level with the middle of its own band, which is what makes the picture read as a tree
            // rather than as a list with indentation.
            placements.Add(CanvasPlacement.At(
                id,
                column,
                bandTop + ((heights[id] - CanvasMetrics.NodeHeight) / 2)));

            var columns = Columns(leaves.Count);

            // The block of everything this node contains that contains nothing itself, filled across
            // and then down, so it is read in the order the graph lists it.
            for (var i = 0; i < leaves.Count; i++)
            {
                placements.Add(CanvasPlacement.At(
                    leaves[i],
                    column + Column + ((i % columns) * WrappedColumn),
                    bandTop + ((i / columns) * (CanvasMetrics.NodeHeight + CanvasMetrics.SiblingGap))));
            }

            var cursor = bandTop + BlockHeight(leaves.Count);

            if (leaves.Count > 0 && branches.Count > 0)
            {
                cursor += CanvasMetrics.SiblingGap;
            }

            var tops = new double[branches.Count];

            for (var i = 0; i < branches.Count; i++)
            {
                tops[i] = cursor;
                cursor += heights[branches[i]] + CanvasMetrics.SiblingGap;
            }

            // Reversed, so the first branch is popped first and the order down the screen is the
            // order in the graph.
            for (var i = branches.Count - 1; i >= 0; i--)
            {
                stack.Push((branches[i], tops[i]));
            }
        }
    }

    /// <summary>How many columns a run of <paramref name="count"/> siblings wraps into.</summary>
    private static int Columns(int count) => count <= 0
        ? 0
        : (int)Math.Ceiling(count / (double)CanvasMetrics.WrapColumnAt);

    /// <summary>How many rows that run occupies once wrapped.</summary>
    private static int Rows(int count)
    {
        var columns = Columns(count);

        return columns == 0 ? 0 : (int)Math.Ceiling(count / (double)columns);
    }

    /// <summary>How tall that run is, gaps included.</summary>
    private static double BlockHeight(int count)
    {
        var rows = Rows(count);

        return rows == 0
            ? 0
            : (rows * CanvasMetrics.NodeHeight) + ((rows - 1) * CanvasMetrics.SiblingGap);
    }

    /// <summary>
    /// Grids whatever the containment walk never reached, below everything else.
    /// </summary>
    /// <remarks>
    /// Real graphs have such nodes: a decision recorded in chat that relates to a file without being
    /// inside anything, or a cycle drawn by hand. They cannot be dropped - a node with no placement
    /// is a node the user cannot see or select - and a square block below the trees is honest about
    /// there being no hierarchy to show.
    /// </remarks>
    private static void AppendOrphans(
        GraphSnapshot graph,
        HashSet<Guid> visited,
        double top,
        List<CanvasPlacement> placements)
    {
        var orphans = graph.Nodes
            .Where(node => !visited.Contains(node.Id))
            .OrderBy(node => node.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Key, StringComparer.Ordinal)
            .ToList();

        if (orphans.Count == 0)
        {
            return;
        }

        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(orphans.Count)));

        for (var i = 0; i < orphans.Count; i++)
        {
            placements.Add(CanvasPlacement.At(
                orphans[i].Id,
                (i % columns) * (CanvasMetrics.NodeWidth + CanvasMetrics.SiblingGap),
                top + ((i / columns) * (CanvasMetrics.NodeHeight + CanvasMetrics.SiblingGap))));
        }
    }
}
