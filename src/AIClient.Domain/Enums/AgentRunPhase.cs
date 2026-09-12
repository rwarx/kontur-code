namespace AIClient.Domain.Enums;

/// <summary>
/// The explicit states an agent run can be in. The state machine is linear with branching
/// at the tool-execution step, and every transition is recorded for tracing and resumption.
/// </summary>
public enum AgentRunPhase
{
    /// <summary>The run has not started yet.</summary>
    Idle,

    /// <summary>Resolving the provider, building context, assembling the request.</summary>
    Preparing,

    /// <summary>Streaming tokens from the provider for the current step.</summary>
    Streaming,

    /// <summary>Executing tool calls produced by the current step.</summary>
    ExecutingTools,

    /// <summary>Persisting the step result and preparing for the next step.</summary>
    Persisting,

    /// <summary>The run finished successfully.</summary>
    Completed,

    /// <summary>The run failed with an error.</summary>
    Failed,

    /// <summary>The user pressed Stop.</summary>
    Cancelled,
}
