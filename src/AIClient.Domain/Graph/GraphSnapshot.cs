using System.Diagnostics.CodeAnalysis;

namespace AIClient.Domain.Graph;

/// <summary>
/// An immutable, persistable state of the whole graph.
/// </summary>
/// <remarks>
/// <para>
/// Built by <see cref="GraphModel"/> after every mutation and handed to anyone who asks, so
/// readers never see a half-applied change set and never need a lock: a snapshot handed out
/// stays valid forever, because nothing can write to it.
/// </para>
/// <para>
/// Version increments by one per applied change set and is what cheap change detection hangs
/// off - a view can compare two integers to decide whether anything it cares about happened,
/// rather than diffing node lists.
/// </para>
/// <para>
/// Equality is deliberately the observable content only: version, then the node list, then
/// the edge list, in order. The lookup structures below are derived state - two snapshots
/// with the same nodes and edges are the same graph, whatever their indexes happen to look
/// like - so they are excluded from <see cref="Equals(GraphSnapshot?)"/> and
/// <see cref="GetHashCode"/> on purpose.
/// </para>
/// </remarks>
public sealed record GraphSnapshot
{
    /// <summary>
    /// The graph before anything was drawn: version 0, no nodes, no edges.
    /// </summary>
    /// <remarks>
    /// A singleton rather than a property per graph, because sharing one instance is safe -
    /// the type is immutable, and handing every caller the same object lets "is this the empty
    /// graph" be a reference check as well as a value one.
    /// </remarks>
    public static readonly GraphSnapshot Empty = new();

    /// <summary>
    /// The index structures shared by every lookup method, built once per snapshot.
    /// </summary>
    /// <remarks>
    /// Kept in one object so a single lazy field covers all four maps: they are always needed
    /// together or not at all, and building them as one unit makes it impossible to hand out
    /// an id index that disagrees with an edge index.
    /// </remarks>
    private sealed class Lookups
    {
        public required Dictionary<string, GraphNode> NodesById { get; init; }

        public required Dictionary<string, GraphEdge> EdgesById { get; init; }

        public required Dictionary<string, List<GraphEdge>> EdgesFrom { get; init; }

        public required Dictionary<string, List<GraphEdge>> EdgesTo { get; init; }
    }

    /// <summary>
    /// The lookups, built the first time anybody asks rather than when the record is
    /// constructed.
    /// </summary>
    /// <remarks>
    /// Construction through an object initializer - the way <see cref="GraphModel"/> builds
    /// every snapshot, and the way <c>with</c> expressions work - sets the
    /// <see cref="Nodes"/> and <see cref="Edges"/> properties after any constructor has
    /// run, so there is no moment inside a constructor at which the final lists are
    /// readable. Building on first use is the one place that is after the lists are final
    /// and before any reader sees a lookup. The lazy value is thread-safe; the snapshot
    /// itself needs no other locking because nothing ever writes to it twice.
    /// </remarks>
    private readonly Lazy<Lookups> _lookups;

    /// <summary>How many change sets have landed in this graph's history, starting at 0.</summary>
    public int Version { get; init; }

    /// <summary>Every node, in the order the model added them.</summary>
    /// <remarks>
    /// Order is preserved and stable: the same sequence of change sets always produces the
    /// same list, which is what makes layouts and context blocks reproducible.
    /// </remarks>
    public IReadOnlyList<GraphNode> Nodes { get; init; } = [];

    /// <summary>Every edge, in the order the model added them.</summary>
    public IReadOnlyList<GraphEdge> Edges { get; init; } = [];

    /// <summary>
    /// The empty snapshot's constructor; the object initializer supplies the content.
    /// </summary>
    public GraphSnapshot() => _lookups = new(BuildLookups);

    /// <summary>
    /// The copy constructor, which exists so <c>with</c> expressions cannot carry stale
    /// lookups.
    /// </summary>
    /// <remarks>
    /// The compiler's own copy constructor would copy the lazy field by reference, and a
    /// <c>with</c> expression that replaced the node list would then read an index built
    /// for the old one. This version copies the content and starts the lookups afresh, so
    /// a derived snapshot is exactly as trustworthy as one built from scratch.
    /// </remarks>
    public GraphSnapshot(GraphSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);

        Version = original.Version;
        Nodes = original.Nodes;
        Edges = original.Edges;
        _lookups = new(BuildLookups);
    }

    /// <summary>Whether a node with this id exists, and which one it is.</summary>
    /// <remarks>
    /// The id may be null or unknown: both simply answer no. Lookups never throw, because
    /// callers reach here with ids that came from files, plans and models, and a lookup
    /// that can explode turns every consumer into a defensive copy of a guard.
    /// </remarks>
    public bool TryGetNode(string? id, [NotNullWhen(true)] out GraphNode? node)
    {
        node = null;

        if (string.IsNullOrEmpty(id) || !_lookups.Value.NodesById.TryGetValue(id, out var found))
        {
            return false;
        }

        node = found;
        return true;
    }

    /// <summary>Whether an edge with this id exists, and which one it is.</summary>
    /// <remarks>See <see cref="TryGetNode"/>: null and unknown ids answer no rather than throw.</remarks>
    public bool TryGetEdge(string? id, [NotNullWhen(true)] out GraphEdge? edge)
    {
        edge = null;

        if (string.IsNullOrEmpty(id) || !_lookups.Value.EdgesById.TryGetValue(id, out var found))
        {
            return false;
        }

        edge = found;
        return true;
    }

    /// <summary>Every edge whose <see cref="GraphEdge.SourceId"/> is this node, in graph order.</summary>
    /// <remarks>Empty for an unknown id, never null: the caller can iterate without a guard.</remarks>
    public IReadOnlyList<GraphEdge> EdgesFrom(string? nodeId)
    {
        if (nodeId is null)
        {
            return [];
        }

        return _lookups.Value.EdgesFrom.TryGetValue(nodeId, out var edges) ? edges : [];
    }

    /// <summary>Every edge whose <see cref="GraphEdge.TargetId"/> is this node, in graph order.</summary>
    /// <remarks>Empty for an unknown id, never null: the caller can iterate without a guard.</remarks>
    public IReadOnlyList<GraphEdge> EdgesTo(string? nodeId)
    {
        if (nodeId is null)
        {
            return [];
        }

        return _lookups.Value.EdgesTo.TryGetValue(nodeId, out var edges) ? edges : [];
    }

    /// <summary>
    /// Every edge incident to this node either way, outgoing first, each edge listed once.
    /// </summary>
    /// <remarks>
    /// The union of <see cref="EdgesFrom"/> and <see cref="EdgesTo"/>. An edge lands in
    /// both lists only when it joins a node to itself - the model refuses those, but a
    /// hand-loaded file might not - and listing it once keeps the answer honest without
    /// pretending the file was well formed.
    /// </remarks>
    public IReadOnlyList<GraphEdge> EdgesOf(string? nodeId)
    {
        if (nodeId is null)
        {
            return [];
        }

        var lookups = _lookups.Value;
        var incident = new List<GraphEdge>();

        if (lookups.EdgesFrom.TryGetValue(nodeId, out var outgoing))
        {
            incident.AddRange(outgoing);
        }

        if (lookups.EdgesTo.TryGetValue(nodeId, out var incoming))
        {
            incident.AddRange(incoming.Where(edge => edge.SourceId != nodeId));
        }

        return incident;
    }

    /// <summary>The distinct ids of the nodes joined to this one by any edge, in graph order.</summary>
    /// <remarks>
    /// Read off <see cref="EdgesOf"/>, so it inherits that method's tolerance: unknown ids
    /// answer empty, and a self-loop names no neighbour.
    /// </remarks>
    public IReadOnlyList<string> NeighboursOf(string? nodeId)
    {
        if (nodeId is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var neighbours = new List<string>();

        foreach (var edge in EdgesOf(nodeId))
        {
            var other = edge.SourceId == nodeId ? edge.TargetId : edge.SourceId;

            if (other != nodeId && seen.Add(other))
            {
                neighbours.Add(other);
            }
        }

        return neighbours;
    }

    /// <summary>
    /// Content equality: version, then nodes, then edges, in order. The lookup structures
    /// are derived and excluded deliberately.
    /// </summary>
    public bool Equals(GraphSnapshot? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Version == other.Version
            && Nodes.Count == other.Nodes.Count
            && Edges.Count == other.Edges.Count
            && Nodes.SequenceEqual(other.Nodes)
            && Edges.SequenceEqual(other.Edges);
    }

    /// <summary>Consistent with <see cref="Equals(GraphSnapshot?)"/>: derived from version, nodes and edges only.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);

        foreach (var node in Nodes)
        {
            hash.Add(node);
        }

        foreach (var edge in Edges)
        {
            hash.Add(edge);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Builds every lookup from the node and edge lists, tolerating input the model would
    /// never produce.
    /// </summary>
    /// <remarks>
    /// Entries without an id, and duplicates, can only arrive in a snapshot restored from a
    /// hand-edited file. They are skipped - first occurrence wins - rather than thrown at:
    /// a snapshot that loads is worth more than one that is strictly pure, and the graph
    /// corrects itself on the next mutation.
    /// </remarks>
    private Lookups BuildLookups()
    {
        var nodesById = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var edgesById = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);
        var edgesFrom = new Dictionary<string, List<GraphEdge>>(StringComparer.Ordinal);
        var edgesTo = new Dictionary<string, List<GraphEdge>>(StringComparer.Ordinal);

        foreach (var node in Nodes)
        {
            if (string.IsNullOrEmpty(node.Id))
            {
                continue;
            }

            nodesById.TryAdd(node.Id, node);
        }

        foreach (var edge in Edges)
        {
            if (string.IsNullOrEmpty(edge.Id)
                || string.IsNullOrEmpty(edge.SourceId)
                || string.IsNullOrEmpty(edge.TargetId))
            {
                continue;
            }

            edgesById.TryAdd(edge.Id, edge);
            IndexBy(edgesFrom, edge.SourceId, edge);
            IndexBy(edgesTo, edge.TargetId, edge);
        }

        return new Lookups
        {
            NodesById = nodesById,
            EdgesById = edgesById,
            EdgesFrom = edgesFrom,
            EdgesTo = edgesTo,
        };
    }

    /// <summary>Adds an edge to one of the direction indexes, creating its bucket on first use.</summary>
    private static void IndexBy(Dictionary<string, List<GraphEdge>> index, string nodeId, GraphEdge edge)
    {
        if (index.TryGetValue(nodeId, out var bucket))
        {
            bucket.Add(edge);
        }
        else
        {
            index[nodeId] = [edge];
        }
    }
}
