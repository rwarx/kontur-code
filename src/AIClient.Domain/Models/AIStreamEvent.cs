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
    /// A fragment of a tool call as it arrives. Emitted for progress only - the name shows up
    /// in the first fragment, so the UI can say which tool is being composed while the
    /// arguments are still streaming.
    /// </summary>
    /// <remarks>
    /// Nothing needs to reassemble these: the provider does it and emits <see cref="ToolCalls"/>
    /// once the stream is whole. A consumer that only wants to act on tool calls can ignore
    /// this case entirely, which is what <c>ChatService</c> does.
    /// </remarks>
    /// <param name="Index">Position in the call array. Fragments for one call share it.</param>
    /// <param name="Id">Present on the first fragment of a call, null afterwards.</param>
    /// <param name="Name">Present on the first fragment of a call, null afterwards.</param>
    /// <param name="ArgumentsFragment">A slice of the argument JSON, meaningless on its own.</param>
    public sealed record ToolCallDelta(int Index, string? Id, string? Name, string? ArgumentsFragment)
        : AIStreamEvent;

    /// <summary>
    /// Every tool call of the turn, reassembled, emitted once immediately before
    /// <see cref="Completed"/>. Absent when the model answered with text alone.
    /// </summary>
    /// <remarks>
    /// The agent loop keys off this rather than off <c>finish_reason == "tool_calls"</c>:
    /// providers disagree about the finish reason when a model both writes text and calls a
    /// tool, and some send <c>stop</c> with calls attached. Calls present means act.
    /// </remarks>
    public sealed record ToolCalls(IReadOnlyList<AIToolCall> Calls) : AIStreamEvent;

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
