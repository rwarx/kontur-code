using AIClient.Domain.Enums;

namespace AIClient.Domain.Models;

/// <summary>
/// One event in a streaming response. Modelled as a closed hierarchy rather than a
/// struct with a "kind" flag so that a future event type (tool call, reasoning trace,
/// image chunk) is an additive change and the compiler points at every switch to update.
/// </summary>
public abstract record AIStreamEvent
{
    /// <summary>A chunk of assistant text. The overwhelming majority of events.</summary>
    /// <param name="Text">Delta only - never the accumulated answer.</param>
    public sealed record ContentDelta(string Text) : AIStreamEvent;

    /// <summary>
    /// Reasoning/thinking text emitted by models that expose it. Kept separate so the UI
    /// can render or hide it independently of the answer.
    /// </summary>
    public sealed record ReasoningDelta(string Text) : AIStreamEvent;

    /// <summary>
    /// Token accounting. Providers send this at most once, usually in the final chunk;
    /// some never send it at all.
    /// </summary>
    public sealed record Usage(int? InputTokens, int? OutputTokens) : AIStreamEvent;

    /// <summary>
    /// The stream ended normally.
    /// </summary>
    /// <param name="FinishReason">Provider-native reason, e.g. <c>stop</c>, <c>length</c>, <c>content_filter</c>.</param>
    public sealed record Completed(string? FinishReason) : AIStreamEvent;

    /// <summary>
    /// The stream ended abnormally. Providers surface failures this way in addition to
    /// throwing, because an error can arrive mid-stream after usable text was produced.
    /// </summary>
    public sealed record Error(AIErrorKind Kind, string Message, string? TechnicalDetails) : AIStreamEvent;
}
