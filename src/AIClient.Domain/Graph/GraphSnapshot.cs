using AIClient.Domain.Enums;

namespace AIClient.Domain.Graph;

/// <summary>
/// The whole graph at one instant, indexed for the questions the projections actually ask.
/// </summary>
/// <remarks>
/// <para>
/// Built once per change and then never modified, which is what lets a canvas hit-test on the UI
/// thread while an indexing pass writes to the database on another. Readers hold a reference to a
/// snapshot for as long as they need consistency and pick up the next one when it arrives.
/// </para>
/// <para>
/// Adjacency is materialised up front rather than filtered on demand: hovering a node asks for its
/// edges, and a linear scan of every edge per hover is the difference between a canvas that feels
/// direct and one that does not.
/// </para>
/// </remarks>
public sealed class GraphSnapshot
{
    private static readonly IReadOnlyList<GraphEdge> NoEdges = [];

    private readonly Dictionary<Guid, GraphNode> _nodes;
    private readonly Dictionary<(string Kind, string Key), GraphNode> _byKey;
    private readonly Dictionary<Guid, List<GraphEdge>> _outgoing = [];
    private readonly Dictionary<Guid, List<GraphEdge>> _incoming = [];
    private readonly List<GraphEdge> _edges;

    private GraphSnapshot(IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges, long version)
    {
        Version = version;

        _nodes = [];
        _byKey = [];

        foreach (var node in nodes)
        {
            // Last one wins rather than throwing. A snapshot is assembled from storage, and a
            // duplicate key there is a defect to be corrected by the next write, not a reason to
            // leave the user with an application that cannot open its own project.
            _nodes[node.Id] = node;
            _byKey[(node.Kind.Value, node.Key)] = node;
        }

        _edges = [];

        foreach (var edge in edges)
        {
            // A dangling edge is dropped silently: nothing downstream can render or traverse it,
            // and keeping it would make every consumer re-check both endpoints.
            if (!_nodes.ContainsKey(edge.FromId) || !_nodes.ContainsKey(edge.ToId))
            {
                continue;
            }

            _edges.Add(edge);
            Add(_outgoing, edge.FromId, edge);
            Add(_incoming, edge.ToId, edge);
        }
    }

    /// <summary>An empty graph, which is what every session starts with until a folder is opened.</summary>
    public static GraphSnapshot Empty { get; } = new([], [], 0);

    /// <summary>
    /// Monotonic counter, bumped once per applied change set.
    /// </summary>
    /// <remarks>
    /// Lets a view decide whether it is looking at stale data without comparing collections. Not a
    /// timestamp: two changes inside the same millisecond are ordinary during an indexing pass.
    /// </remarks>
    public long Version { get; }

    public IReadOnlyCollection<GraphNode> Nodes => _nodes.Values;

    public IReadOnlyList<GraphEdge> Edges => _edges;

    public int NodeCount => _nodes.Count;

    public int EdgeCount => _edges.Count;

    public static GraphSnapshot Create(
        IEnumerable<GraphNode> nodes,
        IEnumerable<GraphEdge> edges,
        long version) => new(nodes, edges, version);

    public bool TryGetNode(Guid id, out GraphNode? node) => _nodes.TryGetValue(id, out node);

    public GraphNode? Node(Guid id) => _nodes.GetValueOrDefault(id);

    /// <summary>The node an indexing pass would upsert, given a kind and a canonical key.</summary>
    public GraphNode? FindByKey(GraphNodeKind kind, string key) =>
        _byKey.GetValueOrDefault((kind.Value, key));

    public IReadOnlyList<GraphEdge> Outgoing(Guid nodeId) =>
        _outgoing.TryGetValue(nodeId, out var edges) ? edges : NoEdges;

    public IReadOnlyList<GraphEdge> Incoming(Guid nodeId) =>
        _incoming.TryGetValue(nodeId, out var edges) ? edges : NoEdges;

    /// <summary>Every edge touching a node, in either direction.</summary>
    public IEnumerable<GraphEdge> EdgesOf(Guid nodeId) => Outgoing(nodeId).Concat(Incoming(nodeId));

    /// <summary>
    /// Nodes reachable from a set of seeds within <paramref name="depth"/> hops, seeds included.
    /// </summary>
    /// <remarks>
    /// Undirected on purpose: asked about AuthService, a person means the things it uses and the
    /// things that use it. Breadth-first with a visited set, so a cycle - which any real dependency
    /// graph has - terminates rather than recursing until the stack gives out.
    /// </remarks>
    public IReadOnlySet<Guid> Neighbourhood(IEnumerable<Guid> seeds, int depth)
    {
        var visited = new HashSet<Guid>();
        var frontier = new List<Guid>();

        foreach (var seed in seeds)
        {
            if (_nodes.ContainsKey(seed) && visited.Add(seed))
            {
                frontier.Add(seed);
            }
        }

        for (var hop = 0; hop < depth && frontier.Count > 0; hop++)
        {
            var next = new List<Guid>();

            foreach (var id in frontier)
            {
                foreach (var edge in EdgesOf(id))
                {
                    if (edge.Other(id) is { } other && visited.Add(other))
                    {
                        next.Add(other);
                    }
                }
            }

            frontier = next;
        }

        return visited;
    }

    /// <summary>
    /// The graph restricted to a selection and its surroundings: exactly what an AI step is given.
    /// </summary>
    /// <remarks>
    /// Edges are kept only where both endpoints survived the restriction. An edge to a node that was
    /// left out would read, to whatever consumes this, as a relationship with nothing on the other
    /// end.
    /// </remarks>
    public GraphSnapshot Subgraph(IEnumerable<Guid> ids, int depth = 0)
    {
        var kept = Neighbourhood(ids, depth);

        return new GraphSnapshot(
            kept.Select(id => _nodes[id]),
            _edges.Where(e => kept.Contains(e.FromId) && kept.Contains(e.ToId)),
            Version);
    }

    public IEnumerable<GraphNode> OfKind(GraphNodeKind kind) =>
        _nodes.Values.Where(n => n.Kind == kind);

    /// <summary>
    /// What sits one level below a node: structural containment and architectural grouping together.
    /// </summary>
    /// <remarks>
    /// Both kinds of edge answer the same question for a reader drilling in, even though they mean
    /// different things - a folder holding a file, and a component gathering services that live in
    /// unrelated folders. Ordered by the explicit hint first so a pipeline reads in sequence, then by
    /// title so the rest is stable between runs.
    /// </remarks>
    public IReadOnlyList<GraphNode> Children(Guid nodeId) =>
        Outgoing(nodeId)
            .Where(e => e.Kind == GraphEdgeKind.Contains || e.Kind == GraphEdgeKind.Groups)
            .OrderBy(e => e.Order)
            .Select(e => _nodes[e.ToId])
            .OrderBy(n => n.Kind != GraphNodeKind.Folder)
            .ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The nodes nothing contains, which is where a canvas starts when no view has been saved.
    /// </summary>
    public IReadOnlyList<GraphNode> Roots() =>
        _nodes.Values
            .Where(n => n.Status != GraphNodeStatus.Archived)
            .Where(n => !Incoming(n.Id).Any(e => e.Kind == GraphEdgeKind.Contains))
            .OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The node containing this one, when anything does.</summary>
    public GraphNode? Parent(Guid nodeId) =>
        Incoming(nodeId).FirstOrDefault(e => e.Kind == GraphEdgeKind.Contains) is { } edge
            ? _nodes.GetValueOrDefault(edge.FromId)
            : null;

    private static void Add(Dictionary<Guid, List<GraphEdge>> index, Guid key, GraphEdge edge)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        list.Add(edge);
    }
}
