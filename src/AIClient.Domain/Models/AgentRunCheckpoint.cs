namespace AIClient.Domain.Models;

/// <summary>
/// A serializable snapshot of an agent run's state, saved at each step boundary.
/// Used for resumption after a crash or restart: the application replays the transcript
/// up to the checkpoint and continues from the next step.
/// </summary>
public sealed record AgentRunCheckpoint
{
    /// <summary>Stable identifier for this run, persisted across restarts.</summary>
    public Guid RunId { get; init; }

    /// <summary>The conversation this run belongs to.</summary>
    public Guid ConversationId { get; init; }

    /// <summary>The mode as a string ("Build", "Plan", "PlanCanvas") to avoid a Domain→Application dependency.</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>The provider and model used for this run.</summary>
    public string ProviderId { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;

    /// <summary>The step number the run has completed (1-based). The next step to execute is this + 1.</summary>
    public int CompletedStep { get; init; }

    /// <summary>The message id of the last completed step's assistant row.</summary>
    public Guid LastMessageId { get; init; }

    /// <summary>The current phase of the run as a string ("Idle", "Preparing", "Streaming", etc.).</summary>
    public string Phase { get; init; } = string.Empty;

    /// <summary>How many steps have been taken.</summary>
    public int TotalSteps { get; init; }

    /// <summary>Elapsed time in milliseconds (excluding approval pauses).</summary>
    public int ElapsedMs { get; init; }

    /// <summary>When this checkpoint was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Tool calls that have been executed in the current step (for mid-step resume).</summary>
    public IReadOnlyList<string> CompletedToolCallIds { get; init; } = [];

    /// <summary>Whether the run can be resumed (true unless it reached a terminal state).</summary>
    public bool CanResume => Phase is not ("Completed" or "Failed" or "Cancelled");
}
