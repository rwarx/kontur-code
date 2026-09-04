using AIClient.Domain.Enums;

namespace AIClient.Domain.Entities;

/// <summary>
/// One entry in the graph's change log: what was proposed or done, and what it would take to undo.
/// </summary>
/// <remarks>
/// <para>
/// This table is three features at once. It is the undo stack, because
/// <see cref="InverseJson"/> is recorded at the moment the change is worked out rather than
/// re-derived later. It is the proposal queue, because a model's suggestion is stored here in
/// state <see cref="GraphChangeState.Proposed"/> and applies nothing until a person says so. And it
/// is the timeline, because every mutation of the graph passes through here in order, with who
/// caused it.
/// </para>
/// <para>
/// The mutations are JSON rather than a child table. They are written once, read back whole, and
/// never queried by their contents - the questions asked of this table are "what happened lately"
/// and "what is waiting", both answered by the columns beside the JSON.
/// </para>
/// </remarks>
public sealed class GraphChangeRow
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>One line a person can read in a list: "Indexed 412 files", "Added CacheService".</summary>
    public string Summary { get; set; } = string.Empty;

    public GraphOrigin Origin { get; set; } = GraphOrigin.User;

    public GraphChangeState State { get; set; } = GraphChangeState.Proposed;

    public Guid? SourceExecutionId { get; set; }

    /// <summary>The mutations as applied, in order, as a JSON array.</summary>
    public string MutationsJson { get; set; } = "[]";

    /// <summary>What to apply to get back, already in the order it must be applied.</summary>
    public string? InverseJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? AppliedAt { get; set; }
}
