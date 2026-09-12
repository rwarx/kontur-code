namespace AIClient.Domain.Models;

/// <summary>
/// A recorded transition of the agent run state machine. Each transition is emitted as a
/// domain event for tracing, debugging, and checkpoint reconstruction.
/// </summary>
public abstract record AgentRunTransition
{
    /// <summary>The run has been created and is about to start.</summary>
    public sealed record RunStarted(
        Guid RunId,
        Guid ConversationId,
        string Mode,
        string ProviderId,
        string ModelId,
        DateTimeOffset Timestamp) : AgentRunTransition;

    /// <summary>A step has begun: the state machine moved to Preparing.</summary>
    public sealed record StepPreparing(
        Guid RunId,
        int Step,
        DateTimeOffset Timestamp) : AgentRunTransition;

    /// <summary>The provider request was sent and streaming has begun.</summary>
    public sealed record StepStreaming(
        Guid RunId,
        int Step,
        DateTimeOffset Timestamp) : AgentRunTransition;

    /// <summary>The stream ended and tool calls are about to be executed.</summary>
    public sealed record StepToolsReceived(
        Guid RunId,
        int Step,
        int ToolCallCount,
        DateTimeOffset Timestamp) : AgentRunTransition;

    /// <summary>A tool call is about to be executed (after approval).</summary>
    public sealed record ToolExecutionStarted(
        Guid RunId,
        int Step,
        string ToolName,
        string CallId,
        DateTimeOffset Timestamp) : AgentRunTransition;

    /// <summary>A tool call has finished.</summary>
    public sealed record ToolExecutionFinished(
        Guid RunId,
        int Step,
        string ToolName,
        string CallId,
        bool Success,
        DateTimeOffset Timestamp) : AgentRunTransition;

    /// <summary>The step result has been persisted to the transcript.</summary>
    public sealed record StepPersisted(
        Guid RunId,
        int Step,
        Guid MessageId,
        DateTimeOffset Timestamp) : AgentRunTransition;

    /// <summary>The run has completed successfully.</summary>
    public sealed record RunCompleted(
        Guid RunId,
        int TotalSteps,
        string StopReason,
        int ElapsedMs,
        DateTimeOffset Timestamp) : AgentRunTransition;

    /// <summary>The run has failed.</summary>
    public sealed record RunFailed(
        Guid RunId,
        int Step,
        string ErrorKind,
        DateTimeOffset Timestamp) : AgentRunTransition;

    /// <summary>The user cancelled the run.</summary>
    public sealed record RunCancelled(
        Guid RunId,
        int Step,
        DateTimeOffset Timestamp) : AgentRunTransition;
}
