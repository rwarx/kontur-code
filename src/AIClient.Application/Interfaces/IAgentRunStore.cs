using AIClient.Domain.Models;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Persists agent run checkpoints so that a run interrupted by a crash or restart
/// can be resumed from the last completed step.
/// </summary>
public interface IAgentRunStore
{
    /// <summary>Saves or updates a checkpoint for a run.</summary>
    Task SaveCheckpointAsync(AgentRunCheckpoint checkpoint, CancellationToken cancellationToken);

    /// <summary>Loads the most recent checkpoint for a run, or null if none exists.</summary>
    Task<AgentRunCheckpoint?> LoadCheckpointAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Loads all checkpoints that can be resumed (not in a terminal state).</summary>
    Task<IReadOnlyList<AgentRunCheckpoint>> GetResumableAsync(CancellationToken cancellationToken);

    /// <summary>Deletes a checkpoint (called when a run reaches a terminal state).</summary>
    Task DeleteCheckpointAsync(Guid runId, CancellationToken cancellationToken);
}
