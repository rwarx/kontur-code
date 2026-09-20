using AIClient.Application.DTOs;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Folds the older half of a conversation into a summary so a long chat can keep going past
/// the model's window.
/// </summary>
/// <remarks>
/// <para>
/// This exists because trimming alone is lossy in a way nobody can see. The context builder
/// drops the oldest turns when the budget runs out, which keeps requests legal but silently
/// throws away the decisions made early in a chat. Compaction replaces those turns with a
/// summary written by the model itself: the wording is lost, the substance is not, and the
/// transcript on screen still shows every original message.
/// </para>
/// <para>
/// Nothing is deleted. The folded messages stay on disk with
/// <see cref="Domain.Entities.Message.IsCompacted"/> set, so the record remains complete and a
/// future feature could unfold them again.
/// </para>
/// </remarks>
public interface ICompactionService
{
    /// <summary>
    /// Whether this chat is far enough through its window that compacting it is worthwhile.
    /// </summary>
    /// <remarks>
    /// Asked before every automatic pass and by the context panel, so the warning the user sees
    /// and the decision the app makes come from the same code.
    /// </remarks>
    Task<bool> ShouldCompactAsync(
        Guid conversationId,
        string? providerId,
        string? modelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Summarises the older history of one conversation.
    /// </summary>
    /// <remarks>
    /// Safe to call on a chat that does not need it: the result reports zero folded messages and
    /// nothing is written. The provider and model are the ones the summary is asked of, which is
    /// deliberately the same pair the chat is using - a summary written by a different model would
    /// answer in a different voice.
    /// </remarks>
    Task<CompactionResult> CompactAsync(
        CompactionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Input for <see cref="ICompactionService.CompactAsync"/>.</summary>
public sealed record CompactionRequest
{
    public required Guid ConversationId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }

    /// <summary>
    /// Messages at the end of the chat to leave untouched. Null takes the configured default.
    /// </summary>
    public int? KeepRecentMessages { get; init; }

    /// <summary>
    /// True when the user asked for this explicitly, which bypasses the fullness threshold.
    /// </summary>
    /// <remarks>
    /// A manual request is a decision, not a heuristic. Refusing it because the window is only
    /// half full would leave the button doing nothing with no explanation.
    /// </remarks>
    public bool Force { get; init; }
}

/// <summary>What one compaction pass achieved.</summary>
public sealed record CompactionResult
{
    /// <summary>How many messages were folded into the summary. Zero means nothing was done.</summary>
    public required int MessagesFolded { get; init; }

    /// <summary>Estimated prompt tokens the fold removed, summary included in the arithmetic.</summary>
    public required int TokensSaved { get; init; }

    /// <summary>The summary message that now stands in for them, when one was written.</summary>
    public MessageDto? Summary { get; init; }

    /// <summary>Why nothing happened, for the log and for a disabled button's tooltip.</summary>
    public string? SkippedReason { get; init; }

    public static CompactionResult Skipped(string reason) =>
        new() { MessagesFolded = 0, TokensSaved = 0, SkippedReason = reason };
}
