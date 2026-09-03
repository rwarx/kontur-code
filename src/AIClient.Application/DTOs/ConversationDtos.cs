using AIClient.Domain.Enums;

namespace AIClient.Application.DTOs;

/// <summary>
/// Sidebar row. Carries no message bodies, so listing thousands of chats stays cheap.
/// </summary>
public sealed record ConversationSummary
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public bool IsPinned { get; init; }
    public int MessageCount { get; init; }

    /// <summary>First line of the most recent message, truncated for the sidebar.</summary>
    public string? Preview { get; init; }
}

/// <summary>A conversation with its full message list, loaded when a chat is opened.</summary>
public sealed record ConversationDetail
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public bool IsTitleUserDefined { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? SystemPrompt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public required IReadOnlyList<MessageDto> Messages { get; init; }
}

/// <summary>A persisted message, as the UI sees it.</summary>
public sealed record MessageDto
{
    public required Guid Id { get; init; }
    public required Guid ConversationId { get; init; }
    public required MessageRole Role { get; init; }
    public required string Content { get; init; }
    public MessageStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    public AIErrorKind? ErrorKind { get; init; }
    public int SequenceNumber { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? GenerationTimeMs { get; init; }
    public IReadOnlyList<AttachmentDto> Attachments { get; init; } = [];
}

/// <summary>An attachment as the UI sees it. <see cref="TextContent"/> is omitted in list views.</summary>
public sealed record AttachmentDto
{
    public required Guid Id { get; init; }
    public required string FileName { get; init; }
    public required string MimeType { get; init; }
    public long Size { get; init; }
    public bool IsTruncated { get; init; }
    public string? TextContent { get; init; }
}

/// <summary>Input for appending a message.</summary>
public sealed record NewMessage
{
    public required MessageRole Role { get; init; }
    public required string Content { get; init; }
    public MessageStatus Status { get; init; } = MessageStatus.Complete;
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public IReadOnlyList<NewAttachment> Attachments { get; init; } = [];
}

/// <summary>Input for attaching a file to a new message.</summary>
public sealed record NewAttachment
{
    public required string FileName { get; init; }
    public required string MimeType { get; init; }
    public long Size { get; init; }
    public string? TextContent { get; init; }
    public string? StoredPath { get; init; }
    public bool IsTruncated { get; init; }
}

/// <summary>
/// Partial update of a stored message. Null fields are left untouched, so committing a
/// finished stream does not have to restate everything the message already knows.
/// </summary>
public sealed record MessageUpdate
{
    public required Guid MessageId { get; init; }
    public string? Content { get; init; }
    public MessageStatus? Status { get; init; }
    public string? ErrorMessage { get; init; }
    public AIErrorKind? ErrorKind { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? GenerationTimeMs { get; init; }
}
