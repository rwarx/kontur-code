using AIClient.Application.DTOs;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;

namespace AIClient.Application.Interfaces;

/// <summary>
/// The knowledge graph: the one thing in this application that is true about the project.
/// </summary>
/// <remarks>
/// <para>
/// Read from memory, written to disk. <see cref="Current"/> is an immutable snapshot handed out
/// without a lock and without a query, because hovering a card, drawing a hundred edges and
/// building a context block all read the graph and none of them can afford a round trip to SQLite.
/// Writing is the opposite: durable, serialised, and logged, because a change to the project's model
/// that survives only until the next crash is worse than no model at all.
/// </para>
/// <para>
/// Every write is a <see cref="GraphChangeSet"/>. There is no method here that adds one node. That
/// is not ceremony - it is what makes undo, a model's proposals and the timeline the same mechanism
/// instead of three, and it is the boundary the canvas is not allowed to reach past.
/// </para>
/// </remarks>
public interface IGraphService
{
    /// <summary>
    /// The graph as it stands. Immutable, so a caller may hold it while someone else writes.
    /// </summary>
    /// <remarks>
    /// A reader that keeps this across an await is reading a consistent older graph rather than a
    /// torn newer one, which is the trade this type is built around.
    /// </remarks>
    GraphSnapshot Current { get; }

    /// <summary>False until <see cref="LoadAsync"/> has run, so the UI can tell empty from unread.</summary>
    bool IsLoaded { get; }

    /// <summary>
    /// Raised after a change has been persisted, off the UI thread.
    /// </summary>
    /// <remarks>
    /// Nothing below the App project touches a dispatcher, so a subscriber that updates the screen
    /// has to hop threads itself - which App already does in one place, and which is the reason this
    /// is a plain event rather than an observable.
    /// </remarks>
    event EventHandler<GraphChangedEventArgs>? Changed;

    /// <summary>Reads the whole graph from storage, replacing whatever was in memory.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Reloads from storage and announces it as a reload, so readers rebuild.</summary>
    Task ReloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a change set now: works out what it does, writes the rows, and announces it.
    /// </summary>
    /// <remarks>
    /// For a change a person made. Mutations that break a rule are refused individually and
    /// reported in <see cref="GraphApplyResult.Refused"/>; the rest still apply.
    /// </remarks>
    Task<GraphResult<GraphApplyResult>> ApplyAsync(
        GraphChangeSet change,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a change set without applying any of it.
    /// </summary>
    /// <remarks>
    /// How a model changes the project model: it writes a proposal, the canvas draws it as ghost
    /// cards and dotted edges, and nothing is true until a person presses Apply. The mutations are
    /// validated against the current graph first, so a suggestion that could not possibly apply is
    /// refused while the model is still in a position to try something else.
    /// </remarks>
    Task<GraphResult<GraphChangeSet>> ProposeAsync(
        GraphChangeSet change,
        CancellationToken cancellationToken = default);

    /// <summary>Applies a proposal the user accepted.</summary>
    Task<GraphResult<GraphApplyResult>> AcceptAsync(
        Guid changeId,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a proposal as turned down. Kept in the log rather than deleted.</summary>
    Task<GraphResult<GraphChangeSet>> DiscardAsync(
        Guid changeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Undoes an applied change by applying its recorded inverse.
    /// </summary>
    /// <remarks>
    /// The inverse was worked out when the change was applied, against the graph as it was then. A
    /// later change may have removed something the inverse expects, in which case the parts that no
    /// longer make sense are refused and reported rather than forced.
    /// </remarks>
    Task<GraphResult<GraphApplyResult>> RevertAsync(
        Guid changeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The change log, newest first.
    /// </summary>
    /// <param name="state">Only entries in this state, or null for all of them.</param>
    /// <param name="limit">How many to return, newest first.</param>
    Task<IReadOnlyList<GraphChangeSet>> HistoryAsync(
        GraphChangeState? state = null,
        int limit = 100,
        CancellationToken cancellationToken = default);
}
