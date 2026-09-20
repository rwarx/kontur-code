using AIClient.Application.DTOs;
using AIClient.Domain.Enums;

namespace AIClient.Application.Services;

/// <summary>
/// What one stored message costs in the prompt, estimated the way <see cref="ContextBuilder"/>
/// counts it.
/// </summary>
/// <remarks>
/// <para>
/// Shared rather than reimplemented, because three features answer the same question and must
/// agree: the context panel draws a breakdown, compaction decides what is worth folding, and the
/// prompt builder decides what to trim. Three copies of this arithmetic would drift, and the
/// symptom would be a bar that disagrees with the app's own behaviour.
/// </para>
/// <para>
/// Estimated, never billed. No provider says which messages filled the window, so the only way to
/// attribute it is to measure the same text the builder sends. The attachment wrapper markup
/// (<c>&lt;file name="…"&gt;</c>) is left out as noise - a few tokens per file against the file's
/// own content.
/// </para>
/// </remarks>
public static class ContextCost
{
    /// <summary>Everything one message contributes: prose, attachment text and tool-call arguments.</summary>
    public static int OfMessage(MessageDto message) =>
        OfProse(message) + OfToolCalls(message);

    /// <summary>
    /// The message's own text, attachment content included, with one per-message overhead.
    /// </summary>
    /// <remarks>
    /// Attachments land on the turn that carried them rather than in a category of their own,
    /// because that is where the model sees them - inlined into the user's question.
    /// </remarks>
    public static int OfProse(MessageDto message)
    {
        var tokens = TokenEstimator.EstimateMessage(message.Content);

        foreach (var attachment in message.Attachments)
        {
            tokens += TokenEstimator.Estimate(attachment.TextContent);
        }

        return tokens;
    }

    /// <summary>
    /// The arguments of the tool calls an assistant message made. Zero on every other role.
    /// </summary>
    /// <remarks>
    /// Counted apart from the prose because it belongs with the tool traffic: a <c>write_file</c>
    /// call carrying three kilobytes of source is tool cost, not something the model said.
    /// </remarks>
    public static int OfToolCalls(MessageDto message)
    {
        if (message.Role != MessageRole.Assistant || message.ToolCallsJson is null)
        {
            return 0;
        }

        return AgentTranscript.Read(message.ToolCallsJson)
            .Sum(call => TokenEstimator.EstimateMessage(call.ArgumentsJson));
    }

    /// <summary>
    /// What the next request would carry: the system prompt plus every message still live.
    /// </summary>
    /// <remarks>
    /// Compacted rows are skipped, which is what makes the number drop after a fold. Failed rows
    /// are skipped too, matching <c>ContextBuilder.SelectHistory</c> - a turn the builder will not
    /// send is not part of what fills the window.
    /// </remarks>
    public static int OfHistory(IEnumerable<MessageDto> messages, string? systemPrompt)
    {
        var tokens = TokenEstimator.EstimateMessage(systemPrompt);

        foreach (var message in messages)
        {
            if (message.IsCompacted || message.Status == MessageStatus.Failed)
            {
                continue;
            }

            tokens += OfMessage(message);
        }

        return tokens;
    }

    /// <summary>
    /// The newest turn a provider actually billed, or null when nothing has completed yet.
    /// </summary>
    /// <remarks>
    /// One turn, not a sum. Prompt tokens already contain the whole history that was sent, so
    /// adding earlier turns counts the same history once per turn - which is how a 74k prompt ends
    /// up being reported as a million.
    /// </remarks>
    public static MessageDto? LastBilledTurn(IReadOnlyList<MessageDto> messages) =>
        messages
            .Where(m => m is { Role: MessageRole.Assistant, Status: MessageStatus.Complete })
            .Where(m => m.InputTokens is not null || m.OutputTokens is not null)
            .OrderByDescending(m => m.SequenceNumber)
            .FirstOrDefault();

    /// <summary>
    /// How full the model's window is, 0-100, or null when the window is unknown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The larger of two measurements, on purpose. What the provider billed on the last turn is
    /// authoritative and includes overheads the estimator cannot see - tool schemas, images - but
    /// says nothing about messages added since. The local estimate is current but blind to those
    /// overheads. Taking the larger errs towards compacting early, which costs one cheap request;
    /// erring late costs the turn.
    /// </para>
    /// <para>
    /// Shared by the panel's warning and by compaction's own gate, so the two cannot disagree about
    /// whether a chat is full. Reasoning is left out of the billed figure because providers report
    /// it as a slice of the completion rather than as an extra charge.
    /// </para>
    /// </remarks>
    public static double? Fullness(ConversationDetail conversation, int? window)
    {
        if (window is not { } limit || limit <= 0)
        {
            return null;
        }

        var estimated = OfHistory(conversation.Messages, conversation.SystemPrompt);
        var last = LastBilledTurn(conversation.Messages);

        var billed = last is null
            ? 0
            : (last.InputTokens ?? 0)
                + (last.OutputTokens ?? 0)
                + (last.CacheReadTokens ?? 0)
                + (last.CacheWriteTokens ?? 0);

        return Math.Max(estimated, billed) * 100.0 / limit;
    }
}
