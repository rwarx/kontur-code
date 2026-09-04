using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using AIClient.Domain.Workspace;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Graph;

/// <summary>
/// Turns the open folder into nodes: one per file and directory, joined by containment.
/// </summary>
/// <remarks>
/// <para>
/// Everything reaches the disk through <see cref="IWorkspaceService"/>, which is why this class has
/// no notion of an absolute path and cannot be pointed anywhere else. It also means the walk
/// inherits the sandbox's judgement for free: build output and dependency trees are pruned by name,
/// credential files and key material are never listed at all, and a junction pointing at half the
/// drive costs one entry rather than a traversal.
/// </para>
/// <para>
/// The walk is breadth-first, one directory per call, rather than a single recursive listing. Not a
/// preference - a recursive listing is capped at <c>MaxListEntries</c> for the whole subtree, which
/// is four hundred entries and would index the first corner of a real repository and call it the
/// project. A directory at a time gives each folder its own budget and reports truncation where it
/// actually happened.
/// </para>
/// <para>
/// The result is one change set. Not several, because a half-applied index is a graph describing a
/// folder that never existed, and because the log is meant to read as "indexed this project" rather
/// than as twenty thousand separate events.
/// </para>
/// </remarks>
public sealed class WorkspaceGraphIndexer : IGraphIndexer
{
    private readonly IWorkspaceService _workspace;
    private readonly IGraphService _graph;
    private readonly ISettingsService _settings;
    private readonly ILogger<WorkspaceGraphIndexer> _logger;

    public WorkspaceGraphIndexer(
        IWorkspaceService workspace,
        IGraphService graph,
        ISettingsService settings,
        ILogger<WorkspaceGraphIndexer> logger)
    {
        _workspace = workspace;
        _graph = graph;
        _settings = settings;
        _logger = logger;
    }

    public async Task<GraphResult<GraphIndexReport>> IndexAsync(
        IProgress<GraphIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_workspace.IsOpen || _workspace.Root is not { } root)
        {
            return GraphResult<GraphIndexReport>.Fail(
                "No folder is open. Choose a project folder before indexing it.");
        }

        if (!_graph.IsLoaded)
        {
            await _graph.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        var name = FolderName(root);
        var walk = new Walk(_graph.Current, _settings.Current.Canvas.MaxIndexedNodes);

        // The project itself is a node so that the whole graph has one root to lay out from, and so
        // that a decision or a requirement can be attached to the project rather than to a file.
        var projectId = walk.Node(GraphNodeKind.Project, ProjectKey, name, WorkspacePath.Root);

        var pending = new Queue<(WorkspacePath Directory, Guid ParentId)>();
        pending.Enqueue((WorkspacePath.Root, projectId));

        while (pending.Count > 0 && !walk.IsFull)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (directory, parentId) = pending.Dequeue();

            var listing = await _workspace
                .ListAsync(directory, recursive: false, cancellationToken)
                .ConfigureAwait(false);

            if (!listing.Success || listing.Value is null)
            {
                // A folder that cannot be read is a fact about the folder, not a failure of the
                // pass: permissions change, and a file is deleted between the listing and the read.
                walk.Refuse(listing.Error);
                continue;
            }

            if (listing.Value.IsTruncated)
            {
                walk.Truncate();
            }

            var order = 0;

            foreach (var entry in listing.Value.Entries)
            {
                if (walk.IsFull)
                {
                    walk.Truncate();
                    break;
                }

                var kind = entry.IsDirectory ? GraphNodeKind.Folder : GraphNodeKind.File;
                var childId = walk.Node(kind, entry.Path.Value, entry.Path.Name, entry.Path);

                walk.Contains(parentId, childId, order++);

                if (entry.IsDirectory)
                {
                    pending.Enqueue((entry.Path, childId));
                }
            }

            walk.ReportTo(progress, directory);
        }

        walk.MarkMissing();

        var report = await CommitAsync(walk, name, cancellationToken).ConfigureAwait(false);

        progress?.Report(new GraphIndexProgress { Nodes = walk.NodeCount, Edges = walk.EdgeCount });

        return report;
    }

    /// <summary>The key of the project node. Relative like every other, and the root is ".".</summary>
    private const string ProjectKey = ".";

    private async Task<GraphResult<GraphIndexReport>> CommitAsync(
        Walk walk,
        string name,
        CancellationToken cancellationToken)
    {
        var report = new GraphIndexReport
        {
            Root = name,
            Nodes = walk.NodeCount,
            Edges = walk.EdgeCount,
            Missing = walk.MissingCount,
            IsTruncated = walk.IsTruncated,
            Refused = walk.Refused,
        };

        var mutations = walk.Mutations;

        if (mutations.Count == 0)
        {
            // Nothing changed since the last pass. Applying an empty change set would still write a
            // log entry and raise an event, which is a rebuild of every card on the canvas for
            // nothing.
            _logger.LogInformation("Index of {Nodes} node(s) matched the graph already held.", walk.NodeCount);

            return GraphResult<GraphIndexReport>.Ok(report);
        }

        var change = GraphChangeSet.Create(
            $"Indexed {name}",
            GraphOrigin.Indexer,
            mutations);

        var applied = await _graph.ApplyAsync(change, cancellationToken).ConfigureAwait(false);

        if (!applied.Success || applied.Value is null)
        {
            return GraphResult<GraphIndexReport>.Fail(
                applied.Error ?? "The index could not be saved.");
        }

        IReadOnlyList<string> refused = walk.Refused.Count == 0
            ? applied.Value.Refused
            : [.. walk.Refused, .. applied.Value.Refused];

        _logger.LogInformation(
            "Indexed {Nodes} node(s) and {Edges} relation(s); {Applied} mutation(s) applied, {Refused} refused.",
            walk.NodeCount,
            walk.EdgeCount,
            applied.Value.Applied.Count,
            refused.Count);

        return GraphResult<GraphIndexReport>.Ok(report with { Refused = refused });
    }

    /// <summary>
    /// The last segment of the open folder's path, and never more than that.
    /// </summary>
    /// <remarks>
    /// The name goes into a node title, which is shown, logged and sent to a model. An absolute path
    /// on this platform usually contains the user's account name, so the fallback is a generic word
    /// rather than the path that could not be parsed.
    /// </remarks>
    private static string FolderName(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);

        return string.IsNullOrEmpty(name) ? "Project" : name;
    }

    /// <summary>
    /// The state of one pass: what it saw, what it wants to change, and what it was refused.
    /// </summary>
    /// <remarks>
    /// Nodes and edges are collected separately and concatenated at the end, because a change set is
    /// applied in order and an edge is refused unless both of its nodes are already there. Keeping
    /// two lists is cheaper than reasoning about interleaving, and it makes the resulting log read
    /// top-down: the tree, then the relations that join it.
    /// </remarks>
    private sealed class Walk
    {
        /// <summary>Nodes between progress reports. Frequent enough to look alive, rare enough to be free.</summary>
        private const int ReportEvery = 64;

        private readonly GraphSnapshot _graph;
        private readonly int _limit;
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly List<GraphMutation> _nodes = [];
        private readonly List<GraphMutation> _edges = [];
        private readonly HashSet<Guid> _seen = [];
        private readonly List<string> _refused = [];
        private int _reportedAt;

        public Walk(GraphSnapshot graph, int limit)
        {
            _graph = graph;
            _limit = Math.Max(1, limit);
        }

        public int NodeCount { get; private set; }

        public int EdgeCount { get; private set; }

        public int MissingCount { get; private set; }

        public bool IsTruncated { get; private set; }

        public bool IsFull => NodeCount >= _limit;

        public IReadOnlyList<string> Refused => _refused;

        public IReadOnlyList<GraphMutation> Mutations => [.. _nodes, .. _edges];

        public void Truncate() => IsTruncated = true;

        public void Refuse(string? error)
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                _refused.Add(error);
            }
        }

        /// <summary>
        /// Records one file or folder, reusing the node that already stands for it.
        /// </summary>
        /// <remarks>
        /// Identity is <c>(Kind, Key)</c> and the key is the workspace-relative path, so a second
        /// pass finds the same node and its placement, its hand-drawn relations and its history all
        /// survive. That is the difference between a graph and a cache.
        /// </remarks>
        public Guid Node(GraphNodeKind kind, string key, string title, WorkspacePath source)
        {
            NodeCount++;

            var existing = _graph.FindByKey(kind, key);

            if (existing is not null)
            {
                _seen.Add(existing.Id);

                // A node somebody took over is theirs. The change-set rules would refuse the write
                // anyway; not emitting it keeps the refusal list about real problems.
                if (!existing.IsIndexerOwned)
                {
                    return existing.Id;
                }

                // The row already says exactly this. Restating it would still count as a change, so
                // the graph would announce one, the canvas would rebuild every card, and the log would
                // keep an undo step for a pass that found the project exactly as it left it.
                if (Matches(existing, title, source))
                {
                    return existing.Id;
                }
            }

            var node = new GraphNode
            {
                Id = existing?.Id ?? Guid.CreateVersion7(),
                Kind = kind,
                Key = key,
                Title = title,
                Source = source,
                Origin = GraphOrigin.Indexer,

                // Archiving is a deliberate act, so a pass that finds the file again does not undo
                // it. Anything else becomes active: this is how a node comes back from Missing.
                Status = existing?.Status == GraphNodeStatus.Archived
                    ? GraphNodeStatus.Archived
                    : GraphNodeStatus.Active,
                CreatedAt = existing?.CreatedAt ?? _now,
                UpdatedAt = _now,
            };

            _nodes.Add(new GraphMutation.AddNode(node));
            _seen.Add(node.Id);

            return node.Id;
        }

        /// <summary>
        /// Whether the node already holds everything this pass would write to it.
        /// </summary>
        /// <remarks>
        /// Kind and key are not compared because the node was found by them. <c>UpdatedAt</c> is not
        /// compared on purpose: a stamp that moves every pass would make every node differ from
        /// itself and nothing would ever match. Neither is anything this pass does not set - a
        /// summary or a line span another indexer wrote is not this one's to overwrite.
        /// </remarks>
        private static bool Matches(GraphNode node, string title, WorkspacePath source) =>
            node.Title == title
            && node.Source == source

            // A file that came back is a change: the pass would move it from Missing to Active, and
            // the user has to see the card stop being greyed out.
            && node.Status != GraphNodeStatus.Missing;

        /// <summary>Records that the parent contains the child, if it does not already say so.</summary>
        /// <remarks>
        /// <paramref name="order"/> is the position in the listing, which the sandbox already returns
        /// with directories first and then by name. Carrying it means the canvas and the inspector
        /// show a folder's contents in the order a file explorer would, rather than alphabetically
        /// with the directories scattered through it.
        /// </remarks>
        public void Contains(Guid parentId, Guid childId, int order)
        {
            EdgeCount++;

            foreach (var edge in _graph.Outgoing(parentId))
            {
                if (edge.ToId == childId && edge.Kind == GraphEdgeKind.Contains)
                {
                    return;
                }
            }

            _edges.Add(new GraphMutation.AddEdge(new GraphEdge
            {
                Id = Guid.CreateVersion7(),
                FromId = parentId,
                ToId = childId,
                Kind = GraphEdgeKind.Contains,
                Origin = GraphOrigin.Indexer,
                Order = order,
                CreatedAt = _now,
            }));
        }

        /// <summary>
        /// Marks what this pass owned but did not find as <see cref="GraphNodeStatus.Missing"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Not deleted. A file that is gone may come back under the same name after a branch switch,
        /// and deleting the node would take the position somebody chose for it, the component they
        /// filed it under and every note attached to it. Missing is a status a person can see and act
        /// on; deletion is a decision only they should make.
        /// </para>
        /// <para>
        /// Skipped entirely when the walk was truncated. A pass that stopped early has no grounds to
        /// call anything gone - unseen would mean "beyond the cap", and the whole tail of the project
        /// would grey out on the strength of a limit.
        /// </para>
        /// </remarks>
        public void MarkMissing()
        {
            if (IsTruncated)
            {
                return;
            }

            foreach (var node in _graph.Nodes)
            {
                // Only the kinds this pass is responsible for. A later indexer owns types and
                // members, and a file walk knowing nothing about them must not declare them gone.
                if (!node.IsIndexerOwned || _seen.Contains(node.Id) || !IsFileSystemKind(node.Kind))
                {
                    continue;
                }

                MissingCount++;

                if (node.Status != GraphNodeStatus.Missing)
                {
                    _nodes.Add(new GraphMutation.UpdateNode(node with
                    {
                        Status = GraphNodeStatus.Missing,
                        UpdatedAt = _now,
                    }));
                }
            }
        }

        public void ReportTo(IProgress<GraphIndexProgress>? progress, WorkspacePath directory)
        {
            if (progress is null || NodeCount - _reportedAt < ReportEvery)
            {
                return;
            }

            _reportedAt = NodeCount;

            progress.Report(new GraphIndexProgress
            {
                Nodes = NodeCount,
                Edges = EdgeCount,
                Path = directory.ToString(),
            });
        }

        private static bool IsFileSystemKind(GraphNodeKind kind) =>
            kind == GraphNodeKind.File ||
            kind == GraphNodeKind.Folder ||
            kind == GraphNodeKind.Project;
    }
}
