using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Server;

/// <summary>
/// Translates turn/run events into plain JSON-able shapes for the NDJSON stream.
/// </summary>
/// <remarks>
/// A switch rather than polymorphic serialization: the event hierarchies live in
/// Application and gain no serializer attributes for one host's benefit.
/// The wire keeps the prototype's <c>{type: delta|done|error}</c> contract for
/// plain chat text, and adds <c>{type: event, name, data}</c> for everything else.
/// </remarks>
public static class EventMapping
{
    public static object MapChat(ChatTurnEvent evt) => evt switch
    {
        ChatTurnEvent.UserMessageSaved(var m) => Event("user-saved", new { message = MapMessage(m) }),
        ChatTurnEvent.AssistantMessageStarted(var m) => Event("assistant-started", new { message = MapMessage(m) }),
        ChatTurnEvent.ContentDelta(var id, var text) => new { type = "delta", messageId = id, text },
        ChatTurnEvent.Completed(var id, var input, var output, var ms, var reasoning, var cacheR, var cacheW)
            => Event("completed", new
            {
                messageId = id,
                inputTokens = input,
                outputTokens = output,
                generationTimeMs = ms,
                reasoningTokens = reasoning,
                cacheReadTokens = cacheR,
                cacheWriteTokens = cacheW,
            }),
        ChatTurnEvent.Compacted(var conv, var folded, var saved)
            => Event("compacted", new { conversationId = conv, messagesFolded = folded, tokensSaved = saved }),
        ChatTurnEvent.Failed(var id, var kind, var user, var details, var retryable)
            => new { type = "error", messageId = id, kind = kind.ToString(), message = user, details, retryable },
        ChatTurnEvent.Cancelled(var id) => Event("cancelled", new { messageId = id }),
        ChatTurnEvent.TitleGenerated(var conv, var title)
            => Event("title", new { conversationId = conv, title }),
        _ => Event("unknown", new { }),
    };

    public static object MapAgent(AgentEvent evt) => evt switch
    {
        AgentEvent.UserMessageSaved(var m) => Event("user-saved", new { message = MapMessage(m) }),
        AgentEvent.TitleGenerated(var conv, var title) => Event("title", new { conversationId = conv, title }),
        AgentEvent.Compacted(var conv, var folded, var saved)
            => Event("compacted", new { conversationId = conv, messagesFolded = folded, tokensSaved = saved }),
        AgentEvent.StepStarted(var step, var m)
            => Event("step-started", new { step, message = MapMessage(m) }),
        AgentEvent.ContentDelta(var id, var text) => new { type = "delta", messageId = id, text },
        AgentEvent.ReasoningDelta(var id, var text)
            => Event("reasoning-delta", new { messageId = id, text }),
        AgentEvent.ToolCallProposed(var msgId, var call, var risk)
            => Event("tool-proposed", new { messageId = msgId, call = MapToolCall(call), risk = risk.ToString() }),
        AgentEvent.ToolCallStarted(var msgId, var call)
            => Event("tool-started", new { messageId = msgId, call = MapToolCall(call) }),
        AgentEvent.ToolCallFinished(var call, var outcome, var row, var summary, var detail)
            => Event("tool-finished", new
            {
                call = MapToolCall(call),
                outcome = outcome.ToString(),
                message = MapMessage(row),
                summary,
                detail,
            }),
        AgentEvent.StepCompleted(var step, var id, var input, var output, var calledTools)
            => Event("step-completed", new
            {
                step,
                messageId = id,
                inputTokens = input,
                outputTokens = output,
                calledTools,
            }),
        AgentEvent.Completed(var id, var steps, var reason, var ms)
            => Event("run-completed", new
            {
                messageId = id,
                steps,
                reason = reason.ToString(),
                elapsedMs = ms,
            }),
        AgentEvent.Failed(var id, var kind, var user, var details, var retryable)
            => new { type = "error", messageId = id, kind = kind.ToString(), message = user, details, retryable },
        AgentEvent.Cancelled(var id, var steps) => Event("cancelled", new { messageId = id, steps }),
        _ => Event("unknown", new { }),
    };

    private static object Event(string name, object data) => new { type = "event", name, data };

    public static object MapMessage(MessageDto m) => new
    {
        id = m.Id,
        conversationId = m.ConversationId,
        role = m.Role.ToString(),
        content = m.Content,
        status = m.Status.ToString(),
        errorMessage = m.ErrorMessage,
        errorKind = m.ErrorKind?.ToString(),
        sequenceNumber = m.SequenceNumber,
        createdAt = m.CreatedAt,
        providerId = m.ProviderId,
        modelId = m.ModelId,
        inputTokens = m.InputTokens,
        outputTokens = m.OutputTokens,
        reasoningTokens = m.ReasoningTokens,
        cacheReadTokens = m.CacheReadTokens,
        cacheWriteTokens = m.CacheWriteTokens,
        generationTimeMs = m.GenerationTimeMs,
        attachments = m.Attachments.Select(a => new
        {
            id = a.Id,
            fileName = a.FileName,
            mimeType = a.MimeType,
            size = a.Size,
            isTruncated = a.IsTruncated,
            textContent = a.TextContent,
        }),
        isContextSummary = m.IsContextSummary,
        isCompacted = m.IsCompacted,
        toolCallsJson = m.ToolCallsJson,
        toolCallId = m.ToolCallId,
        toolName = m.ToolName,
        toolSucceeded = m.ToolSucceeded,
    };

    public static object MapToolCall(Domain.Models.AIToolCall call) => new
    {
        id = call.Id,
        name = call.Name,
        argumentsJson = call.ArgumentsJson,
    };
}
