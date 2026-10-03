using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Server;

/// <summary>
/// The agent's approval gate over HTTP: publishes the question on the run's registry
/// entry and waits for the renderer's answer, honouring run cancellation so that
/// Stop closes the question instead of wedging the run behind it.
/// </summary>
public sealed class HttpAgentApproval : IAgentApproval
{
    private readonly RunRegistry _runs;
    private readonly ILogger<HttpAgentApproval> _logger;

    /// <summary>Async-local run id, set by the streaming endpoints around the run.</summary>
    public static readonly AsyncLocal<Guid?> CurrentRunId = new();

    public HttpAgentApproval(RunRegistry runs, ILogger<HttpAgentApproval> logger)
    {
        _runs = runs;
        _logger = logger;
    }

    public Task<AgentApprovalDecision> RequestAsync(
        AgentApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        if (CurrentRunId.Value is not { } runId || !_runs.TryGet(runId, out var entry) || entry is null)
        {
            // No HTTP run owns this call (tests, another host): refuse, like the default gate.
            _logger.LogWarning("Approval requested outside a tracked run; denying {Tool}.", request.ToolName);
            return Task.FromResult(AgentApprovalDecision.Deny("No interactive session is attached to this run."));
        }

        var pending = new PendingApproval
        {
            ApprovalId = Guid.NewGuid(),
            RunId = runId,
            ConversationId = request.ConversationId,
            ToolName = request.ToolName,
            Risk = request.Risk.ToString(),
            ArgumentsJson = request.ArgumentsJson,
            Summary = request.Summary,
            Preview = request.Preview,
            IsRepeat = request.IsRepeat,
            AskedAt = DateTimeOffset.UtcNow,
        };

        return entry.WaitForApprovalAsync(pending, cancellationToken);
    }
}
