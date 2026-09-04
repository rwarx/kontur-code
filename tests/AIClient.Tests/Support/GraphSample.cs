using AIClient.Domain.Enums;
using AIClient.Domain.Graph;

namespace AIClient.Tests.Support;

/// <summary>
/// Short ways to say "a node" and "an edge", so that a graph test reads as the rule it is about.
/// </summary>
/// <remarks>
/// Every one of these types is <c>required</c>-heavy on purpose - a node cannot be half-built - which
/// makes an inline object initialiser six lines long and buries the one field a test actually cares
/// about. These fill in defaults that no assertion depends on: a fresh identity, the key doubling as
/// the title, active and user-owned.
/// </remarks>
public static class GraphSample
{
    public static GraphNode Node(
        string key,
        GraphNodeKind? kind = null,
        GraphOrigin origin = GraphOrigin.User,
        string? title = null,
        GraphNodeStatus status = GraphNodeStatus.Active) => new()
    {
        Id = Guid.CreateVersion7(),
        Kind = kind ?? GraphNodeKind.File,
        Key = key,
        Title = title ?? key,
        Origin = origin,
        Status = status,
    };

    public static GraphEdge Edge(
        GraphNode from,
        GraphNode to,
        GraphEdgeKind? kind = null,
        GraphOrigin origin = GraphOrigin.User,
        int order = 0) => new()
    {
        Id = Guid.CreateVersion7(),
        FromId = from.Id,
        ToId = to.Id,
        Kind = kind ?? GraphEdgeKind.Contains,
        Origin = origin,
        Order = order,
    };

    /// <summary>A snapshot at version 1, as though one change set had built it.</summary>
    public static GraphSnapshot Snapshot(
        IEnumerable<GraphNode> nodes,
        IEnumerable<GraphEdge>? edges = null) =>
        GraphSnapshot.Create(nodes, edges ?? [], version: 1);

    /// <summary>The mutations that would introduce these nodes, in order.</summary>
    public static IReadOnlyList<GraphMutation> Adds(params GraphNode[] nodes) =>
        [.. nodes.Select(node => (GraphMutation)new GraphMutation.AddNode(node))];

    /// <summary>The mutations that would introduce these edges, in order.</summary>
    public static IReadOnlyList<GraphMutation> Adds(params GraphEdge[] edges) =>
        [.. edges.Select(edge => (GraphMutation)new GraphMutation.AddEdge(edge))];
}
