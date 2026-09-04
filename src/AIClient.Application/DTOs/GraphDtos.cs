using AIClient.Domain.Enums;
using AIClient.Domain.Graph;

namespace AIClient.Application.DTOs;

/// <summary>
/// The outcome of a graph operation: a value, or the reason there is none.
/// </summary>
/// <remarks>
/// The same shape as <see cref="WorkspaceResult{T}"/>, and deliberately not a shared generic. The
/// workspace contract is written, tested and used; unifying the two would mean editing it to gain
/// nothing but one fewer type. If a third of these appears, that is the moment to generalise.
///
/// A failure here is a whole operation that did not happen - no graph is open, the change set names
/// an entry that is not there. Individual mutations that were turned down are not failures: they
/// come back in <see cref="GraphApplyResult.Refused"/> while the rest of the batch applies.
/// </remarks>
public sealed record GraphResult<T>(bool Success, T? Value, string? Error)
    where T : class
{
    public static GraphResult<T> Ok(T value) => new(true, value, null);

    public static GraphResult<T> Fail(string error) => new(false, null, error);
}

/// <summary>
/// What just happened to the graph.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Applied"/> is what makes this worth raising as an event rather than just bumping a
/// version. A canvas holding two thousand cards can move the three that changed; handed only "the
/// graph is different now" it would have to rebuild all of them, and an indexing pass would turn
/// into two thousand rebuilds.
/// </para>
/// <para>
/// <see cref="Origin"/> is here so the UI can be quiet about its own work. A position the user just
/// dragged does not need a toast; a node an agent added does.
/// </para>
/// </remarks>
public sealed record GraphChangedEventArgs
{
    public required GraphSnapshot Snapshot { get; init; }

    /// <summary>The primitive operations that took effect, in order. Empty on a reload.</summary>
    public required IReadOnlyList<GraphMutation> Applied { get; init; }

    /// <summary>The change log entry this came from, or <see cref="Guid.Empty"/> on a reload.</summary>
    public Guid ChangeId { get; init; }

    public GraphOrigin Origin { get; init; }

    /// <summary>
    /// Where the change ended up. <see cref="GraphChangeState.Proposed"/> means nothing moved -
    /// a suggestion is waiting - and the canvas draws it as a ghost rather than as fact.
    /// </summary>
    public GraphChangeState State { get; init; } = GraphChangeState.Applied;

    /// <summary>
    /// True when the whole graph was replaced from storage, so <see cref="Applied"/> says nothing
    /// about the difference and every reader has to rebuild.
    /// </summary>
    public bool IsReload { get; init; }
}

/// <summary>How far an indexing pass has got, reported while it runs.</summary>
/// <remarks>
/// Walking a large repository takes seconds, and the status line has to say something during them
/// or the application looks stalled. Counts rather than a percentage: the total is not known until
/// the walk finishes, and an invented denominator that jumps backwards is worse than no bar.
/// </remarks>
public sealed record GraphIndexProgress
{
    public required int Nodes { get; init; }
    public required int Edges { get; init; }

    /// <summary>The folder being read, relative to the root, for the status line.</summary>
    public string? Path { get; init; }
}

/// <summary>What an indexing pass produced.</summary>
public sealed record GraphIndexReport
{
    /// <summary>Name of the folder that was indexed, without the path leading to it.</summary>
    public required string Root { get; init; }

    public required int Nodes { get; init; }
    public required int Edges { get; init; }

    /// <summary>Nodes from an earlier pass whose files are gone, marked missing rather than deleted.</summary>
    public int Missing { get; init; }

    /// <summary>
    /// True when the walk stopped at its cap, so the graph describes part of the folder.
    /// </summary>
    /// <remarks>
    /// Reported for the same reason a truncated listing is: a caller that cannot tell a complete
    /// index from a partial one will conclude the project does not contain a file that it does.
    /// </remarks>
    public bool IsTruncated { get; init; }

    /// <summary>Mutations the graph turned down, verbatim, so the UI can show why.</summary>
    public IReadOnlyList<string> Refused { get; init; } = [];
}
