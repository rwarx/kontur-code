using AIClient.Application.DTOs;

namespace AIClient.Server;

/// <summary>
/// Tracks in-flight chat/agent runs: their cancellation and their pending approvals.
/// </summary>
/// <remarks>
/// One entry per run id, created when a streaming request starts and removed when the
/// stream ends. Approvals arrive on a different HTTP request than the run itself, so
/// the run's wait has to live somewhere both requests can reach: here.
/// </remarks>
public sealed class RunRegistry
{
    /// <summary>
    /// How many runs may be in flight at once.
    /// </summary>
    /// <remarks>
    /// Each run holds a request open, a provider connection, and possibly a database write. There is
    /// no natural ceiling — the caller supplies the id, so it is not even bounded by the number of
    /// clients. Fifty is far above any real use and low enough that a runaway loop cannot exhaust the
    /// machine.
    /// </remarks>
    public const int MaxConcurrentRuns = 50;

    private readonly object _lock = new();
    private readonly Dictionary<Guid, RunEntry> _runs = new();

    /// <summary>
    /// Registers a run, or returns null when the id is already in flight or the ceiling is reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dictionary indexer was used here, and that silently overwrote a live entry when a caller
    /// re-posted an id it had chosen itself. The displaced run's <see cref="CancellationTokenSource"/>
    /// was never disposed, its approval waiter was never signalled — so anything awaiting one waited
    /// for an answer that could never arrive — and whichever request finished first disposed that
    /// token while its sibling was still registering a callback on it.
    /// </para>
    /// <para>
    /// Refusing the duplicate is the whole fix. A caller that chose the id may retry it, but it gets
    /// told no rather than silently orphaning the run already using it.
    /// </para>
    /// </remarks>
    public RunEntry? Start(Guid runId)
    {
        var entry = new RunEntry(runId);

        lock (_lock)
        {
            if (_runs.ContainsKey(runId))
            {
                entry.Dispose();
                return null;
            }

            if (_runs.Count >= MaxConcurrentRuns)
            {
                entry.Dispose();
                return null;
            }

            _runs[runId] = entry;
        }

        return entry;
    }

    public bool TryGet(Guid runId, out RunEntry? entry)
    {
        lock (_lock)
        {
            return _runs.TryGetValue(runId, out entry);
        }
    }

    public void Finish(Guid runId)
    {
        lock (_lock)
        {
            if (_runs.Remove(runId, out var entry))
            {
                entry.Dispose();
            }
        }
    }
}

/// <summary>Mutable state of one streaming run.</summary>
public sealed class RunEntry : IDisposable
{
    private TaskCompletionSource<AgentApprovalDecision>? _approvalWaiter;
    private PendingApproval? _pending;

    public RunEntry(Guid runId)
    {
        RunId = runId;
    }

    public Guid RunId { get; }

    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>The approval currently awaiting an answer, if any.</summary>
    public PendingApproval? PendingApproval
    {
        get
        {
            lock (this)
            {
                return _pending;
            }
        }
    }

    /// <summary>Called by the approval gate: publishes the question and waits.</summary>
    public async Task<AgentApprovalDecision> WaitForApprovalAsync(
        PendingApproval pending,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<AgentApprovalDecision> waiter;
        lock (this)
        {
            _pending = pending;
            waiter = new TaskCompletionSource<AgentApprovalDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _approvalWaiter = waiter;
        }

        using var registration = cancellationToken.Register(
            () => waiter.TrySetCanceled(cancellationToken));

        try
        {
            return await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (this)
            {
                _pending = null;
                _approvalWaiter = null;
            }
        }
    }

    /// <summary>Called by the approval-answer endpoint.</summary>
    public bool Answer(PendingApprovalAnswer answer)
    {
        lock (this)
        {
            if (_approvalWaiter is null || _pending is null || _pending.ApprovalId != answer.ApprovalId)
            {
                return false;
            }

            _approvalWaiter.TrySetResult(answer.Decision);
            return true;
        }
    }

    public void Cancel() => Cancellation.Cancel();

    public void Dispose() => Cancellation.Dispose();
}

/// <summary>One question the agent put to the user.</summary>
public sealed record PendingApproval
{
    public required Guid ApprovalId { get; init; }
    public required Guid RunId { get; init; }
    public required Guid ConversationId { get; init; }
    public required string ToolName { get; init; }
    public required string Risk { get; init; }
    public required string ArgumentsJson { get; init; }
    public string? Summary { get; init; }
    public string? Preview { get; init; }
    public bool IsRepeat { get; init; }
    public DateTimeOffset AskedAt { get; init; }
}

/// <summary>The user's answer to a pending approval.</summary>
public sealed record PendingApprovalAnswer
{
    public required Guid ApprovalId { get; init; }
    public required AgentApprovalDecision Decision { get; init; }
}
