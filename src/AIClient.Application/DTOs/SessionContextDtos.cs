namespace AIClient.Application.DTOs;

/// <summary>
/// Everything the context panel shows about one chat: what the model is holding, how much of
/// its window that fills, and what it cost.
/// </summary>
/// <remarks>
/// <para>
/// Assembled per open rather than stored. Every number here is derived - from the message rows,
/// from the model catalogue, or from the same estimator the prompt builder uses - so there is no
/// second copy of the truth to go stale, and a report is always about the model currently
/// selected rather than the one that happened to answer last.
/// </para>
/// <para>
/// The nullable fields are the ones a provider may simply not report. They stay nullable all the
/// way to the view so "not published" can be drawn as unknown instead of as zero, which is a
/// different and misleading claim.
/// </para>
/// </remarks>
public sealed record SessionContextReport
{
    public required Guid ConversationId { get; init; }
    public required string Title { get; init; }

    /// <summary>Display name of the provider, or null when the chat has no model chosen yet.</summary>
    public string? ProviderName { get; init; }

    /// <summary>Display name of the model, falling back to its native id when the catalogue is silent.</summary>
    public string? ModelName { get; init; }

    /// <summary>The model's window, or null when the catalogue does not publish one.</summary>
    public int? ContextWindow { get; init; }

    /// <summary>
    /// Prompt tokens billed on the last completed turn, cached ones included where the provider
    /// counts them that way.
    /// </summary>
    public int InputTokens { get; init; }

    public int OutputTokens { get; init; }

    /// <summary>The part of the output that was thinking. A subset of <see cref="OutputTokens"/>.</summary>
    public int ReasoningTokens { get; init; }

    public int CacheReadTokens { get; init; }
    public int CacheWriteTokens { get; init; }

    /// <summary>
    /// What the last turn actually put through the model: input + output + cache read + cache write.
    /// </summary>
    /// <remarks>
    /// Reasoning is deliberately absent from the sum. Providers report it as a breakdown of the
    /// completion, not as an extra charge, so adding it would double-count the thinking tokens.
    /// </remarks>
    public int TotalTokens { get; init; }

    /// <summary>
    /// <see cref="TotalTokens"/> as a share of the window, 0-100. Null when the window is unknown.
    /// </summary>
    public double? UsagePercent { get; init; }

    /// <summary>Messages in the transcript, the compaction stand-in excluded.</summary>
    public int MessageCount { get; init; }

    public int UserMessageCount { get; init; }
    public int AssistantMessageCount { get; init; }

    /// <summary>Tool results in the transcript. Zero for a chat that never ran the agent.</summary>
    public int ToolMessageCount { get; init; }

    /// <summary>
    /// Money spent across every turn of this chat, or null when the catalogue publishes no prices.
    /// </summary>
    /// <remarks>
    /// Null rather than zero: a free model and a model whose price nobody told us about are not
    /// the same fact, and showing "0,00 $" for the second one invents a guarantee.
    /// </remarks>
    public decimal? TotalCost { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastActivityAt { get; init; }

    /// <summary>
    /// How the estimated prompt divides up, for the stacked bar. Shares sum to 100 unless the
    /// chat is empty, in which case every share is zero.
    /// </summary>
    public required ContextBreakdown Breakdown { get; init; }

    /// <summary>
    /// Whether this chat is close enough to its window that compacting it would help.
    /// </summary>
    /// <remarks>
    /// Computed against the same threshold the automatic pass uses, so the panel's warning and
    /// the app's behaviour cannot disagree.
    /// </remarks>
    public bool ShouldCompact { get; init; }

    /// <summary>How many messages have already been folded into a summary.</summary>
    public int CompactedMessageCount { get; init; }
}

/// <summary>
/// The estimated prompt split by what is taking up the room.
/// </summary>
/// <remarks>
/// Estimated, not billed: the provider reports one number for the whole prompt, so the only way
/// to say which turns fill it is to measure them the way the prompt builder does. Counted with
/// the same estimator, which is what keeps this consistent with the trimming decisions.
/// </remarks>
public sealed record ContextBreakdown
{
    /// <summary>Tokens in user turns, attachment text included.</summary>
    public int UserTokens { get; init; }

    /// <summary>Tokens in assistant prose, tool-call arguments excluded.</summary>
    public int AssistantTokens { get; init; }

    /// <summary>Tool-call arguments and the results that answered them.</summary>
    public int ToolTokens { get; init; }

    /// <summary>The system prompt and anything else that is not a turn.</summary>
    public int OtherTokens { get; init; }

    public int Total => UserTokens + AssistantTokens + ToolTokens + OtherTokens;

    public double UserPercent => Share(UserTokens);
    public double AssistantPercent => Share(AssistantTokens);
    public double ToolPercent => Share(ToolTokens);
    public double OtherPercent => Share(OtherTokens);

    private double Share(int part) => Total == 0 ? 0 : part * 100.0 / Total;
}
