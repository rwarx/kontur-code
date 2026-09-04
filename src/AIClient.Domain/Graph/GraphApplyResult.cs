namespace AIClient.Domain.Graph;

/// <summary>
/// The outcome of applying a change set: the graph that resulted, what it took, and what it cost.
/// </summary>
/// <remarks>
/// <see cref="Applied"/> is the reason this type exists. Whatever writes rows does not re-derive
/// anything and does not repeat a single rule: it walks this list, which is already validated,
/// already reduced to primitive operations, and already expanded where one mutation implied several.
/// Snapshot and storage therefore move together or not at all.
/// </remarks>
public sealed record GraphApplyResult
{
    public required GraphSnapshot Snapshot { get; init; }

    /// <summary>What actually took effect, in the order it took effect.</summary>
    public required IReadOnlyList<GraphMutation> Applied { get; init; }

    /// <summary>What to apply to get back. Already in the order it must be applied.</summary>
    public required IReadOnlyList<GraphMutation> Inverse { get; init; }

    /// <summary>
    /// What was turned down, in words.
    /// </summary>
    /// <remarks>
    /// Text rather than exceptions, following the workspace sandbox: half of what proposes a change
    /// here is a model, and a model can read "that key already belongs to another node" and try
    /// something else. It cannot read a stack trace. A refusal is also not a failure of the batch -
    /// the rest of the mutations still apply, because one bad suggestion in twenty should not throw
    /// away the nineteen good ones.
    /// </remarks>
    public IReadOnlyList<string> Refused { get; init; } = [];

    public bool Changed => Applied.Count > 0;
}
