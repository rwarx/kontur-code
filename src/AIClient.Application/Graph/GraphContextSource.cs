using System.Text;
using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;

namespace AIClient.Application.Graph;

/// <summary>
/// Serialises graph context for the model: what is selected and what surrounds it.
/// </summary>
/// <remarks>
/// <para>
/// The honest way to give the AI context: the user sees exactly the block the model reads,
/// in the draft, before it is sent - so "what does the model know about my project" is a
/// question answered by looking rather than by trusting. It is also the cheapest context
/// there is: the graph is already built, and the selection is already the user's own
/// notion of what matters.
/// </para>
/// <para>
/// Compact and deterministic: the same selection over the same graph produces the same
/// text, so a regenerated answer sees the same world and a user comparing two runs is
/// comparing two models rather than two contexts.
/// </para>
/// </remarks>
public sealed class GraphContextSource
{
    /// <summary>
    /// The most node lines one context will hold, whatever the caller asks for.
    /// </summary>
    /// <remarks>
    /// A backstop rather than the working size: the working size is the caller's
    /// <c>maxNodes</c>, and this exists so a caller that passes a huge budget cannot turn
    /// the "compact" context into a transcript of the whole repository.
    /// </remarks>
    private const int AbsoluteMaxNodes = 200;

    /// <summary>
    /// The most edges one context will mention, both in the focus lines and while walking
    /// the neighbourhood.
    /// </summary>
    /// <remarks>
    /// A hub node with hundreds of edges is common in real graphs, and naming every one of
    /// them spends the context budget on a list the model cannot act on line by line.
    /// </remarks>
    private const int MaxEdgeMentions = 200;

    private readonly IGraphService _graph;

    public GraphContextSource(IGraphService graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
    }

    /// <summary>
    /// Builds context for the given focus nodes (BFS out to <paramref name="hops"/>,
    /// bounded to <paramref name="maxNodes"/> nodes and 200 edge mentions). Empty focus
    /// summarises the whole graph. Returns null when the graph is empty - there is no
    /// honest context to give, and an empty block would read as "the model knows
    /// something it does not".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shape of the text: a header, a one-line whole-graph summary, then the focus nodes
    /// in the order given - each with its relationships listed - then the neighbourhood as
    /// bare lines, sorted by kind and title so the block reads like an index rather than a
    /// walk order. A budget that cuts the walk short ends the block with a truncation line
    /// rather than an unexplained shorter list.
    /// </para>
    /// <para>
    /// A <paramref name="hops"/> below 1 means the focus only - no neighbourhood - and a
    /// <paramref name="maxNodes"/> below 1 is treated as 1. Focus ids that are unknown are
    /// dropped silently: the selection belongs to the caller, and reporting its mistakes
    /// is the selection's business, not the context's.
    /// </para>
    /// </remarks>
    public string? BuildContext(IReadOnlyCollection<string>? focusNodeIds, int hops = 1, int maxNodes = 40)
    {
        var snapshot = _graph.Current;

        if (snapshot.Nodes.Count == 0)
        {
            return null;
        }

        var budget = Math.Min(Math.Max(maxNodes, 1), AbsoluteMaxNodes);

        var builder = new StringBuilder();
        builder.AppendLine("Workspace graph context:");
        builder.AppendLine(SummariseWholeGraph(snapshot));

        var focus = CollectFocus(snapshot, focusNodeIds);

        if (focus.Count > 0)
        {
            AppendFocusSection(builder, snapshot, focus, budget);

            if (hops >= 1 && budget > focus.Count)
            {
                AppendNeighbourhoodSection(builder, snapshot, focus, hops, budget);
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// The one line that stands for the whole graph: counts of nodes and edges, then a
    /// count per kind.
    /// </summary>
    /// <remarks>
    /// Kind counts come in declaration order with empty kinds skipped, so the line is
    /// stable across releases of the kind list and the caller's eye can find its kind in
    /// the same place every time.
    /// </remarks>
    private static string SummariseWholeGraph(GraphSnapshot snapshot)
    {
        var kindCounts = new List<string>();

        foreach (GraphNodeKind kind in Enum.GetValues<GraphNodeKind>())
        {
            var count = snapshot.Nodes.Count(node => node.Kind == kind);

            if (count > 0)
            {
                kindCounts.Add($"{PluralLabel(kind)} {count}");
            }
        }

        return $"Graph: {snapshot.Nodes.Count} nodes, {snapshot.Edges.Count} edges ({string.Join(", ", kindCounts)})";
    }

    /// <summary>
    /// The focus nodes, in the order given, deduplicated and filtered down to nodes that
    /// exist.
    /// </summary>
    private static List<GraphNode> CollectFocus(GraphSnapshot snapshot, IReadOnlyCollection<string>? focusNodeIds)
    {
        var focus = new List<GraphNode>();

        if (focusNodeIds is null)
        {
            return focus;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in focusNodeIds)
        {
            if (string.IsNullOrEmpty(id) || !seen.Add(id))
            {
                continue;
            }

            if (snapshot.TryGetNode(id, out var node))
            {
                focus.Add(node);
            }
        }

        return focus;
    }

    /// <summary>
    /// Writes the focus lines, each followed by the relationships the node takes part in.
    /// </summary>
    /// <remarks>
    /// Every incident edge is named with its far end - the "relates to" line - in graph
    /// order, bounded by the shared edge budget so a hub node cannot spend the whole block
    /// on itself. Edges to nodes that are not on the graph are named by id rather than
    /// dropped: a dangling edge is a fact about the graph, and hiding it would make the
    /// context tidier than the truth.
    /// </remarks>
    private static void AppendFocusSection(
        StringBuilder builder,
        GraphSnapshot snapshot,
        List<GraphNode> focus,
        int budget)
    {
        builder.AppendLine("Focus:");

        var edgeBudget = MaxEdgeMentions;

        foreach (var node in focus.Take(budget))
        {
            builder.Append("- ");
            builder.Append(NodeLine(node));
            builder.AppendLine();

            var relations = new List<string>();

            foreach (var edge in snapshot.EdgesOf(node.Id))
            {
                if (edgeBudget == 0)
                {
                    relations.Add("...");
                    break;
                }

                edgeBudget--;
                relations.Add(RelationLine(snapshot, node.Id, edge));
            }

            if (relations.Count > 0)
            {
                builder.Append("  relates to: ");
                builder.Append(string.Join(", ", relations));
                builder.AppendLine();
            }
        }

        if (focus.Count > budget)
        {
            // More focus was asked for than the block will hold; say so rather than let a
            // selection quietly lose its tail.
            builder.AppendLine("... (truncated)");
        }
    }

    /// <summary>
    /// Writes the neighbourhood: everything within the hop count of the focus, as bare
    /// lines, sorted by kind then title.
    /// </summary>
    /// <remarks>
    /// The walk is breadth-first from the focus, level by level, and stops at the node
    /// budget or the edge budget, whichever bites first - saying so with a truncation line
    /// when it was the budget that stopped it.
    /// </remarks>
    private static void AppendNeighbourhoodSection(
        StringBuilder builder,
        GraphSnapshot snapshot,
        List<GraphNode> focus,
        int hops,
        int budget)
    {
        var included = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in focus)
        {
            included.Add(node.Id);
        }

        var neighbours = new List<GraphNode>();
        var frontier = focus.Select(node => node.Id).ToList();
        var edgeBudget = MaxEdgeMentions;
        var truncated = false;

        for (var level = 0; level < hops; level++)
        {
            var next = new List<string>();

            foreach (var id in frontier)
            {
                foreach (var edge in snapshot.EdgesOf(id))
                {
                    var other = edge.SourceId == id ? edge.TargetId : edge.SourceId;

                    if (other == id || included.Contains(other))
                    {
                        continue;
                    }

                    if (edgeBudget == 0)
                    {
                        truncated = true;
                        break;
                    }

                    edgeBudget--;

                    if (!snapshot.TryGetNode(other, out var node))
                    {
                        // A dangling endpoint is not a node to show; the edge that named it
                        // was already accounted for in the focus lines, if at all.
                        continue;
                    }

                    if (included.Count >= budget)
                    {
                        truncated = true;
                        break;
                    }

                    included.Add(other);
                    neighbours.Add(node);
                    next.Add(other);
                }

                if (truncated)
                {
                    break;
                }
            }

            if (truncated)
            {
                break;
            }

            frontier = next;
        }

        if (neighbours.Count == 0)
        {
            return;
        }

        var ordered = neighbours
            .OrderBy(node => node.Kind)
            .ThenBy(node => node.Title, StringComparer.Ordinal)
            .ToList();

        builder.Append("Neighbourhood (");
        builder.Append(hops == 1 ? "1 hop" : $"{hops} hops");
        builder.AppendLine("):");

        foreach (var node in ordered)
        {
            builder.Append("- ");
            builder.Append(NodeLine(node));
            builder.AppendLine();
        }

        if (truncated)
        {
            builder.AppendLine("... (truncated)");
        }
    }

    /// <summary>
    /// One node as a bare line: title, kind, and the path when the node has one.
    /// </summary>
    /// <remarks>
    /// Titles have their line breaks replaced with spaces so the line format holds: a
    /// title that wraps is a title that stops being parseable to anything reading the
    /// block a line at a time.
    /// </remarks>
    private static string NodeLine(GraphNode node) =>
        node.Path is null
            ? $"{Clean(node.Title)} [{SingularLabel(node.Kind)}]"
            : $"{Clean(node.Title)} [{SingularLabel(node.Kind)}] — path: {node.Path}";

    /// <summary>
    /// One relationship as it appears in a "relates to" list: the far end, its kind, and
    /// the edge kind in parentheses.
    /// </summary>
    private static string RelationLine(GraphSnapshot snapshot, string focusId, GraphEdge edge)
    {
        var other = edge.SourceId == focusId ? edge.TargetId : edge.SourceId;

        if (!snapshot.TryGetNode(other, out var node))
        {
            return $"node '{other}' [missing] ({SingularLabel(edge.Kind)})";
        }

        return $"{Clean(node.Title)} [{SingularLabel(node.Kind)}] ({SingularLabel(edge.Kind)})";
    }

    /// <summary>The kind as the model reads it: one lowercase word.</summary>
    private static string SingularLabel<TKind>(TKind kind) where TKind : struct, Enum =>
        kind.ToString().ToLowerInvariant();

    /// <summary>
    /// The kind as the whole-graph summary counts it: one lowercase plural word, with the
    /// one irregular in the list handled so the line reads like English rather than a
    /// format string.
    /// </summary>
    private static string PluralLabel(GraphNodeKind kind) =>
        kind == GraphNodeKind.Data ? "data" : $"{SingularLabel(kind)}s";

    /// <summary>Replaces line breaks with spaces so one node stays one line.</summary>
    private static string Clean(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}
