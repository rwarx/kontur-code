using AIClient.Application.DTOs;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Answers "what is the model actually holding right now" for one conversation.
/// </summary>
/// <remarks>
/// Separate from <see cref="IConversationService"/> because it is a different kind of question.
/// Conversation persistence reads and writes rows; this reads rows, the model catalogue and the
/// prompt estimator together and reports a derived view that is stored nowhere. Keeping it apart
/// also keeps the panel out of the hot path: nothing here runs during a turn.
/// </remarks>
public interface ISessionContextService
{
    /// <summary>
    /// Builds the context report for one chat. Null when the conversation does not exist.
    /// </summary>
    /// <param name="providerId">
    /// The provider currently selected in the UI, which may differ from the one that answered
    /// last. Null falls back to what the conversation recorded.
    /// </param>
    /// <param name="modelId">The model currently selected, on the same terms.</param>
    Task<SessionContextReport?> GetReportAsync(
        Guid conversationId,
        string? providerId = null,
        string? modelId = null,
        CancellationToken cancellationToken = default);
}
