using AIClient.Application.DTOs;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Conversation and message persistence. The only route the UI has to stored chats;
/// no ViewModel ever sees a DbContext.
/// </summary>
public interface IConversationService
{
    /// <summary>
    /// Sidebar list: metadata and a preview only, never message bodies. Paged so that
    /// opening the app with thousands of chats does not read the whole table.
    /// </summary>
    Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default);

    /// <summary>Full-text search over titles and message bodies.</summary>
    Task<IReadOnlyList<ConversationSummary>> SearchAsync(
        string query,
        int take = 50,
        CancellationToken cancellationToken = default);

    /// <summary>Loads one conversation with its messages and attachments. Null when it does not exist.</summary>
    Task<ConversationDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ConversationSummary> CreateAsync(
        string? title = null,
        string? providerId = null,
        string? modelId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Renames a chat and marks the title user-defined so auto-titling leaves it alone.</summary>
    Task RenameAsync(Guid id, string title, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task SetPinnedAsync(Guid id, bool isPinned, CancellationToken cancellationToken = default);

    /// <summary>Records which provider/model the chat should use for its next message.</summary>
    Task SetModelAsync(Guid id, string providerId, string modelId, CancellationToken cancellationToken = default);

    /// <summary>Appends a message and bumps the conversation's UpdatedAt.</summary>
    Task<MessageDto> AddMessageAsync(Guid conversationId, NewMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a message's content and status. Used to commit a streamed answer and
    /// to save an edited user message.
    /// </summary>
    Task UpdateMessageAsync(MessageUpdate update, CancellationToken cancellationToken = default);

    Task DeleteMessageAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a message and every message after it. Backs Regenerate and Edit, both of
    /// which must discard the turns that followed the point being changed.
    /// </summary>
    Task DeleteFromMessageAsync(Guid messageId, bool inclusive, CancellationToken cancellationToken = default);

    /// <summary>Applies an auto-generated title, unless the user has already named the chat.</summary>
    Task<string?> TryApplyAutoTitleAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a run of messages as folded into a summary, so the context builder stops sending
    /// them while the transcript keeps showing them.
    /// </summary>
    /// <returns>How many rows were actually changed.</returns>
    Task<int> MarkCompactedAsync(
        IReadOnlyCollection<Guid> messageIds,
        CancellationToken cancellationToken = default);

    /// <summary>Every project, ordered the way the sidebar draws them.</summary>
    Task<IReadOnlyList<ProjectSummary>> GetProjectsAsync(CancellationToken cancellationToken = default);

    Task<ProjectSummary> CreateProjectAsync(NewProject project, CancellationToken cancellationToken = default);

    /// <summary>Applies the non-null fields of <paramref name="update"/>. Unknown ids are ignored.</summary>
    Task UpdateProjectAsync(ProjectUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a project. Its conversations are unfiled rather than deleted - see
    /// <see cref="Domain.Entities.Conversation.ProjectId"/>.
    /// </summary>
    Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Files a chat under a project, or unfiles it when <paramref name="projectId"/> is null.</summary>
    Task SetProjectAsync(Guid conversationId, Guid? projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sidebar rows for one project, or for the unfiled chats when <paramref name="projectId"/>
    /// is null.
    /// </summary>
    Task<IReadOnlyList<ConversationSummary>> GetSummariesByProjectAsync(
        Guid? projectId,
        int take = 200,
        CancellationToken cancellationToken = default);
}
