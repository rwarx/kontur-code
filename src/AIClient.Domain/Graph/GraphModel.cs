namespace AIClient.Domain.Graph;

/// <summary>
/// The mutable working state of the graph, and the only thing allowed to change it.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation arrives as a <see cref="GraphChangeSet"/> and is applied best-effort
/// across the set: a change that cannot be applied (duplicate id, edge to a missing node)
/// is rejected with a reason and the rest still land. That is the same discipline as the
/// workspace results one layer up, and it is there for the same reason - change sets are
/// authored by models as often as by users, and a model-authored set must degrade, not
/// explode, or the agent pipeline turns every typo into a crash.
/// </para>
/// <para>
/// The model is not thread-safe. Whoever owns one serialises access; in this application
/// that is the graph service marshalling onto the UI thread, the same contract the rest of
/// the observable state follows.
/// </para>
/// <para>
/// <see cref="Snapshot"/> is O(1): it is rebuilt after each apply that changed something,
/// never on demand, so a reader that polls every frame costs a property read.
/// </para>
/// <para>
/// The model knows nothing about history - undo and redo live one layer up, as snapshots
/// this class restored with <see cref="Restore(GraphSnapshot?)"/>. Keeping the timeline out
/// of the mutator is what allows a restore to be the same operation as an undo.
/// </para>
/// </remarks>
public sealed class GraphModel
{
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphEdge> _edges = new(StringComparer.Ordinal);
    private int _version;

    /// <summary>
    /// The current state, always complete and always consistent with the changes that have
    /// been applied so far.
    /// </summary>
    /// <remarks>
    /// Rebuilt after every apply that lands at least one change, so it never shows a
    /// half-applied set.
    /// </remarks>
    public GraphSnapshot Snapshot { get; private set; } = GraphSnapshot.Empty;

    /// <summary>
    /// Applies a change set best-effort: each change either lands or is rejected with a
    /// reason, and the rest of the set is unaffected by either outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Refusals, exactly:
    /// </para>
    /// <para>
    /// <see cref="AddNode"/> - the id is already in use; the title is missing; the width or
    /// height is zero, negative or not a finite number.
    /// </para>
    /// <para>
    /// <see cref="UpdateNode"/> - the node does not exist; nothing was asked to change; the
    /// new title is empty (mirroring the add rule, so a node cannot lose its title through
    /// the back door).
    /// </para>
    /// <para>
    /// <see cref="MoveNode"/> - the node does not exist; the position is not a finite
    /// number.
    /// </para>
    /// <para>
    /// <see cref="RemoveNode"/> - never refused. Removing a node that is not there is an
    /// applied no-op, because the indexer and an agent edit may both delete the same file,
    /// and the second one to arrive is not an error. Edges incident to a removed node are
    /// removed with it.
    /// </para>
    /// <para>
    /// <see cref="AddEdge"/> - the id is already in use; an endpoint is missing (both ids
    /// are named in the reason); the edge joins a node to itself.
    /// </para>
    /// <para>
    /// <see cref="UpdateEdge"/> - the edge does not exist; nothing was asked to change.
    /// <see cref="RemoveEdge"/> - never refused, for the same reason as node removal.
    /// </para>
    /// <para>
    /// The version is bumped by one whenever at least one change was applied - and only
    /// then. That is a plain rule chosen over "bump only when the content changed",
    /// because whether a no-op removal changed the content is a question with two defensible
    /// answers, and a rule that needs a debate is a rule the timeline will get wrong. The
    /// cost of the simple version is that a set of pure no-op removals still bumps, which
    /// is invisible except to change detection.
    /// </para>
    /// </remarks>
    public GraphMutationResult Apply(GraphChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);

        var applied = new List<GraphChange>();
        var rejected = new List<string>();

        foreach (var change in changeSet.Changes)
        {
            switch (change)
            {
                case AddNode add:
                    if (TryApplyAddNode(add.Node, rejected))
                    {
                        applied.Add(add);
                    }

                    break;

                case UpdateNode update:
                    if (TryApplyUpdateNode(update, rejected))
                    {
                        applied.Add(update);
                    }

                    break;

                case MoveNode move:
                    if (TryApplyMoveNode(move, rejected))
                    {
                        applied.Add(move);
                    }

                    break;

                case RemoveNode remove:
                    // Always applied: removal is idempotent by design.
                    TryRemoveNode(remove.NodeId);
                    applied.Add(remove);
                    break;

                case AddEdge addEdge:
                    if (TryApplyAddEdge(addEdge.Edge, rejected))
                    {
                        applied.Add(addEdge);
                    }

                    break;

                case UpdateEdge updateEdge:
                    if (TryApplyUpdateEdge(updateEdge, rejected))
                    {
                        applied.Add(updateEdge);
                    }

                    break;

                case RemoveEdge removeEdge:
                    // Always applied: removal is idempotent by design.
                    TryRemoveEdge(removeEdge.EdgeId);
                    applied.Add(removeEdge);
                    break;

                case null:
                    rejected.Add("A change in the set is null and was skipped.");
                    break;
            }
        }

        if (applied.Count > 0)
        {
            _version++;
            Snapshot = RebuildSnapshot();
        }

        return new GraphMutationResult
        {
            Snapshot = Snapshot,
            Applied = applied,
            Rejected = rejected,
        };
    }

    /// <summary>
    /// Loads a snapshot wholesale (undo, restore, persistence) without going through
    /// changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Version is taken as given, so a restored graph keeps its history identity rather
    /// than restarting at zero - an undo that arrives at a lower version than the one it
    /// removed is legible, and a reloaded graph is recognisably the same document.
    /// </para>
    /// <para>
    /// A null snapshot resets to <see cref="GraphSnapshot.Empty"/> and version 0: the
    /// callers that can hand null (a load that found nothing) want exactly that. The
    /// snapshot's own content is taken as given too - entries without an id are skipped
    /// rather than refused, because the point of a restore is to trust the source.
    /// </para>
    /// </remarks>
    public void Restore(GraphSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            _nodes.Clear();
            _edges.Clear();
            _version = 0;
            Snapshot = GraphSnapshot.Empty;
            return;
        }

        _nodes.Clear();

        foreach (var node in snapshot.Nodes)
        {
            if (string.IsNullOrEmpty(node.Id))
            {
                continue;
            }

            // Assignment rather than first-wins: a restored snapshot is taken as given, and
            // a duplicate can only have come from a hand-edited file.
            _nodes[node.Id] = node;
        }

        _edges.Clear();

        foreach (var edge in snapshot.Edges)
        {
            if (string.IsNullOrEmpty(edge.Id))
            {
                continue;
            }

            _edges[edge.Id] = edge;
        }

        _version = snapshot.Version;
        Snapshot = snapshot;
    }

    private bool TryApplyAddNode(GraphNode node, List<string> rejected)
    {
        if (string.IsNullOrWhiteSpace(node.Id))
        {
            rejected.Add("A node needs an id.");
            return false;
        }

        if (_nodes.ContainsKey(node.Id))
        {
            rejected.Add($"Node '{node.Id}' already exists.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(node.Title))
        {
            rejected.Add($"Node '{node.Id}' cannot be added without a title.");
            return false;
        }

        if (!IsUsableSize(node.Width) || !IsUsableSize(node.Height))
        {
            rejected.Add(
                $"Node '{node.Id}' cannot be added with a width or height that is zero, negative or not a number.");
            return false;
        }

        _nodes.Add(node.Id, node);
        return true;
    }

    private bool TryApplyUpdateNode(UpdateNode change, List<string> rejected)
    {
        if (string.IsNullOrWhiteSpace(change.NodeId))
        {
            rejected.Add("A node change needs a node id.");
            return false;
        }

        if (!_nodes.TryGetValue(change.NodeId, out var node))
        {
            rejected.Add($"Node '{change.NodeId}' does not exist.");
            return false;
        }

        if (change.Title is null && change.Subtitle is null && change.Detail is null && change.Kind is null)
        {
            rejected.Add($"Node '{change.NodeId}' has nothing to change.");
            return false;
        }

        if (change.Title is not null && string.IsNullOrWhiteSpace(change.Title))
        {
            rejected.Add($"Node '{change.NodeId}' cannot be renamed without a title.");
            return false;
        }

        _nodes[change.NodeId] = node with
        {
            Title = change.Title ?? node.Title,
            Subtitle = change.Subtitle ?? node.Subtitle,
            Detail = change.Detail ?? node.Detail,
            Kind = change.Kind ?? node.Kind,
        };

        return true;
    }

    private bool TryApplyMoveNode(MoveNode change, List<string> rejected)
    {
        if (string.IsNullOrWhiteSpace(change.NodeId))
        {
            rejected.Add("A node change needs a node id.");
            return false;
        }

        if (!_nodes.TryGetValue(change.NodeId, out var node))
        {
            rejected.Add($"Node '{change.NodeId}' does not exist.");
            return false;
        }

        if (!double.IsFinite(change.X) || !double.IsFinite(change.Y))
        {
            rejected.Add($"Node '{change.NodeId}' cannot be moved to a position that is not a number.");
            return false;
        }

        _nodes[change.NodeId] = node with { X = change.X, Y = change.Y };
        return true;
    }

    private void TryRemoveNode(string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId) || !_nodes.Remove(nodeId))
        {
            return;
        }

        // The edges joined to a removed node go with it, collected first because the
        // dictionary cannot be edited while it is being walked.
        var incident = _edges.Values
            .Where(edge => edge.SourceId == nodeId || edge.TargetId == nodeId)
            .Select(edge => edge.Id)
            .ToList();

        foreach (var edgeId in incident)
        {
            _edges.Remove(edgeId);
        }
    }

    private bool TryApplyAddEdge(GraphEdge edge, List<string> rejected)
    {
        if (string.IsNullOrWhiteSpace(edge.Id))
        {
            rejected.Add("An edge needs an id.");
            return false;
        }

        if (_edges.ContainsKey(edge.Id))
        {
            rejected.Add($"Edge '{edge.Id}' already exists.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(edge.SourceId) || string.IsNullOrWhiteSpace(edge.TargetId))
        {
            rejected.Add($"Edge '{edge.Id}' cannot be added without the two nodes it joins.");
            return false;
        }

        if (edge.SourceId == edge.TargetId)
        {
            rejected.Add($"Edge '{edge.Id}' cannot join node '{edge.SourceId}' to itself.");
            return false;
        }

        if (!_nodes.ContainsKey(edge.SourceId) || !_nodes.ContainsKey(edge.TargetId))
        {
            rejected.Add(
                $"Edge '{edge.Id}' cannot be added: node '{edge.SourceId}' and node '{edge.TargetId}' have to exist first.");
            return false;
        }

        _edges.Add(edge.Id, edge);
        return true;
    }

    private bool TryApplyUpdateEdge(UpdateEdge change, List<string> rejected)
    {
        if (string.IsNullOrWhiteSpace(change.EdgeId))
        {
            rejected.Add("An edge change needs an edge id.");
            return false;
        }

        if (!_edges.TryGetValue(change.EdgeId, out var edge))
        {
            rejected.Add($"Edge '{change.EdgeId}' does not exist.");
            return false;
        }

        if (change.Kind is null && change.Label is null)
        {
            rejected.Add($"Edge '{change.EdgeId}' has nothing to change.");
            return false;
        }

        _edges[change.EdgeId] = edge with
        {
            Kind = change.Kind ?? edge.Kind,
            Label = change.Label ?? edge.Label,
        };

        return true;
    }

    private void TryRemoveEdge(string? edgeId)
    {
        if (string.IsNullOrEmpty(edgeId))
        {
            return;
        }

        _edges.Remove(edgeId);
    }

    /// <summary>
    /// Rebuilds the snapshot from the dictionaries, in insertion order, so the same
    /// sequence of change sets always produces the same node and edge lists.
    /// </summary>
    private GraphSnapshot RebuildSnapshot() => new()
    {
        Version = _version,
        Nodes = [.. _nodes.Values],
        Edges = [.. _edges.Values],
    };

    /// <summary>Widths and heights must be numbers greater than zero; anything else cannot be drawn.</summary>
    private static bool IsUsableSize(double value) => double.IsFinite(value) && value > 0;
}

/// <summary>
/// What actually happened when a change set met the graph.
/// </summary>
/// <remarks>
/// <para>
/// A change set is best-effort, so the outcome has two halves: what landed and what was
/// refused. The refusals are human-readable invariant English - they surface in tool
/// results and the timeline, where a model reads them and corrects itself, so each one
/// names the id it is about rather than assuming the caller still knows which change was
/// which.
/// </para>
/// <para>
/// There is deliberately no undo payload here: history is the service's business, kept as
/// the snapshots each result replaced, which keeps this record a plain report rather than
/// a command.
/// </para>
/// </remarks>
public sealed record GraphMutationResult
{
    /// <summary>The graph as it stands after the change set was applied.</summary>
    public required GraphSnapshot Snapshot { get; init; }

    /// <summary>
    /// The changes that landed, in the order they were applied - including removals of
    /// things that were not there, which count as landed no-ops.
    /// </summary>
    public required IReadOnlyList<GraphChange> Applied { get; init; }

    /// <summary>
    /// The reasons the remaining changes were skipped, one per refused change, each naming
    /// the id involved. Empty when everything landed.
    /// </summary>
    public required IReadOnlyList<string> Rejected { get; init; }
}
