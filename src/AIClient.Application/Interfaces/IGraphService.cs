using AIClient.Domain.Graph;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Owns the graph's state, history and persistence - one graph at a time.
/// </summary>
/// <remarks>
/// <para>
/// The canvas, the outline view, the plan sink and the context builder all read snapshots
/// from here and all write through here, which is what keeps "AI proposes, the model
/// applies, the timeline remembers" one pipeline rather than four that drift apart.
/// </para>
/// <para>
/// Not thread-safe. Hosts marshal calls onto one thread (the UI thread), the same contract
/// the rest of the application's observable state follows; events are raised inline on
/// whatever thread completed the call, and it is the host that moves them where they are
/// wanted.
/// </para>
/// <para>
/// The service deliberately has no save policy of its own: applying, remembering and
/// offering undo are its job, while when to persist - after every apply, on a timer, on
/// close - is the host's, because only the host knows what the user would call "losing
/// their work".
/// </para>
/// </remarks>
public interface IGraphService
{
    /// <summary>The graph as it stands. Reading it is free and always consistent.</summary>
    GraphSnapshot Current { get; }

    /// <summary>
    /// Raised after every change to <see cref="Current"/>, carrying the new snapshot, on
    /// the thread that caused it; a version-only change still raises, because a view that
    /// is waiting to redraw cannot tell the difference.
    /// </summary>
    event EventHandler<GraphSnapshot>? SnapshotChanged;

    /// <summary>
    /// Applies a change set through the domain model and records what happened.
    /// </summary>
    /// <remarks>
    /// <see cref="GraphMutationResult.Rejected"/> describes any changes the model refused;
    /// a set where everything was refused is still recorded on the timeline as an attempt,
    /// because "the agent tried and failed" is history too.
    /// </remarks>
    Task<GraphMutationResult> ApplyAsync(GraphChangeSet changeSet, CancellationToken cancellationToken = default);

    /// <summary>
    /// The graph's history, newest first, bounded (100 entries; older entries are dropped,
    /// and the snapshots they describe are not kept either).
    /// </summary>
    /// <remarks>
    /// An entry is a description of a step, not the step itself: undo works from the
    /// service's own snapshot stacks, so a truncated timeline costs a scrollbar, not the
    /// ability to step back through the entries that remain.
    /// </remarks>
    IReadOnlyList<GraphTimelineEntry> Timeline { get; }

    /// <summary>Raised after the timeline gains or loses an entry, or is cleared by a load.</summary>
    event EventHandler? TimelineChanged;

    /// <summary>Whether there is anything to undo - drives the enabled state of a button, nothing more.</summary>
    bool CanUndo { get; }

    /// <summary>Whether there is anything to redo.</summary>
    bool CanRedo { get; }

    /// <summary>
    /// Restores the snapshot the last applied change set replaced, and re-enables redo.
    /// </summary>
    /// <remarks>
    /// Undo is not an inverted change set but a restore, because a change set that removes
    /// what an earlier one added says nothing about where a node the user moved in between
    /// should go back to.
    /// </remarks>
    Task<GraphMutationResult> UndoAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-applies the snapshot the last undo removed.</summary>
    Task<GraphMutationResult> RedoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the current snapshot under the given key; an empty graph saves as an empty
    /// file rather than being skipped (a cleared canvas is a state, not an absence).
    /// </summary>
    Task SaveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the graph with the snapshot stored under key, or an empty graph when
    /// nothing is stored. Returns what was loaded.
    /// </summary>
    /// <remarks>
    /// Loading starts the history afresh - undo does not cross a load, because the graph
    /// before the load is a different document rather than an earlier state of this one.
    /// </remarks>
    Task<GraphSnapshot> LoadAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// One step of the graph's history as the timeline shows it.
/// </summary>
/// <remarks>
/// A description of a step, deliberately not the step: it records what the graph looked
/// like afterwards (version, node and edge counts) and how the step went, which is
/// everything a timeline row can show, and none of what an undo needs.
/// </remarks>
public sealed record GraphTimelineEntry
{
    /// <summary>Identity of the entry, stable for the process's lifetime so views can key rows on it.</summary>
    public required Guid Id { get; init; }

    /// <summary>When the step happened, as the timeline shows it.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The change set's title, or "Undo"/"Redo" for the steps the user's keystrokes made.</summary>
    public required string Title { get; init; }

    /// <summary>What the step was for, when the title does not say it. Null when there was nothing more.</summary>
    public string? Description { get; init; }

    /// <summary>Who produced the step; see <see cref="GraphChangeOrigin"/>.</summary>
    public required GraphChangeOrigin Origin { get; init; }

    /// <summary>The version the graph reached: the number each step is written against.</summary>
    public required int ResultingVersion { get; init; }

    /// <summary>Nodes on the canvas after the step.</summary>
    public required int NodeCount { get; init; }

    /// <summary>Edges on the canvas after the step.</summary>
    public required int EdgeCount { get; init; }

    /// <summary>
    /// True when every change in the set was refused.
    /// </summary>
    /// <remarks>
    /// The timeline records the attempt, not just the successes: a row that says the
    /// indexer found nothing and a row that says the agent's change set was refused look
    /// the same in the node counts, and only this flag tells them apart.
    /// </remarks>
    public required bool WasRejected { get; init; }
}
