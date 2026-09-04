using System.Text;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using Microsoft.Extensions.Logging;

namespace AIClient.Application.Services;

/// <summary>
/// Describes a graph selection to a model: richly when it fits, plainly when it does not.
/// </summary>
/// <remarks>
/// <para>
/// One block of text, inlined into the system prompt by <see cref="ContextBuilder"/>, using the same
/// <c>&lt;file name="…"&gt;</c> shape attachments already use. Nothing new is sent, no second
/// pipeline exists, and a turn with no selection is byte for byte the request it was before.
/// </para>
/// <para>
/// The ladder is the whole design. Four rungs, from a file's actual text down to a list of titles
/// and relations, and the first one that fits the budget wins. A person who selects one class gets
/// the code; a person who lassoes two hundred nodes gets the shape of what they picked. Both are
/// useful answers, and neither overruns the window.
/// </para>
/// <para>
/// Every file read goes through <see cref="IWorkspaceService"/>, so an excerpt is subject to the
/// same containment, protected-name and size rules as any other read. A selection cannot be used to
/// get at a key file by pointing a node at one.
/// </para>
/// </remarks>
public sealed class GraphContextSource : IGraphContextSource
{
    /// <summary>Below this there is no room for even a title list, so nothing is sent at all.</summary>
    private const int MinimumBudget = 96;

    /// <summary>
    /// Selected nodes whose file is read for the richest rung.
    /// </summary>
    /// <remarks>
    /// Past a handful the reads cost more than they buy: the budget is split so thinly that each
    /// excerpt is a few lines of a file, which tells a model less than the summary would.
    /// </remarks>
    private const int MaxExcerptFiles = 8;

    /// <summary>Lines read from a node with no span of its own. A file card is not a code review.</summary>
    private const int MaxExcerptLines = 240;

    /// <summary>Characters per token, deliberately pessimistic, for sizing an excerpt before reading it.</summary>
    private const double CharsPerToken = 3.0;

    /// <summary>Share of the block excerpts may claim, leaving room for the structure around them.</summary>
    private const double ExcerptShare = 0.7;

    private const int SummaryLimit = 320;
    private const int MaxRelationsPerNode = 12;
    private const int MaxMetadataPairs = 6;
    private const int MaxNeighbours = 40;

    /// <summary>Hops around the selection, capped: four levels of a file tree is most of a project.</summary>
    private const int MaxDepth = 3;

    private const string Open = "<graph-context>";
    private const string Close = "</graph-context>";

    private static readonly IReadOnlyDictionary<Guid, WorkspaceFile> NoExcerpts =
        new Dictionary<Guid, WorkspaceFile>().AsReadOnly();

    private readonly IGraphService _graph;
    private readonly IWorkspaceService _workspace;
    private readonly ISettingsService _settings;
    private readonly ILogger<GraphContextSource> _logger;

    public GraphContextSource(
        IGraphService graph,
        IWorkspaceService workspace,
        ISettingsService settings,
        ILogger<GraphContextSource> logger)
    {
        _graph = graph;
        _workspace = workspace;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>How much of a node is spelled out. Ordered richest first; the ladder walks it.</summary>
    private enum Detail
    {
        /// <summary>Everything, including the text of the files behind the selected nodes.</summary>
        Excerpts,

        /// <summary>Where to find each node, and what is known about it, but not its contents.</summary>
        Reference,

        /// <summary>What each node is and what it is for, without paths or line spans.</summary>
        Described,

        /// <summary>Titles, kinds and relations. The floor, and it truncates rather than overflow.</summary>
        Outline,
    }

    public async Task<string?> BuildAsync(
        GraphSelection selection,
        int tokenBudget,
        CancellationToken cancellationToken = default)
    {
        if (selection is null || selection.IsEmpty)
        {
            return null;
        }

        var canvas = _settings.Current.Canvas;
        var budget = (int)(tokenBudget * Math.Clamp(canvas.MaxContextShare, 0.05, 0.9));

        if (budget < MinimumBudget)
        {
            return null;
        }

        // The graph is empty until something loads it, which resolves every id to nothing and returns
        // null below. That is the same answer a stale selection gets, and it costs the send path no
        // database read of its own.
        var view = Compose(_graph.Current, selection);

        if (view.Selected.Count == 0)
        {
            return null;
        }

        var excerpts = await ReadExcerptsAsync(view, budget, cancellationToken).ConfigureAwait(false);

        foreach (var detail in new[] { Detail.Excerpts, Detail.Reference, Detail.Described })
        {
            // With nothing read, the richest rung is the next one rendered twice.
            if (detail == Detail.Excerpts && excerpts.Count == 0)
            {
                continue;
            }

            var candidate = Render(view, detail, excerpts);
            var cost = TokenEstimator.EstimateMessage(candidate);

            if (cost <= budget)
            {
                Trace(view, detail, cost, budget);

                return candidate;
            }
        }

        var outline = RenderOutline(view, budget);

        Trace(view, Detail.Outline, TokenEstimator.EstimateMessage(outline), budget);

        return outline;
    }

    private void Trace(SelectionView view, Detail detail, int cost, int budget) =>
        _logger.LogDebug(
            "Graph context: {Selected} selected and {Context} nearby node(s) as {Detail}, {Cost}/{Budget} tokens.",
            view.Selected.Count,
            view.Context.Count,
            detail,
            cost,
            budget);

    /// <summary>
    /// Resolves a selection against a snapshot: what was picked, what surrounds it, what joins them.
    /// </summary>
    /// <remarks>
    /// Ids that name nothing are dropped rather than reported. A selection is a gesture that has
    /// already happened, and by the time it reaches a model the file behind a node may well have been
    /// deleted; the honest response is to describe what is still there.
    /// </remarks>
    private static SelectionView Compose(GraphSnapshot graph, GraphSelection selection)
    {
        var seeds = new HashSet<Guid>(selection.NodeIds.Where(id => graph.Node(id) is not null));

        // Edges are not indexed by id - a snapshot would carry a dictionary per change for a case
        // that arrives with a handful of ids - so the few that were picked are found by one scan.
        List<GraphEdge> picked = selection.EdgeIds.Count == 0
            ? []
            : [.. graph.Edges.Where(edge => selection.EdgeIds.Contains(edge.Id))];

        // An edge picked on its own is a question about a relationship, and a relationship with one
        // end missing is not one, so both endpoints join the selection.
        foreach (var edge in picked)
        {
            seeds.Add(edge.FromId);
            seeds.Add(edge.ToId);
        }

        var depth = Math.Clamp(selection.Depth, 0, MaxDepth);
        var sub = graph.Subgraph(seeds, depth);

        return new SelectionView(
            sub,
            [.. InReadingOrder(seeds.Select(id => sub.Node(id)).OfType<GraphNode>())],
            [.. InReadingOrder(sub.Nodes.Where(node => !seeds.Contains(node.Id)))],
            picked.Count);
    }

    /// <summary>
    /// Kind, then title, then key.
    /// </summary>
    /// <remarks>
    /// Grouping by kind is how the block reads best - the services together, then the files - and the
    /// two tie-breaks make the text identical between runs, which is what lets a test assert on it.
    /// </remarks>
    private static IEnumerable<GraphNode> InReadingOrder(IEnumerable<GraphNode> nodes) =>
        nodes
            .OrderBy(node => node.Kind.Value, StringComparer.Ordinal)
            .ThenBy(node => node.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Key, StringComparer.Ordinal);

    /// <summary>
    /// Reads the files behind the selected nodes, each clipped to its share of the budget.
    /// </summary>
    /// <remarks>
    /// Clipped before rendering rather than after, because the alternative is an all-or-nothing rung:
    /// two large files would push the block over the budget and cost the selection every excerpt,
    /// including the small ones that would have fitted comfortably.
    /// </remarks>
    private async Task<IReadOnlyDictionary<Guid, WorkspaceFile>> ReadExcerptsAsync(
        SelectionView view,
        int budget,
        CancellationToken cancellationToken)
    {
        if (!_workspace.IsOpen)
        {
            return NoExcerpts;
        }

        var candidates = view.Selected
            .Where(node => node.Kind == GraphNodeKind.File && node.Source is not null)
            .Where(node => node.Status == GraphNodeStatus.Active)
            .ToList();

        if (candidates.Count == 0 || candidates.Count > MaxExcerptFiles)
        {
            return NoExcerpts;
        }

        var allowance = (int)(budget * ExcerptShare * CharsPerToken / candidates.Count);

        // A few hundred characters each is a heading and two lines of code. The poorer rung says more.
        if (allowance < 400)
        {
            return NoExcerpts;
        }

        var excerpts = new Dictionary<Guid, WorkspaceFile>();

        foreach (var node in candidates)
        {
            if (node.Source is not { } path)
            {
                continue;
            }

            var start = node.StartLine is { } line && line > 0 ? line : 1;
            var count = node.EndLine is { } end && end >= start
                ? Math.Min(end - start + 1, MaxExcerptLines)
                : MaxExcerptLines;

            var read = await _workspace
                .ReadAsync(path, start, count, cancellationToken)
                .ConfigureAwait(false);

            // A file that cannot be read is not a failure of the build. The block falls back to
            // describing the node, which is what the poorer rungs say about it anyway - and the
            // refusal text stays in the sandbox's log rather than going to a model as an excerpt.
            if (read is { Success: true, Value: { } file } && file.Content.Length > 0)
            {
                excerpts[node.Id] = Clip(file, allowance);
            }
        }

        return excerpts;
    }

    /// <summary>Cuts an excerpt down to <paramref name="allowance"/> characters, on a line boundary.</summary>
    /// <remarks>
    /// Half a line of source reads as a syntax error rather than as an excerpt, and a model asked to
    /// reason about it will start by pointing out the mistake that is not there.
    /// </remarks>
    private static WorkspaceFile Clip(WorkspaceFile file, int allowance)
    {
        if (file.Content.Length <= allowance)
        {
            return file;
        }

        var cut = file.Content.LastIndexOf('\n', allowance);

        return file with
        {
            Content = file.Content[..(cut > 0 ? cut : allowance)],
            IsTruncated = true,
        };
    }

    /// <summary>Renders the whole block at one rung of the ladder, however long it comes out.</summary>
    private static string Render(
        SelectionView view,
        Detail detail,
        IReadOnlyDictionary<Guid, WorkspaceFile> excerpts)
    {
        var builder = new StringBuilder();

        AppendHeader(builder, view);

        foreach (var node in view.Selected)
        {
            AppendNode(builder, view, node, detail, excerpts);
        }

        AppendNeighbours(builder, view, detail);

        return builder.Append(Close).ToString();
    }

    /// <summary>
    /// Renders the poorest rung, dropping selected nodes off the end until it fits.
    /// </summary>
    /// <remarks>
    /// The floor of the ladder has to return something, so this is the one renderer that truncates.
    /// It says how many nodes it left out: a model told about twelve of two hundred nodes can answer
    /// about the twelve, while one that believes it was shown everything answers about the project.
    /// </remarks>
    private static string RenderOutline(SelectionView view, int budget)
    {
        var builder = new StringBuilder();

        AppendHeader(builder, view);

        var spent = TokenEstimator.EstimateMessage(builder.ToString()) + TokenEstimator.Estimate(Close);
        var written = 0;

        foreach (var node in view.Selected)
        {
            var piece = new StringBuilder();

            AppendNode(piece, view, node, Detail.Outline, NoExcerpts);

            var text = piece.ToString();
            var cost = TokenEstimator.Estimate(text);

            // At least one node always goes in. A block that names nothing is worse than a block that
            // overruns a budget by one node, because it reads as an empty selection.
            if (written > 0 && spent + cost > budget)
            {
                break;
            }

            builder.Append(text);
            spent += cost;
            written++;
        }

        if (written < view.Selected.Count)
        {
            builder
                .Append("\n… ")
                .Append(view.Selected.Count - written)
                .Append(" more selected node(s) omitted to fit the context budget.\n");
        }

        return builder.Append(Close).ToString();
    }

    /// <summary>
    /// Opens the block and says what the selection is.
    /// </summary>
    /// <remarks>
    /// The sentence about entities is not padding. Given a list of paths a model assumes a directory
    /// listing and answers about files; told these are entities in a project graph, it answers about
    /// the architecture, which is the question the Canvas was used to ask.
    /// </remarks>
    private static void AppendHeader(StringBuilder builder, SelectionView view)
    {
        builder
            .Append(Open)
            .Append("\nThe user selected part of this project's knowledge graph. Each entry is an ")
            .Append("entity the project is made of - a file, a folder, a component, a decision - ")
            .Append("followed by its relations. This is a model of the project, not a directory ")
            .Append("listing, and only part of it: read a file or ask for more if you need it.\n\n")
            .Append("Selected: ")
            .Append(view.Selected.Count)
            .Append(view.Selected.Count == 1 ? " node" : " nodes");

        if (view.PickedEdges > 0)
        {
            builder
                .Append(", ")
                .Append(view.PickedEdges)
                .Append(view.PickedEdges == 1 ? " relation" : " relations");
        }

        if (view.Context.Count > 0)
        {
            builder.Append(". Nearby: ").Append(view.Context.Count);
        }

        builder.Append(".\n\n");
    }

    /// <summary>Renders one selected node at the given rung.</summary>
    private static void AppendNode(
        StringBuilder builder,
        SelectionView view,
        GraphNode node,
        Detail detail,
        IReadOnlyDictionary<Guid, WorkspaceFile> excerpts)
    {
        builder.Append("- ").Append(node.Title).Append(" [").Append(node.Kind.Value).Append(']');

        if (detail is Detail.Excerpts or Detail.Reference && node.Source is { } source)
        {
            builder.Append(' ').Append(source.Value).Append(Span(node));
        }

        // Worth saying at every rung: a model reasoning about a file that is gone should know it is
        // gone, and this is the one fact about a node that changes what an answer may promise.
        if (node.Status != GraphNodeStatus.Active)
        {
            builder.Append(" (").Append(node.Status.ToString().ToLowerInvariant()).Append(')');
        }

        builder.Append('\n');

        if (detail != Detail.Outline)
        {
            AppendDescription(builder, node);
        }

        AppendRelations(builder, view, node, detail);

        if (detail == Detail.Excerpts && excerpts.TryGetValue(node.Id, out var file))
        {
            AppendExcerpt(builder, file);
        }
    }

    /// <summary>The summary somebody wrote, and whatever an indexer thought worth recording.</summary>
    private static void AppendDescription(StringBuilder builder, GraphNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.Summary))
        {
            builder.Append("    ").Append(Shorten(node.Summary.Trim(), SummaryLimit)).Append('\n');
        }

        if (node.Metadata.Count == 0)
        {
            return;
        }

        var written = 0;

        foreach (var (key, value) in node.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (written == MaxMetadataPairs)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            builder.Append(written == 0 ? "    " : ", ").Append(key).Append('=').Append(Shorten(value.Trim(), 80));
            written++;
        }

        if (written > 0)
        {
            builder.Append('\n');
        }
    }

    /// <summary>
    /// The relations a node has, in both directions, named.
    /// </summary>
    /// <remarks>
    /// Present at every rung including the poorest, because they are the cheapest thing in the block
    /// and the only thing in it a directory listing could not have said. An arrow shows direction:
    /// <c>-&gt;</c> for what this node does to another, <c>&lt;-</c> for what is done to it.
    /// </remarks>
    private static void AppendRelations(StringBuilder builder, SelectionView view, GraphNode node, Detail detail)
    {
        var relations = view.Graph.Outgoing(node.Id).Select(edge => (Edge: edge, Outgoing: true))
            .Concat(view.Graph.Incoming(node.Id).Select(edge => (Edge: edge, Outgoing: false)))
            .ToList();

        var written = 0;

        foreach (var (edge, outgoing) in relations)
        {
            if (written == MaxRelationsPerNode)
            {
                break;
            }

            if (view.Graph.Node(outgoing ? edge.ToId : edge.FromId) is not { } other)
            {
                continue;
            }

            builder
                .Append(outgoing ? "    -> " : "    <- ")
                .Append(edge.Kind.Value)
                .Append(' ')
                .Append(other.Title)
                .Append(" [")
                .Append(other.Kind.Value)
                .Append(']');

            if (detail != Detail.Outline && !string.IsNullOrWhiteSpace(edge.Label))
            {
                builder.Append(" - ").Append(Shorten(edge.Label.Trim(), 80));
            }

            builder.Append('\n');
            written++;
        }

        if (relations.Count > written)
        {
            builder.Append("    … ").Append(relations.Count - written).Append(" more relation(s)\n");
        }
    }

    /// <summary>
    /// Names the nodes drawn in by depth, one line each.
    /// </summary>
    /// <remarks>
    /// Most of them are already named in the relation lines above; the ones two hops out are not, and
    /// a node that appears in the block only as "depends on X" leaves X as a word rather than a thing.
    /// Dropped entirely at the poorest rung, where the room is needed for the selection itself.
    /// </remarks>
    private static void AppendNeighbours(StringBuilder builder, SelectionView view, Detail detail)
    {
        if (view.Context.Count == 0 || detail == Detail.Outline)
        {
            return;
        }

        builder.Append("\nNearby in the graph:\n");

        var written = 0;

        foreach (var node in view.Context)
        {
            if (written == MaxNeighbours)
            {
                break;
            }

            builder.Append("- ").Append(node.Title).Append(" [").Append(node.Kind.Value).Append(']');

            if (detail is Detail.Excerpts or Detail.Reference && node.Source is { } source)
            {
                builder.Append(' ').Append(source.Value);
            }

            builder.Append('\n');
            written++;
        }

        if (view.Context.Count > written)
        {
            builder.Append("… ").Append(view.Context.Count - written).Append(" more\n");
        }
    }

    /// <summary>
    /// Inlines the text of a file.
    /// </summary>
    /// <remarks>
    /// The same <c>&lt;file name="…"&gt;</c> wrapper attachments use, deliberately: a model that has
    /// learned what that means from a dropped file should not have to learn a second convention for
    /// the same thing. Flush left and unindented, because indenting source changes it.
    /// </remarks>
    private static void AppendExcerpt(StringBuilder builder, WorkspaceFile file)
    {
        var lines = file.Content.AsSpan().Count('\n') + 1;

        builder
            .Append("<file name=\"")
            .Append(file.Path.Value)
            .Append("\" lines=\"")
            .Append(file.FirstLine <= 0 ? 1 : file.FirstLine)
            .Append('-')
            .Append((file.FirstLine <= 0 ? 1 : file.FirstLine) + lines - 1)
            .Append("\">\n")
            .Append(file.Content);

        if (!file.Content.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        if (file.IsTruncated)
        {
            builder.Append("… [truncated - only the first part is shown]\n");
        }

        builder.Append("</file>\n");
    }

    /// <summary>The line span a node covers, when it covers part of a file rather than all of one.</summary>
    private static string Span(GraphNode node) => node switch
    {
        { StartLine: { } start, EndLine: { } end } => $":{start}-{end}",
        { StartLine: { } start } => $":{start}",
        _ => string.Empty,
    };

    private static string Shorten(string text, int limit) =>
        text.Length <= limit ? text : string.Concat(text.AsSpan(0, limit), "…");

    /// <param name="Graph">The selection and its surroundings, with the edges that survived.</param>
    /// <param name="Selected">What the user pointed at, in reading order.</param>
    /// <param name="Context">What was drawn in by depth, in reading order.</param>
    /// <param name="PickedEdges">Relations picked on their own, counted for the header.</param>
    private sealed record SelectionView(
        GraphSnapshot Graph,
        IReadOnlyList<GraphNode> Selected,
        IReadOnlyList<GraphNode> Context,
        int PickedEdges);
}
