using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;
using AIClient.Domain.Workspace;
using Microsoft.Extensions.Logging;

namespace AIClient.Application.Graph;

/// <summary>
/// Turns the workspace folder into the graph: files and folders as nodes, containment as
/// edges.
/// </summary>
/// <remarks>
/// <para>
/// The indexer is the reason the canvas is not a picture: it is the same files the agent
/// reads and edits, reflected as one structure, so "what is selected", "what the model is
/// told about" and "what is on disk" are the same answer by construction rather than by
/// vigilance.
/// </para>
/// <para>
/// It diffs rather than rebuilds. A refresh adds only what is new and removes only what is
/// gone, so nodes keep their positions across refreshes - a canvas the user has tidied is
/// not shuffled because a file changed. Kept nodes are never updated, which is a
/// limitation stated plainly: a node's title and metric are the ones from the day it was
/// added, and a rename shows up as remove-plus-add rather than as an edit.
/// </para>
/// <para>
/// Read-risk only: it lists, it never reads file contents. No sizes are fetched, no
/// <c>stat</c> calls are made - the presentation layer can afford those lazily for the one
/// node the user is looking at, while this class has to walk the whole tree.
/// </para>
/// <para>
/// The listing is capped by the workspace service (400 entries by default), and the
/// indexer naturally stops there: the change set says so in its description, so nobody
/// reads a capped refresh as a complete one.
/// </para>
/// <para>
/// Removal is scoped to the id families this class mints - <c>ws</c>, <c>dir:</c>,
/// <c>file:</c> - so a refresh does not delete nodes other sources put on the same canvas,
/// such as a drawn plan. A workspace close clears everything, including those: with no
/// workspace there is no canvas state worth keeping.
/// </para>
/// </remarks>
public sealed class WorkspaceGraphIndexer
{
    /// <summary>The workspace root node: the one node whose id is not derived from a path.</summary>
    private const string RootNodeId = "ws";

    /// <summary>Id prefix for directory nodes; followed by the workspace-relative path.</summary>
    private const string DirectoryPrefix = "dir:";

    /// <summary>Id prefix for file nodes; followed by the workspace-relative path.</summary>
    private const string FilePrefix = "file:";

    /// <summary>Id prefix for containment edges; followed by parent id, colon, child id.</summary>
    private const string ContainmentPrefix = "c:";

    private readonly IWorkspaceService _workspace;
    private readonly IGraphService _graph;
    private readonly ILogger<WorkspaceGraphIndexer> _logger;

    public WorkspaceGraphIndexer(
        IWorkspaceService workspace,
        IGraphService graph,
        ILogger<WorkspaceGraphIndexer> logger)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(logger);

        _workspace = workspace;
        _graph = graph;
        _logger = logger;
    }

    /// <summary>
    /// Re-indexes the open workspace into the graph. Empty or closed workspace clears the
    /// graph instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result is whatever the graph service reported about the change set it applied.
    /// One thing can stop the refresh before it starts: the workspace listing itself
    /// failing. That is returned as a result with the listing error as its single
    /// rejection reason rather than thrown, following the repo's discipline that an
    /// expected failure is a message, not an exception - and an indexer failing to list is
    /// expected, because the folder may have been deleted out from under it.
    /// </para>
    /// <para>
    /// The indexer raises nothing itself and saves nothing itself: it applies one change
    /// set through the graph service, and the service's events and the host's save policy
    /// do the rest.
    /// </para>
    /// </remarks>
    public async Task<GraphMutationResult> RebuildAsync(CancellationToken cancellationToken = default)
    {
        if (!_workspace.IsOpen)
        {
            return await CloseAsync(cancellationToken).ConfigureAwait(false);
        }

        var listing = await _workspace.ListAsync(WorkspacePath.Root, recursive: true, cancellationToken)
            .ConfigureAwait(false);

        if (!listing.Success || listing.Value is null)
        {
            // The folder may have been closed, deleted or detached between the IsOpen check
            // and the walk; the honest answer is the listing's own reason.
            var error = listing.Error ?? "The workspace could not be listed.";
            _logger.LogWarning("Workspace graph refresh could not list the workspace: {Error}", error);

            return new GraphMutationResult
            {
                Snapshot = _graph.Current,
                Applied = [],
                Rejected = [error],
            };
        }

        return await RefreshAsync(listing.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the graph because the workspace is closed: one change set removing every
    /// node, applied through the service like any other.
    /// </summary>
    /// <remarks>
    /// Removals are idempotent, so an already-empty graph applies the set as a round of
    /// no-ops and the timeline still records that the canvas was cleared.
    /// </remarks>
    private async Task<GraphMutationResult> CloseAsync(CancellationToken cancellationToken)
    {
        var current = _graph.Current;
        var changes = new List<GraphChange>(current.Nodes.Count);

        foreach (var node in current.Nodes)
        {
            changes.Add(new RemoveNode(node.Id));
        }

        var changeSet = new GraphChangeSet
        {
            Title = "Close workspace graph",
            Description = $"Workspace closed; {changes.Count} node(s) removed.",
            Origin = GraphChangeOrigin.Indexer,
            Changes = changes,
        };

        return await _graph.ApplyAsync(changeSet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Diffs the listing against the graph and applies what differs: positions for new
    /// nodes, nothing at all for kept ones.
    /// </summary>
    private async Task<GraphMutationResult> RefreshAsync(
        WorkspaceListing listing,
        CancellationToken cancellationToken)
    {
        var fileCount = listing.Entries.Count(entry => !entry.IsDirectory);
        var targetNodes = new List<GraphNode>(listing.Entries.Count + 1) { BuildRootNode(fileCount) };
        var targetEdges = new List<GraphEdge>(listing.Entries.Count);
        var targetIds = new HashSet<string>(StringComparer.Ordinal) { RootNodeId };

        foreach (var entry in listing.Entries)
        {
            var id = entry.IsDirectory
                ? DirectoryPrefix + entry.Path.Value
                : FilePrefix + entry.Path.Value;

            targetNodes.Add(new GraphNode
            {
                Id = id,
                Kind = entry.IsDirectory ? GraphNodeKind.Folder : InferKind(entry.Path),
                Title = entry.Path.Name,
                Path = entry.Path.Value,
            });

            targetIds.Add(id);

            var parent = entry.Path.Parent;
            var parentId = parent is null || parent.IsRoot ? RootNodeId : DirectoryPrefix + parent.Value;

            targetEdges.Add(new GraphEdge
            {
                Id = $"{ContainmentPrefix}{parentId}:{id}",
                SourceId = parentId,
                TargetId = id,
                Kind = GraphEdgeKind.Contains,
            });
        }

        var current = _graph.Current;
        var currentIds = new HashSet<string>(StringComparer.Ordinal);
        var currentEdgeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in current.Nodes)
        {
            currentIds.Add(node.Id);
        }

        foreach (var edge in current.Edges)
        {
            currentEdgeIds.Add(edge.Id);
        }

        var addedNodes = targetNodes.Where(node => !currentIds.Contains(node.Id)).ToList();
        var removedIds = current.Nodes
            .Where(node => Owns(node.Id) && !targetIds.Contains(node.Id))
            .Select(node => node.Id)
            .ToList();
        var addedEdges = targetEdges.Where(edge => !currentEdgeIds.Contains(edge.Id)).ToList();
        var keptCount = current.Nodes.Count(node => Owns(node.Id) && targetIds.Contains(node.Id));

        // New nodes get their positions from a layered layout of the whole target tree, so
        // a file added three folders deep lands three columns in. Existing nodes keep
        // their recorded positions: the synthetic snapshot exists only for this
        // calculation and nothing of it reaches the graph.
        IReadOnlyDictionary<string, (double X, double Y)> positions = addedNodes.Count > 0
            ? GraphLayouts.Layered(new GraphSnapshot { Nodes = targetNodes, Edges = targetEdges })
            : new Dictionary<string, (double X, double Y)>(0, StringComparer.Ordinal);

        var changes = new List<GraphChange>(removedIds.Count + addedNodes.Count + addedEdges.Count);

        // Removals first, so a renamed entry's old node is gone before its new one arrives;
        // then nodes, then edges, because an edge needs both of its endpoints to exist.
        foreach (var id in removedIds)
        {
            changes.Add(new RemoveNode(id));
        }

        foreach (var node in addedNodes)
        {
            var (x, y) = positions.TryGetValue(node.Id, out var position) ? position : (0, 0);
            changes.Add(new AddNode(node with { X = x, Y = y }));
        }

        foreach (var edge in addedEdges)
        {
            changes.Add(new AddEdge(edge));
        }

        var description = $"Added {addedNodes.Count}, removed {removedIds.Count}, kept {keptCount} node(s)";

        if (listing.IsTruncated)
        {
            description += $"; showing the first {listing.Entries.Count} entries (the listing stopped at its cap)";
        }

        var changeSet = new GraphChangeSet
        {
            Title = "Refresh workspace graph",
            Description = description,
            Origin = GraphChangeOrigin.Indexer,
            Changes = changes,
        };

        var result = await _graph.ApplyAsync(changeSet, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Workspace graph refreshed: {Added} added, {Removed} removed, {Kept} kept, {Edges} edge(s) added.",
            addedNodes.Count, removedIds.Count, keptCount, addedEdges.Count);

        return result;
    }

    /// <summary>
    /// The root node: the folder itself, named after its last path segment, carrying the
    /// file count as its metric.
    /// </summary>
    /// <remarks>
    /// No path - the root is the workspace, and the graph does not know where the
    /// workspace sits on disk. The full root is shown by the presentation layer if the
    /// user wants it, truncated there rather than baked in here.
    /// </remarks>
    private GraphNode BuildRootNode(int fileCount)
    {
        var root = _workspace.Root ?? string.Empty;
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        var title = Path.GetFileName(trimmed);

        if (string.IsNullOrEmpty(title))
        {
            // A drive root ("C:\") has no name to take; the whole path is the most honest
            // label available.
            title = root;
        }

        return new GraphNode
        {
            Id = RootNodeId,
            Kind = GraphNodeKind.Folder,
            Title = title,
            Metric = fileCount,
        };
    }

    /// <summary>
    /// What kind of thing a file is, judged from its name alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Judged from the name because that is all a listing says, and reading contents to
    /// classify 400 files is exactly the cost this class exists not to pay. The table:
    /// </para>
    /// <para>
    /// A test when the path has a segment named <c>Tests</c> (case-insensitive), or the
    /// file name starts with <c>Test</c>, or contains <c>.Tests.</c>, or ends with
    /// <c>.test.cs</c> - each of these is a convention a project actually uses, and
    /// together they catch the common shapes without reading a line of the file.
    /// </para>
    /// <para>
    /// Code extensions (<c>.cs</c>, <c>.ts</c>, <c>.js</c>, <c>.py</c>, <c>.rs</c>,
    /// <c>.go</c>), judged by the name stem: starts with <c>I</c> followed by an uppercase
    /// letter - an interface; ends with <c>Service</c> - a service; otherwise a module.
    /// </para>
    /// <para>
    /// View extensions (<c>.xaml</c>, <c>.axaml</c>, <c>.cshtml</c>, <c>.razor</c>,
    /// <c>.vue</c>, <c>.jsx</c>, <c>.tsx</c>, <c>.html</c>) - a view. Data extensions
    /// (<c>.sql</c>, <c>.db</c>, <c>.sqlite</c>, <c>.csv</c>, <c>.json</c>, <c>.xml</c>,
    /// <c>.yaml</c>, <c>.yml</c>) - data. Build files (<c>.sln</c>, <c>.csproj</c>,
    /// <c>.props</c>, <c>.targets</c>) - external, because they belong to the build
    /// system the project depends on rather than to the project.
    /// </para>
    /// <para>
    /// Everything else is a plain file, and directories are folders. Wrong guesses cost a
    /// colour, never a function, which is why the rules stay cheap.
    /// </para>
    /// </remarks>
    private static GraphNodeKind InferKind(WorkspacePath path)
    {
        var name = path.Name;

        if (IsTestEntry(path, name))
        {
            return GraphNodeKind.Test;
        }

        var extension = Path.GetExtension(name).ToLowerInvariant();

        return extension switch
        {
            ".cs" or ".ts" or ".js" or ".py" or ".rs" or ".go" => InferCodeKind(name),
            ".xaml" or ".axaml" or ".cshtml" or ".razor" or ".vue" or ".jsx" or ".tsx" or ".html"
                => GraphNodeKind.View,
            ".sql" or ".db" or ".sqlite" or ".csv" or ".json" or ".xml" or ".yaml" or ".yml"
                => GraphNodeKind.Data,
            ".sln" or ".csproj" or ".props" or ".targets" => GraphNodeKind.External,
            _ => GraphNodeKind.File,
        };
    }

    /// <summary>Whether a file is a test, by the conventions projects actually name things with.</summary>
    private static bool IsTestEntry(WorkspacePath path, string name) =>
        path.Segments.Any(segment => segment.Equals("Tests", StringComparison.OrdinalIgnoreCase))
        || name.StartsWith("Test", StringComparison.OrdinalIgnoreCase)
        || name.Contains(".Tests.", StringComparison.Ordinal)
        || name.EndsWith(".test.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The code kind from the file name stem: an <c>I</c> prefix on an uppercase letter
    /// reads as an interface, a <c>Service</c> suffix reads as a service, and everything
    /// else is a module.
    /// </summary>
    private static GraphNodeKind InferCodeKind(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);

        if (stem.Length > 1 && stem[0] == 'I' && char.IsUpper(stem[1]))
        {
            return GraphNodeKind.Interface;
        }

        if (stem.EndsWith("Service", StringComparison.Ordinal))
        {
            return GraphNodeKind.Service;
        }

        return GraphNodeKind.Module;
    }

    /// <summary>
    /// Whether an id belongs to one of the families this indexer mints - the test for
    /// "is this node mine to remove".
    /// </summary>
    private static bool Owns(string id) =>
        id == RootNodeId
        || id.StartsWith(DirectoryPrefix, StringComparison.Ordinal)
        || id.StartsWith(FilePrefix, StringComparison.Ordinal);
}
