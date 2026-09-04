using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Entities;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using AIClient.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Graph;

/// <summary>
/// The knowledge graph, held in memory and written through to SQLite.
/// </summary>
/// <remarks>
/// <para>
/// The asymmetry is the design. Reads are a field access, because hovering a card, drawing a hundred
/// edges and assembling a context block all read the graph and none of them can afford a query.
/// Writes go to disk before they go to memory, because a model of the project that survives only
/// until the next crash is worth less than no model at all.
/// </para>
/// <para>
/// Every write follows the same three steps: work out what the change does against the current
/// snapshot, save the rows and the log entry in one transaction, and only then publish the new
/// snapshot. A failure at the second step therefore leaves memory holding the graph as it was, which
/// is the state storage is still in.
/// </para>
/// </remarks>
public sealed class GraphService : IGraphService
{
    /// <summary>
    /// How many identities to look up per query.
    /// </summary>
    /// <remarks>
    /// An indexing pass touches thousands of rows in one change set, and one query per row is the
    /// difference between a folder that opens and a folder that appears to hang. Chunked rather than
    /// unbounded because the ids become SQL parameters.
    /// </remarks>
    private const int LookupChunk = 400;

    private readonly IDbContextFactory<AIClientDbContext> _contextFactory;
    private readonly ILogger<GraphService> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public GraphService(
        IDbContextFactory<AIClientDbContext> contextFactory,
        ILogger<GraphService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>Empty until <see cref="LoadAsync"/> has run, which is also what a closed folder looks like.</summary>
    public GraphSnapshot Current { get; private set; } = GraphSnapshot.Empty;

    public bool IsLoaded { get; private set; }

    public event EventHandler<GraphChangedEventArgs>? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(announce: false, cancellationToken);

    public Task ReloadAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(announce: true, cancellationToken);

    public Task<GraphResult<GraphApplyResult>> ApplyAsync(
        GraphChangeSet change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        return CommitAsync(
            change,
            GraphChangeState.Applied,
            // A change made directly gets a fresh log entry. Written in the same transaction as the
            // rows: a change that took effect but was never recorded could not be undone, and a
            // record of a change that never happened would offer to undo nothing.
            (db, result, _) =>
            {
                db.GraphChanges.Add(Journal(
                    change,
                    GraphChangeState.Applied,
                    result.Applied,
                    result.Inverse));

                return Task.CompletedTask;
            },
            cancellationToken);
    }

    public async Task<GraphResult<GraphChangeSet>> ProposeAsync(
        GraphChangeSet change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (!IsLoaded)
        {
            return GraphResult<GraphChangeSet>.Fail(NotLoaded);
        }

        if (change.Mutations.Count == 0)
        {
            return GraphResult<GraphChangeSet>.Fail("A proposal has to suggest something.");
        }

        var proposal = change with { State = GraphChangeState.Proposed };

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Applied to a copy and then thrown away. The point is to turn down an impossible
            // suggestion while the model is still in a position to try another one, rather than at
            // the moment a person presses Apply on a ghost that was never going to work.
            var rehearsal = GraphMutator.Apply(Current, proposal);

            if (rehearsal.Applied.Count == 0)
            {
                return GraphResult<GraphChangeSet>.Fail(rehearsal.Refused.Count > 0
                    ? string.Join(" ", rehearsal.Refused)
                    : "Nothing in the proposal would change the graph.");
            }

            await using var db = await _contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            // The suggestion is stored as it was made, not as the rehearsal reduced it. A person is
            // about to read this back as "what the model wants to do".
            db.GraphChanges.Add(Journal(
                proposal,
                GraphChangeState.Proposed,
                proposal.Mutations,
                inverse: []));

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Could not record proposal {ChangeId}.", proposal.Id);

            return GraphResult<GraphChangeSet>.Fail($"The proposal could not be saved: {ex.Message}");
        }
        finally
        {
            _mutex.Release();
        }

        // Nothing moved, and the canvas is told exactly that: the mutations are what it draws as
        // ghost cards and dotted edges, and the state is what stops it drawing them as fact.
        Changed?.Invoke(this, new GraphChangedEventArgs
        {
            Snapshot = Current,
            Applied = proposal.Mutations,
            ChangeId = proposal.Id,
            Origin = proposal.Origin,
            State = GraphChangeState.Proposed,
        });

        return GraphResult<GraphChangeSet>.Ok(proposal);
    }

    public async Task<GraphResult<GraphApplyResult>> AcceptAsync(
        Guid changeId,
        CancellationToken cancellationToken = default)
    {
        var (row, error) = await FindAsync(changeId, GraphChangeState.Proposed, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return GraphResult<GraphApplyResult>.Fail(error!);
        }

        if (!GraphChangeJson.TryRead(row.MutationsJson, out var mutations) || mutations.Count == 0)
        {
            return GraphResult<GraphApplyResult>.Fail(
                "This proposal could not be read back from the change log, so there is nothing to apply.");
        }

        // Rebuilt from the row rather than kept in memory: a proposal may have been written days ago,
        // by a different run of the application.
        var change = new GraphChangeSet
        {
            Id = row.Id,
            Summary = row.Summary,
            Mutations = mutations,
            Origin = row.Origin,
            SourceExecutionId = row.SourceExecutionId,
            CreatedAt = row.CreatedAt,
        };

        return await CommitAsync(
            change,
            GraphChangeState.Applied,
            async (db, result, token) =>
            {
                var entry = await db.GraphChanges
                    .FirstOrDefaultAsync(r => r.Id == change.Id, token)
                    .ConfigureAwait(false);

                if (entry is null)
                {
                    db.GraphChanges.Add(Journal(
                        change, GraphChangeState.Applied, result.Applied, result.Inverse));

                    return;
                }

                // The entry is rewritten rather than duplicated, and now holds what took effect
                // instead of what was suggested. The two differ only where something was refused,
                // and in that case what took effect is the more useful record to keep.
                entry.State = GraphChangeState.Applied;
                entry.MutationsJson = GraphChangeJson.Write(result.Applied);
                entry.InverseJson = Write(result.Inverse);
                entry.AppliedAt = DateTimeOffset.UtcNow;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GraphResult<GraphChangeSet>> DiscardAsync(
        Guid changeId,
        CancellationToken cancellationToken = default)
    {
        var (row, error) = await FindAsync(changeId, GraphChangeState.Proposed, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return GraphResult<GraphChangeSet>.Fail(error!);
        }

        GraphChangeSet discarded;

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            var entry = await db.GraphChanges
                .FirstOrDefaultAsync(r => r.Id == changeId, cancellationToken)
                .ConfigureAwait(false);

            if (entry is null)
            {
                return GraphResult<GraphChangeSet>.Fail($"There is no change {changeId} in the log.");
            }

            // Kept rather than deleted. What a model suggested and a person turned down is a fact
            // about the project's history, and one worth having when the same suggestion arrives
            // again.
            entry.State = GraphChangeState.Discarded;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            discarded = ToChangeSet(entry);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Could not discard proposal {ChangeId}.", changeId);

            return GraphResult<GraphChangeSet>.Fail($"The proposal could not be updated: {ex.Message}");
        }
        finally
        {
            _mutex.Release();
        }

        // No snapshot moved, and the canvas needs to know only that this ghost is gone.
        Changed?.Invoke(this, new GraphChangedEventArgs
        {
            Snapshot = Current,
            Applied = [],
            ChangeId = changeId,
            Origin = discarded.Origin,
            State = GraphChangeState.Discarded,
        });

        return GraphResult<GraphChangeSet>.Ok(discarded);
    }

    public async Task<GraphResult<GraphApplyResult>> RevertAsync(
        Guid changeId,
        CancellationToken cancellationToken = default)
    {
        var (row, error) = await FindAsync(changeId, GraphChangeState.Applied, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return GraphResult<GraphApplyResult>.Fail(error!);
        }

        if (!GraphChangeJson.TryRead(row.InverseJson, out var inverse) || inverse.Count == 0)
        {
            return GraphResult<GraphApplyResult>.Fail(
                "This change did not record how to undo it, so it cannot be undone.");
        }

        // Undone as the user, whatever made the change. The inverse was already proved legal when it
        // was computed, and re-checking it as the indexer would refuse to undo an indexing pass -
        // which is precisely the change a person is most likely to want back.
        var undo = new GraphChangeSet
        {
            // The same id on purpose. No new row is written for an undo, so nothing can collide, and
            // the announcement then names the log entry whose state actually changed.
            Id = changeId,
            Summary = $"Undo: {row.Summary}",
            Mutations = inverse,
            Origin = GraphOrigin.User,
        };

        return await CommitAsync(
            undo,
            GraphChangeState.Reverted,
            async (db, _, token) =>
            {
                var entry = await db.GraphChanges
                    .FirstOrDefaultAsync(r => r.Id == changeId, token)
                    .ConfigureAwait(false);

                if (entry is not null)
                {
                    entry.State = GraphChangeState.Reverted;
                }

                // The undo adds no entry of its own. This log is a record of how the project got to
                // where it is, and a pair of entries saying "went back" and "came forward" would make
                // it a record of which buttons were pressed instead. Redo, when there is one, re-applies
                // the reverted entry rather than inverting this.
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GraphChangeSet>> HistoryAsync(
        GraphChangeState? state = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        // No lock: this reads its own rows through its own context and never touches the snapshot,
        // so a timeline being drawn cannot hold up an indexing pass.
        await using var db = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var query = db.GraphChanges.AsNoTracking();

        if (state is { } wanted)
        {
            query = query.Where(r => r.State == wanted);
        }

        var rows = await query
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToChangeSet).ToList();
    }

    private const string NotLoaded =
        "The graph has not been read from storage yet, so it cannot be changed.";

    /// <summary>Replaces the in-memory graph with what storage holds.</summary>
    /// <param name="announce">
    /// Whether to raise <see cref="Changed"/> as a reload. False for the first read, where nobody is
    /// subscribed yet and every reader is about to look at <see cref="Current"/> anyway.
    /// </param>
    private async Task ReadAsync(bool announce, CancellationToken cancellationToken)
    {
        GraphSnapshot snapshot;

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            // Two queries, not a join: the graph is loaded whole, and matching edges to nodes is what
            // the snapshot's constructor does anyway.
            var nodes = await db.GraphNodes
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var edges = await db.GraphEdges
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            snapshot = GraphSnapshot.Create(
                nodes.Select(GraphRowMapper.ToDomain),
                edges.Select(GraphRowMapper.ToDomain),

                // Bumped rather than reset, so a reader comparing versions across a reload sees a
                // number it has never seen before and rebuilds.
                Current.Version + 1);

            Current = snapshot;
            IsLoaded = true;
        }
        finally
        {
            _mutex.Release();
        }

        _logger.LogInformation(
            "Read {NodeCount} graph node(s) and {EdgeCount} edge(s) from storage.",
            snapshot.NodeCount,
            snapshot.EdgeCount);

        if (announce)
        {
            // Raised outside the lock: a handler that reads the graph must not deadlock.
            Changed?.Invoke(this, new GraphChangedEventArgs
            {
                Snapshot = snapshot,
                Applied = [],
                IsReload = true,
            });
        }
    }

    /// <summary>
    /// The one write path: apply, persist, publish.
    /// </summary>
    /// <param name="state">What to tell subscribers the change ended up as.</param>
    /// <param name="journal">
    /// How this change is recorded. The only thing that differs between applying, accepting and
    /// undoing, which is why it is a parameter rather than three copies of everything else.
    /// </param>
    private async Task<GraphResult<GraphApplyResult>> CommitAsync(
        GraphChangeSet change,
        GraphChangeState state,
        Func<AIClientDbContext, GraphApplyResult, CancellationToken, Task> journal,
        CancellationToken cancellationToken)
    {
        if (!IsLoaded)
        {
            return GraphResult<GraphApplyResult>.Fail(NotLoaded);
        }

        GraphApplyResult result;

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            result = GraphMutator.Apply(Current, change);

            if (!result.Changed)
            {
                // Nothing took effect, so there is nothing to record and nothing to redraw. The
                // refusals are still the answer the caller asked for.
                _logger.LogInformation(
                    "Graph change {ChangeId} changed nothing. {Refused}",
                    change.Id,
                    string.Join(" ", result.Refused));

                return GraphResult<GraphApplyResult>.Ok(result);
            }

            await using var db = await _contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            await WriteAsync(db, result.Applied, cancellationToken).ConfigureAwait(false);
            await journal(db, result, cancellationToken).ConfigureAwait(false);

            // One transaction for the rows and the log entry together.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Published only after the save returned. Until this line, every reader is looking at the
            // graph storage still holds.
            Current = result.Snapshot;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Could not write graph change {ChangeId}.", change.Id);

            return GraphResult<GraphApplyResult>.Fail($"The change could not be saved: {ex.Message}");
        }
        finally
        {
            _mutex.Release();
        }

        if (result.Refused.Count > 0)
        {
            _logger.LogWarning(
                "Graph change {ChangeId} applied {Applied} mutation(s) and refused {Count}. {Refused}",
                change.Id,
                result.Applied.Count,
                result.Refused.Count,
                string.Join(" ", result.Refused));
        }

        Changed?.Invoke(this, new GraphChangedEventArgs
        {
            Snapshot = result.Snapshot,
            Applied = result.Applied,
            ChangeId = change.Id,
            Origin = change.Origin,
            State = state,
        });

        return GraphResult<GraphApplyResult>.Ok(result);
    }

    /// <summary>
    /// Turns the mutations that took effect into row operations.
    /// </summary>
    /// <remarks>
    /// No rule is re-checked here and no decision is re-made: the list has already been validated,
    /// reduced to primitives and expanded where one mutation implied several. Adds and updates are
    /// both treated as an upsert, so a snapshot that has drifted from storage heals on the next write
    /// instead of failing on it.
    /// </remarks>
    private static async Task WriteAsync(
        AIClientDbContext db,
        IReadOnlyList<GraphMutation> applied,
        CancellationToken cancellationToken)
    {
        var nodeIds = new HashSet<Guid>();
        var edgeIds = new HashSet<Guid>();

        foreach (var mutation in applied)
        {
            switch (mutation)
            {
                case GraphMutation.AddNode add:
                    nodeIds.Add(add.Node.Id);
                    break;

                case GraphMutation.UpdateNode edit:
                    nodeIds.Add(edit.Node.Id);
                    break;

                case GraphMutation.RemoveNode drop:
                    nodeIds.Add(drop.NodeId);
                    break;

                case GraphMutation.AddEdge add:
                    edgeIds.Add(add.Edge.Id);
                    break;

                case GraphMutation.RemoveEdge drop:
                    edgeIds.Add(drop.EdgeId);
                    break;
            }
        }

        // Everything the batch will touch, fetched up front. One pass over the ids, then no further
        // queries however many mutations there are.
        var nodes = await LoadNodesAsync(db, nodeIds, cancellationToken).ConfigureAwait(false);
        var edges = await LoadEdgesAsync(db, edgeIds, cancellationToken).ConfigureAwait(false);

        var droppedNodes = new Dictionary<Guid, GraphNodeRow>();
        var droppedEdges = new Dictionary<Guid, GraphEdgeRow>();

        foreach (var mutation in applied)
        {
            switch (mutation)
            {
                case GraphMutation.AddNode add:
                    PutNode(add.Node);
                    break;

                case GraphMutation.UpdateNode edit:
                    PutNode(edit.Node);
                    break;

                case GraphMutation.RemoveNode drop:
                    DropNode(drop.NodeId);
                    break;

                case GraphMutation.AddEdge add:
                    PutEdge(add.Edge);
                    break;

                case GraphMutation.RemoveEdge drop:
                    DropEdge(drop.EdgeId);
                    break;
            }
        }

        void PutNode(GraphNode node)
        {
            if (nodes.TryGetValue(node.Id, out var row))
            {
                // Filled rather than replaced: the row is tracked, and a new instance would be a
                // delete and an insert, which would take its placements and edges down with it.
                GraphRowMapper.Fill(row, node);
                return;
            }

            if (droppedNodes.Remove(node.Id, out row))
            {
                // Removed and put back inside one batch - two files swapping names, for instance.
                // Reviving the tracked row is the only way: a second instance with the same primary
                // key is an exception out of the change tracker.
                db.Entry(GraphRowMapper.Fill(row, node)).State = EntityState.Modified;
                nodes[node.Id] = row;
                return;
            }

            row = GraphRowMapper.ToRow(node);
            nodes[node.Id] = row;
            db.GraphNodes.Add(row);
        }

        void DropNode(Guid id)
        {
            if (nodes.Remove(id, out var row))
            {
                db.GraphNodes.Remove(row);
                droppedNodes[id] = row;
            }
        }

        void PutEdge(GraphEdge edge)
        {
            if (edges.TryGetValue(edge.Id, out var row))
            {
                GraphRowMapper.Fill(row, edge);
                return;
            }

            if (droppedEdges.Remove(edge.Id, out row))
            {
                db.Entry(GraphRowMapper.Fill(row, edge)).State = EntityState.Modified;
                edges[edge.Id] = row;
                return;
            }

            row = GraphRowMapper.ToRow(edge);
            edges[edge.Id] = row;
            db.GraphEdges.Add(row);
        }

        void DropEdge(Guid id)
        {
            if (edges.Remove(id, out var row))
            {
                db.GraphEdges.Remove(row);
                droppedEdges[id] = row;
            }
        }
    }

    /// <summary>Fetches the node rows a batch will touch, tracked, in chunks.</summary>
    private static async Task<Dictionary<Guid, GraphNodeRow>> LoadNodesAsync(
        AIClientDbContext db,
        HashSet<Guid> ids,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<Guid, GraphNodeRow>(ids.Count);

        foreach (var chunk in ids.Chunk(LookupChunk))
        {
            var found = await db.GraphNodes
                .Where(row => chunk.Contains(row.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in found)
            {
                rows[row.Id] = row;
            }
        }

        return rows;
    }

    private static async Task<Dictionary<Guid, GraphEdgeRow>> LoadEdgesAsync(
        AIClientDbContext db,
        HashSet<Guid> ids,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<Guid, GraphEdgeRow>(ids.Count);

        foreach (var chunk in ids.Chunk(LookupChunk))
        {
            var found = await db.GraphEdges
                .Where(row => chunk.Contains(row.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in found)
            {
                rows[row.Id] = row;
            }
        }

        return rows;
    }

    /// <summary>Reads a log entry and checks it is in the state the caller assumed.</summary>
    /// <remarks>
    /// The error is written for a person: "already applied" and "no such change" are the two ways an
    /// Apply button on a stale ghost card can fail, and both need to say so rather than throw.
    /// </remarks>
    private async Task<(GraphChangeRow? Row, string? Error)> FindAsync(
        Guid changeId,
        GraphChangeState expected,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await db.GraphChanges
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == changeId, cancellationToken)
            .ConfigureAwait(false);

        return row switch
        {
            null => (null, $"There is no change {changeId} in the log."),
            _ when row.State != expected =>
                (null, $"Change \"{row.Summary}\" is {Describe(row.State)}, not {Describe(expected)}."),
            _ => (row, null),
        };
    }

    private static string Describe(GraphChangeState state) => state switch
    {
        GraphChangeState.Proposed => "a proposal",
        GraphChangeState.Applied => "applied",
        GraphChangeState.Discarded => "discarded",
        GraphChangeState.Reverted => "already undone",
        _ => state.ToString(),
    };

    /// <summary>Builds the log row for a change.</summary>
    private static GraphChangeRow Journal(
        GraphChangeSet change,
        GraphChangeState state,
        IReadOnlyList<GraphMutation> mutations,
        IReadOnlyList<GraphMutation> inverse) => new()
    {
        Id = change.Id,
        Summary = change.Summary,
        Origin = change.Origin,
        State = state,
        SourceExecutionId = change.SourceExecutionId,
        MutationsJson = GraphChangeJson.Write(mutations),
        InverseJson = Write(inverse),
        CreatedAt = change.CreatedAt,
        AppliedAt = state == GraphChangeState.Applied ? DateTimeOffset.UtcNow : null,
    };

    private static GraphChangeSet ToChangeSet(GraphChangeRow row)
    {
        // A damaged entry becomes an entry with no detail rather than an exception: the timeline can
        // still show that something happened and when, and the operations that need the mutations -
        // accept and undo - check for themselves and refuse.
        GraphChangeJson.TryRead(row.MutationsJson, out var mutations);
        GraphChangeJson.TryRead(row.InverseJson, out var inverse);

        return new GraphChangeSet
        {
            Id = row.Id,
            Summary = row.Summary,
            Mutations = mutations,
            Inverse = inverse,
            Origin = row.Origin,
            State = row.State,
            SourceExecutionId = row.SourceExecutionId,
            CreatedAt = row.CreatedAt,
            AppliedAt = row.AppliedAt,
        };
    }

    /// <summary>Null for an empty list, so the column says "there is no inverse" rather than "[]".</summary>
    private static string? Write(IReadOnlyList<GraphMutation> mutations) =>
        mutations.Count == 0 ? null : GraphChangeJson.Write(mutations);
}
