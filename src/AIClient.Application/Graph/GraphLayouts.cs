using AIClient.Domain.Graph;

namespace AIClient.Application.Graph;

/// <summary>
/// Where nodes go when nobody has placed them yet.
/// </summary>
/// <remarks>
/// <para>
/// Layout is a change set like any other: these methods compute positions, and the host
/// applies them through the graph service as <see cref="MoveNode"/> changes, so a layout
/// pass is undoable and persisted for free instead of being a side door that writes
/// positions where the timeline cannot see them.
/// </para>
/// <para>
/// Every method here is a pure function of the snapshot: same graph in, same positions
/// out, with no randomness that is not seeded. Deterministic layout is what makes a saved
/// canvas reproducible and a refresh that re-lays-out new nodes stable rather than a
/// reshuffle.
/// </para>
/// <para>
/// Positions are node centres in world coordinates, matching
/// <see cref="GraphNode.X"/> and <see cref="GraphNode.Y"/> - a node is a point plus a
/// size to everything in this class.
/// </para>
/// </remarks>
public static class GraphLayouts
{
    /// <summary>
    /// Horizontal distance between the centres of one depth level and the next in
    /// <see cref="Layered"/>: a node's default width plus breathing room for the edges
    /// that have to run between the columns.
    /// </summary>
    private const double LayerSpacingX = 300;

    /// <summary>Vertical distance between node centres within a layer in <see cref="Layered"/>.</summary>
    private const double LayerSpacingY = 120;

    /// <summary>Rows per column in <see cref="Grid"/>.</summary>
    private const int GridRows = 12;

    /// <summary>Horizontal distance between column centres in <see cref="Grid"/>.</summary>
    private const double GridSpacingX = 260;

    /// <summary>Vertical distance between row centres in <see cref="Grid"/>.</summary>
    private const double GridSpacingY = 110;

    /// <summary>Directions tried per ring in <see cref="PlaceNear"/>: enough to look like a spiral, few enough to stay cheap.</summary>
    private const int PlaceDirections = 16;

    /// <summary>Radius step between rings in <see cref="PlaceNear"/>.</summary>
    private const double PlaceRingStep = 60;

    /// <summary>How many rings <see cref="PlaceNear"/> tries before giving up and returning the last candidate.</summary>
    private const int PlaceMaxRings = 10;

    /// <summary>
    /// Above this many nodes <see cref="Force"/> refuses to run - O(n²) stops being a
    /// detail and starts being a hang.
    /// </summary>
    private const int ForceMaxNodes = 500;

    /// <summary>
    /// Area per node used by <see cref="Force"/> to derive its forces; roughly a
    /// 170-square around each node, which is where the default node size plus gaps land.
    /// </summary>
    private const double ForceAreaPerNode = 30_000;

    /// <summary>
    /// Layered layout for containment trees (workspace graphs): one column per depth
    /// level, ordered by kind then title within a layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <see cref="GraphEdgeKind.Contains"/> edges shape the layers - a workspace
    /// graph's other relationships are rare and would fold the tree into a knot. Depth is
    /// measured from the nodes nothing contains (the workspace root, top-level folders),
    /// and each layer is drawn at <c>X = depth * 300</c> with <c>Y = index * 120</c>.
    /// </para>
    /// <para>
    /// Nodes nothing reaches - an entry whose parent is absent, or a cycle - are appended
    /// to the deepest layer rather than dropped: a node that cannot be placed is still a
    /// node the user asked to see. When nothing is a root at all (a pure cycle), every
    /// node is treated as one.
    /// </para>
    /// <para>
    /// A node reachable by two paths keeps the depth of its first visit, which is what
    /// keeps the walk linear instead of exponential in a tangled tree.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, (double X, double Y)> Layered(GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var positions = new Dictionary<string, (double X, double Y)>(snapshot.Nodes.Count, StringComparer.Ordinal);
        if (snapshot.Nodes.Count == 0)
        {
            return positions;
        }

        // Containment only, and never downwards from a node to itself.
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var contained = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edge in snapshot.Edges)
        {
            if (edge.Kind != GraphEdgeKind.Contains || edge.SourceId == edge.TargetId)
            {
                continue;
            }

            if (!children.TryGetValue(edge.SourceId, out var bucket))
            {
                bucket = [];
                children[edge.SourceId] = bucket;
            }

            bucket.Add(edge.TargetId);
            contained.Add(edge.TargetId);
        }

        // Roots: nothing contains them. When everything is contained (a cycle), every node
        // is a root - a bad tree still deserves a layout.
        var roots = snapshot.Nodes
            .Where(node => !contained.Contains(node.Id))
            .OrderBy(node => node.Kind)
            .ThenBy(node => node.Title, StringComparer.Ordinal)
            .Select(node => node.Id)
            .ToList();

        var depth = new Dictionary<string, int>(snapshot.Nodes.Count, StringComparer.Ordinal);
        var frontier = new List<string>(roots.Count);

        foreach (var root in roots)
        {
            if (depth.TryAdd(root, 0))
            {
                frontier.Add(root);
            }
        }

        while (frontier.Count > 0)
        {
            var next = new List<string>();

            foreach (var id in frontier)
            {
                if (!children.TryGetValue(id, out var bucket))
                {
                    continue;
                }

                foreach (var child in bucket)
                {
                    if (depth.TryAdd(child, depth[id] + 1))
                    {
                        next.Add(child);
                    }
                }
            }

            frontier = next;
        }

        // Whatever the walk never reached joins the deepest layer.
        var deepest = 0;
        foreach (var level in depth.Values)
        {
            if (level > deepest)
            {
                deepest = level;
            }
        }
        foreach (var node in snapshot.Nodes)
        {
            if (!depth.ContainsKey(node.Id))
            {
                depth[node.Id] = deepest;
            }
        }

        // Gather per layer, then order each layer by kind and title: the columns read as
        // a sorted list, and the same tree always lays out the same way.
        var layers = new List<List<GraphNode>>();

        foreach (var node in snapshot.Nodes)
        {
            var level = depth[node.Id];

            while (layers.Count <= level)
            {
                layers.Add([]);
            }

            layers[level].Add(node);
        }

        for (var level = 0; level < layers.Count; level++)
        {
            var ordered = layers[level]
                .OrderBy(node => node.Kind)
                .ThenBy(node => node.Title, StringComparer.Ordinal)
                .ToList();

            for (var index = 0; index < ordered.Count; index++)
            {
                var node = ordered[index];
                positions[node.Id] = (level * LayerSpacingX, index * LayerSpacingY);
            }
        }

        return positions;
    }

    /// <summary>
    /// Force-directed layout for relationship graphs (plans): seeded
    /// Fruchterman-Reingold, fixed iteration count, anchored at the first node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O(n²) per iteration, which is fine for plan-sized graphs (tens of parts). Above
    /// <see cref="ForceMaxNodes"/> nodes it refuses - returns an empty result - and the
    /// host falls back to <see cref="Grid"/>. Refusing is better than degrading: a layout
    /// that visibly hangs is worse than one that is visibly boring.
    /// </para>
    /// <para>
    /// Pure: it lays out every node the snapshot holds, ignoring where those nodes
    /// already sit. To keep existing positions, the caller splits the set - lays out only
    /// the new nodes, then places stragglers with <see cref="PlaceNear"/> around a node
    /// they relate to - rather than asking this method to guess which positions are
    /// meaningful.
    /// </para>
    /// <para>
    /// The first node in the snapshot is pinned where it starts, at the origin; everything
    /// else settles around it on a jittered circle whose jitter comes from a seeded
    /// <see cref="Random"/>, so the same snapshot, seed and iteration count always
    /// produce the same picture - and a re-run does not drift the way an unseeded one
    /// would.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, (double X, double Y)> Force(
        GraphSnapshot snapshot,
        int seed = 7,
        int iterations = 250)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var positions = new Dictionary<string, (double X, double Y)>(snapshot.Nodes.Count, StringComparer.Ordinal);

        if (snapshot.Nodes.Count == 0 || snapshot.Nodes.Count > ForceMaxNodes)
        {
            return positions;
        }

        var count = snapshot.Nodes.Count;
        var area = count * ForceAreaPerNode;
        var equilibrium = Math.Sqrt(area / count);
        var ringRadius = Math.Sqrt(area) / 2;

        var ids = new string[count];
        var indexById = new Dictionary<string, int>(count, StringComparer.Ordinal);

        for (var index = 0; index < count; index++)
        {
            var id = snapshot.Nodes[index].Id;
            ids[index] = id;

            // First occurrence wins: a duplicate id can only come from a hand-edited file,
            // and the layout needs one point per node, not a crash.
            indexById.TryAdd(id, index);
        }

        var links = new List<(int Source, int Target)>();

        foreach (var edge in snapshot.Edges)
        {
            if (edge.SourceId == edge.TargetId)
            {
                continue;
            }

            if (indexById.TryGetValue(edge.SourceId, out var source) && indexById.TryGetValue(edge.TargetId, out var target))
            {
                links.Add((source, target));
            }
        }

        var random = new Random(seed);
        var x = new double[count];
        var y = new double[count];

        // Node 0 is the anchor and stays at the origin; the rest start on a jittered circle
        // around it so no two nodes ever begin stacked.
        for (var index = 1; index < count; index++)
        {
            var angle = (2 * Math.PI * index / count) + (random.NextDouble() - 0.5) * 0.5;
            var radius = ringRadius * (0.9 + 0.2 * random.NextDouble());
            x[index] = radius * Math.Cos(angle);
            y[index] = radius * Math.Sin(angle);
        }

        var initialTemperature = Math.Sqrt(area) / 8;

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var temperature = initialTemperature * (1 - (double)iteration / iterations);
            var displacementX = new double[count];
            var displacementY = new double[count];

            // Repulsion: every pair pushes apart with k^2 / d, so no two nodes ever
            // collapse onto one point.
            for (var i = 0; i < count; i++)
            {
                for (var j = i + 1; j < count; j++)
                {
                    var deltaX = x[i] - x[j];
                    var deltaY = y[i] - y[j];
                    var distance = Math.Max(1, Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY)));
                    var force = (equilibrium * equilibrium) / distance;
                    var directionX = deltaX / distance;
                    var directionY = deltaY / distance;

                    displacementX[i] += directionX * force;
                    displacementY[i] += directionY * force;
                    displacementX[j] -= directionX * force;
                    displacementY[j] -= directionY * force;
                }
            }

            // Attraction: each edge pulls its ends together with d^2 / k, which is what
            // makes related nodes end up near each other rather than merely not stacked.
            foreach (var (source, target) in links)
            {
                var deltaX = x[source] - x[target];
                var deltaY = y[source] - y[target];
                var distance = Math.Max(1, Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY)));
                var force = (distance * distance) / equilibrium;
                var directionX = deltaX / distance;
                var directionY = deltaY / distance;

                displacementX[source] -= directionX * force;
                displacementY[source] -= directionY * force;
                displacementX[target] += directionX * force;
                displacementY[target] += directionY * force;
            }

            // Displacement capped by a linearly cooling temperature: early iterations make
            // coarse moves, late ones only tidy, and the layout settles instead of
            // oscillating forever.
            for (var index = 1; index < count; index++)
            {
                var magnitude = Math.Sqrt((displacementX[index] * displacementX[index])
                    + (displacementY[index] * displacementY[index]));

                if (magnitude > temperature && magnitude > 0)
                {
                    var scale = temperature / magnitude;
                    displacementX[index] *= scale;
                    displacementY[index] *= scale;
                }

                x[index] += displacementX[index];
                y[index] += displacementY[index];
            }
        }

        for (var index = 0; index < count; index++)
        {
            positions[ids[index]] = (x[index], y[index]);
        }

        return positions;
    }

    /// <summary>
    /// Simple grid fallback for very large graphs: columns of 12, spacing 260 by 110,
    /// ordered by kind then title.
    /// </summary>
    /// <remarks>
    /// No structure is honoured at all, on purpose. A graph too big for <see cref="Force"/>
    /// is a graph the user will read by scrolling and searching, and a uniform grid - where
    /// the same snapshot always puts the same node in the same cell - serves that better
    /// than a layout that pretends to show structure it cannot resolve.
    /// </remarks>
    public static IReadOnlyDictionary<string, (double X, double Y)> Grid(GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var ordered = snapshot.Nodes
            .OrderBy(node => node.Kind)
            .ThenBy(node => node.Title, StringComparer.Ordinal)
            .ToList();

        var positions = new Dictionary<string, (double X, double Y)>(ordered.Count, StringComparer.Ordinal);

        for (var index = 0; index < ordered.Count; index++)
        {
            var column = index / GridRows;
            var row = index % GridRows;
            positions[ordered[index].Id] = (column * GridSpacingX, row * GridSpacingY);
        }

        return positions;
    }

    /// <summary>
    /// Places a node near an anchor without overlapping what is already there: tries the
    /// anchor itself, then an outward spiral of offsets, and returns the first fit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The anchor is where the node would ideally sit - usually the centre of the node it
    /// was created next to - and the result is the centre of the placed node, ready for
    /// <see cref="GraphNode.X"/> and <see cref="GraphNode.Y"/>. Candidate centres step
    /// through 16 directions at growing radii (60, 120, ...), and each is checked for
    /// axis-aligned overlap against the rectangles of <paramref name="existing"/>, using
    /// each node's own size.
    /// </para>
    /// <para>
    /// Nothing fits within <see cref="PlaceMaxRings"/> rings - a canvas that is solidly
    /// occupied - and the last candidate is returned anyway, overlapped: a node the caller
    /// asked to place has to appear somewhere, and an honest overlap the user can drag
    /// apart beats a node that quietly never arrived.
    /// </para>
    /// </remarks>
    public static (double X, double Y) PlaceNear(
        double anchorX,
        double anchorY,
        double width,
        double height,
        IReadOnlyList<GraphNode> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var candidateX = anchorX;
        var candidateY = anchorY;

        if (!Overlaps(candidateX, candidateY, width, height, existing))
        {
            return (candidateX, candidateY);
        }

        for (var ring = 1; ring <= PlaceMaxRings; ring++)
        {
            var radius = ring * PlaceRingStep;

            for (var step = 0; step < PlaceDirections; step++)
            {
                var angle = (2 * Math.PI * step) / PlaceDirections;
                candidateX = anchorX + (radius * Math.Cos(angle));
                candidateY = anchorY + (radius * Math.Sin(angle));

                if (!Overlaps(candidateX, candidateY, width, height, existing))
                {
                    return (candidateX, candidateY);
                }
            }
        }

        return (candidateX, candidateY);
    }

    /// <summary>
    /// Whether a rectangle centred at the given point, of the given size, overlaps any of
    /// the existing nodes' rectangles.
    /// </summary>
    /// <remarks>
    /// Rectangles that merely touch do not overlap - a node placed flush beside another is
    /// a valid placement, and the caller asked for space, not for a margin.
    /// </remarks>
    private static bool Overlaps(
        double centreX,
        double centreY,
        double width,
        double height,
        IReadOnlyList<GraphNode> existing)
    {
        foreach (var node in existing)
        {
            var deltaX = Math.Abs(centreX - node.X);
            var deltaY = Math.Abs(centreY - node.Y);

            if (deltaX < (width + node.Width) / 2 && deltaY < (height + node.Height) / 2)
            {
                return true;
            }
        }

        return false;
    }
}
