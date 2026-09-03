using AIClient.Application.DTOs;
using AIClient.Domain.Enums;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Orchestrates a chat turn: build context, call the provider, stream the answer,
/// persist the result. This is the single entry point <c>ChatViewModel</c> uses;
/// the ViewModel never touches a provider, an HTTP client or the database.
/// </summary>
public interface IChatService
{
    /// <summary>
    /// Sends a user message and streams the reply.
    /// </summary>
    /// <remarks>
    /// The user message and a placeholder assistant message are persisted before the
    /// first token arrives, so a crash mid-stream still leaves a coherent transcript.
    /// The sequence always ends with a terminal event; transport failures are surfaced
    /// as <see cref="Domain.Models.AIStreamEvent.Error"/> rather than thrown, because a
    /// failure can arrive after usable text has already been shown.
    /// </remarks>
    IAsyncEnumerable<ChatTurnEvent> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Re-answers an existing assistant message. Everything from that message onward is
    /// discarded first, so the model sees exactly the history that preceded it.
    /// </summary>
    IAsyncEnumerable<ChatTurnEvent> RegenerateAsync(RegenerateRequest request, CancellationToken cancellationToken);
}

/// <summary>Input for <see cref="IChatService.SendMessageAsync"/>.</summary>
public sealed record SendMessageRequest
{
    public required Guid ConversationId { get; init; }
    public required string Content { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public IReadOnlyList<NewAttachment> Attachments { get; init; } = [];
}

/// <summary>Input for <see cref="IChatService.RegenerateAsync"/>.</summary>
public sealed record RegenerateRequest
{
    public required Guid ConversationId { get; init; }

    /// <summary>The assistant message being replaced.</summary>
    public required Guid AssistantMessageId { get; init; }

    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
}

/// <summary>
/// What the ViewModel observes during a turn. Distinct from
/// <see cref="Domain.Models.AIStreamEvent"/> because the UI also needs to learn the
/// database ids assigned to the new messages, which the provider knows nothing about.
/// </summary>
public abstract record ChatTurnEvent
{
    /// <summary>The user message has been persisted. Carries its assigned id.</summary>
    public sealed record UserMessageSaved(MessageDto Message) : ChatTurnEvent;

    /// <summary>A placeholder assistant message exists and is about to receive tokens.</summary>
    public sealed record AssistantMessageStarted(MessageDto Message) : ChatTurnEvent;

    /// <summary>A chunk of answer text. Delta only.</summary>
    public sealed record ContentDelta(Guid MessageId, string Text) : ChatTurnEvent;

    /// <summary>The turn finished and the final content has been persisted.</summary>
    public sealed record Completed(
        Guid MessageId,
        int? InputTokens,
        int? OutputTokens,
        int GenerationTimeMs) : ChatTurnEvent;

    /// <summary>
    /// The turn failed. Any text received before the failure is kept and persisted.
    /// </summary>
    public sealed record Failed(
        Guid MessageId,
        AIErrorKind Kind,
        string UserMessage,
        string? TechnicalDetails,
        bool IsRetryable) : ChatTurnEvent;

    /// <summary>The user pressed Stop. Partial text is kept and stays usable as context.</summary>
    public sealed record Cancelled(Guid MessageId) : ChatTurnEvent;

    /// <summary>An auto-generated title was applied, so the sidebar can update in place.</summary>
    public sealed record TitleGenerated(Guid ConversationId, string Title) : ChatTurnEvent;
}
