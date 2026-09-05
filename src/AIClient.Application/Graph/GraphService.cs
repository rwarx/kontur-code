using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;

namespace AIClient.Application.Graph;

/// <summary>
/// The graph's owner: state, history and persistence for the one graph the application has.
/// </summary>
/// <remarks>
/// <para>
/// Everything the interface promises is here in one place: a <see cref="GraphModel"/> to
/// mutate, snapshot stacks to undo and redo through, a bounded timeline to describe what
/// happened, and a store to survive restarts. The model itself knows none of that - it is
/// a pure mutator - which is what keeps this class about policy and nothing else.
/// </para>
/// <para>
/// History is snapshots, not inverse change sets. An inverse would have to say where a
/// node goes back to when the user moved it after it was added, and a snapshot simply
/// already knows. The cost is memory - one graph per step - which the bound of 100 turns
/// into a constant, and which is cheap next to the cost of getting undo subtly wrong.
/// </para>
/// <para>
/// Events are raised inline, in the same call that changed something, and on whatever
/// thread that call ran on; the host marshals them (the UI thread in this application),
/// the same contract the rest of the observable services follow.
/// </para>
/// <para>
/// Saving is the host's decision, not this class's: <see cref="SaveAsync"/> writes when
/// asked and never on its own, because "when is a canvas safe to lose" is a question about
/// the user, not about the graph.
/// </para>
/// </remarks>
public sealed class GraphService : IGraphService
{
    /// <summary>
    /// How many snapshots the undo and redo stacks each keep, and how many entries the
    /// timeline shows.
    /// </summary>
    /// <remarks>
    /// One number for all three so they visibly describe the same bargain: a hundred steps
    /// of history is roughly a working session, and beyond it the oldest is dropped rather
    /// than the memory growing without end.
    /// </remarks>
    private const int HistoryLimit = 100;

    private readonly IGraphStore _store;
    private readonly GraphModel _model = new();

    /// <summary>Oldest first, newest last: the snapshots each applied change set replaced.</summary>
    private readonly List<GraphSnapshot> _undo = [];

    /// <summary>Oldest first, newest last: the snapshots each undo replaced.</summary>
    private readonly List<GraphSnapshot> _redo = [];

    /// <summary>Newest first, oldest dropped past the limit. The same list instance is handed out.</summary>
    private readonly List<GraphTimelineEntry> _timeline = [];

    public GraphService(IGraphStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>The graph as it stands; the model rebuilds it after every applied change set.</summary>
    public GraphSnapshot Current => _model.Snapshot;

    /// <summary>Raised after every change to <see cref="Current"/>, carrying the new snapshot.</summary>
    public event EventHandler<GraphSnapshot>? SnapshotChanged;

    /// <summary>Raised after the timeline changes: an entry added, or history cleared by a load.</summary>
    public event EventHandler? TimelineChanged;

    /// <summary>The graph's history, newest first, bounded. See <see cref="HistoryLimit"/>.</summary>
    public IReadOnlyList<GraphTimelineEntry> Timeline => _timeline;

    /// <summary>Whether an <see cref="UndoAsync"/> call would do anything.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether a <see cref="RedoAsync"/> call would do anything.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Applies a change set and, when something landed, remembers the snapshot it replaced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The model's part is synchronous and CPU-trivial, so the cancellation token is only
    /// honoured where there is something to cancel: the store calls. Everything else - a
    /// set that lands in microseconds - is not worth an interruption half-applied.
    /// </para>
    /// <para>
    /// A set where nothing landed is still recorded on the timeline, marked refused: the
    /// timeline shows attempts, not just successes. Such a set touches neither undo nor
    /// redo - there is no state to step back to - and raises
    /// <see cref="TimelineChanged"/> alone, since <see cref="Current"/> did not change.
    /// </para>
    /// </remarks>
    public Task<GraphMutationResult> ApplyAsync(GraphChangeSet changeSet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        cancellationToken.ThrowIfCancellationRequested();

        var before = Current;
        var result = _model.Apply(changeSet);

        if (result.Applied.Count > 0)
        {
            Remember(_undo, before);
            _redo.Clear();
        }

        Record(changeSet, result);

        if (result.Applied.Count > 0)
        {
            SnapshotChanged?.Invoke(this, result.Snapshot);
        }

        TimelineChanged?.Invoke(this, EventArgs.Empty);

        return Task.FromResult(result);
    }

    /// <summary>
    /// Restores the snapshot the last applied change set replaced.
    /// </summary>
    /// <remarks>
    /// The snapshot being undone is pushed onto the redo stack, so a redo re-applies
    /// exactly what the undo removed rather than guessing from the timeline. Undoing with
    /// nothing to undo is a refusal in the result, not an exception: the caller is usually
    /// a keystroke, and the honest answer to pressing Ctrl+Z at the bottom of history is
    /// "nothing to undo", shown quietly.
    /// </remarks>
    public Task<GraphMutationResult> UndoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_undo.Count == 0)
        {
            return Task.FromResult(Refuse("Nothing to undo."));
        }

        var target = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        var current = Current;
        _model.Restore(target);
        Remember(_redo, current);

        RecordStep("Undo", $"Restored version {target.Version}: {target.Nodes.Count} nodes, {target.Edges.Count} edges.",
            GraphChangeOrigin.Undo, target);

        SnapshotChanged?.Invoke(this, Current);
        TimelineChanged?.Invoke(this, EventArgs.Empty);

        return Task.FromResult(new GraphMutationResult
        {
            Snapshot = Current,
            Applied = [],
            Rejected = [],
        });
    }

    /// <summary>
    /// Re-applies the snapshot the last undo removed.
    /// </summary>
    /// <remarks>The mirror of <see cref="UndoAsync"/>: the snapshot being redone is pushed back onto the undo stack, so the next undo lands where the user expects.</remarks>
    public Task<GraphMutationResult> RedoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_redo.Count == 0)
        {
            return Task.FromResult(Refuse("Nothing to redo."));
        }

        var target = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        var current = Current;
        _model.Restore(target);
        Remember(_undo, current);

        RecordStep("Redo", $"Restored version {target.Version}: {target.Nodes.Count} nodes, {target.Edges.Count} edges.",
            GraphChangeOrigin.Redo, target);

        SnapshotChanged?.Invoke(this, Current);
        TimelineChanged?.Invoke(this, EventArgs.Empty);

        return Task.FromResult(new GraphMutationResult
        {
            Snapshot = Current,
            Applied = [],
            Rejected = [],
        });
    }

    /// <summary>
    /// Writes the current snapshot to the store, whole.
    /// </summary>
    /// <remarks>
    /// No state changes and no events: the graph is what it was, and saving it is not
    /// something the timeline needs to show. An empty graph is saved as an empty document,
    /// because "the user cleared the canvas and closed the app" should not come back as
    /// "the last state before that".
    /// </remarks>
    public Task SaveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return _store.SaveAsync(key, Current, cancellationToken);
    }

    /// <summary>
    /// Replaces the graph with what the store holds under the key, and starts the history
    /// again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing stored, or a document the store cannot read, both load as the empty graph:
    /// a fresh canvas beats a crash, and the workspace indexer can rebuild the rest.
    /// </para>
    /// <para>
    /// Loading clears the timeline and both stacks. Undo does not cross a load - the graph
    /// before the load is a different document, not an earlier state of this one - and a
    /// timeline that mixed the two would describe steps that never happened together.
    /// </para>
    /// </remarks>
    public async Task<GraphSnapshot> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var snapshot = await _store.LoadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? GraphSnapshot.Empty;

        _model.Restore(snapshot);
        _undo.Clear();
        _redo.Clear();
        _timeline.Clear();

        SnapshotChanged?.Invoke(this, snapshot);
        TimelineChanged?.Invoke(this, EventArgs.Empty);

        return snapshot;
    }

    /// <summary>
    /// Adds an entry for a change set the model has already judged.
    /// </summary>
    /// <remarks>
    /// Newest first, so the timeline the interface hands out needs no sorting, and bounded,
    /// so a long session forgets its oldest steps rather than growing without end.
    /// </remarks>
    private void Record(GraphChangeSet changeSet, GraphMutationResult result)
    {
        _timeline.Insert(0, new GraphTimelineEntry
        {
            Id = Guid.NewGuid(),
            At = DateTimeOffset.UtcNow,
            Title = changeSet.Title,
            Description = changeSet.Description,
            Origin = changeSet.Origin,
            ResultingVersion = result.Snapshot.Version,
            NodeCount = result.Snapshot.Nodes.Count,
            EdgeCount = result.Snapshot.Edges.Count,
            WasRejected = result.Applied.Count == 0,
        });

        TrimTimeline();
    }

    /// <summary>
    /// Adds an entry for a step the service itself took (an undo or a redo).
    /// </summary>
    private void RecordStep(string title, string description, GraphChangeOrigin origin, GraphSnapshot target)
    {
        _timeline.Insert(0, new GraphTimelineEntry
        {
            Id = Guid.NewGuid(),
            At = DateTimeOffset.UtcNow,
            Title = title,
            Description = description,
            Origin = origin,
            ResultingVersion = target.Version,
            NodeCount = target.Nodes.Count,
            EdgeCount = target.Edges.Count,
            WasRejected = false,
        });

        TrimTimeline();
    }

    /// <summary>Keeps the timeline at <see cref="HistoryLimit"/> entries, dropping the oldest.</summary>
    private void TrimTimeline()
    {
        if (_timeline.Count > HistoryLimit)
        {
            _timeline.RemoveRange(HistoryLimit, _timeline.Count - HistoryLimit);
        }
    }

    /// <summary>
    /// Pushes a snapshot onto one of the stacks, dropping the oldest past the limit.
    /// </summary>
    /// <remarks>
    /// The undo stack is the memory the history promise is backed by, so its bound is the
    /// honest one: past a hundred steps, the oldest past really is gone, and
    /// <see cref="CanUndo"/> says so by going false rather than by throwing.
    /// </remarks>
    private static void Remember(List<GraphSnapshot> stack, GraphSnapshot snapshot)
    {
        stack.Add(snapshot);

        if (stack.Count > HistoryLimit)
        {
            stack.RemoveAt(0);
        }
    }

    /// <summary>The shape of a refused undo or redo: nothing applied, one plain reason.</summary>
    private GraphMutationResult Refuse(string reason) => new()
    {
        Snapshot = Current,
        Applied = [],
        Rejected = [reason],
    };
}
