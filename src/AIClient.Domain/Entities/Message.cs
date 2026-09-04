using AIClient.Domain.Enums;

namespace AIClient.Domain.Entities;

/// <summary>
/// A single turn in a <see cref="Conversation"/>.
/// </summary>
public sealed class Message
{
    /// <summary>UUIDv7 so that insertion order and key order agree, which keeps the index dense.</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ConversationId { get; set; }

    public MessageRole Role { get; set; }

    /// <summary>The text as typed or as streamed. Markdown for assistant messages.</summary>
    public string Content { get; set; } = string.Empty;

    public MessageStatus Status { get; set; } = MessageStatus.Complete;

    /// <summary>
    /// Human-readable failure reason when <see cref="Status"/> is
    /// <see cref="MessageStatus.Failed"/>. Never contains credentials.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Stable error classification, letting the UI offer Retry only where it helps.</summary>
    public AIErrorKind? ErrorKind { get; set; }

    /// <summary>Explicit ordinal. Guards sibling ordering when timestamps collide during fast streaming.</summary>
    public int SequenceNumber { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Which provider produced an assistant message. Null for user messages.</summary>
    public string? ProviderId { get; set; }

    /// <summary>Which model produced an assistant message, recorded even if the chat later switches models.</summary>
    public string? ModelId { get; set; }

    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }

    /// <summary>Wall-clock generation time, used for the "12.4 s" hint under an answer.</summary>
    public int? GenerationTimeMs { get; set; }

    /// <summary>Reserved for provider-specific extras (finish reason, native ids). JSON or null.</summary>
    public string? MetadataJson { get; set; }

    /// <summary>
    /// On an assistant message, the tool calls it decided to make, as JSON. Null everywhere else,
    /// including on an assistant message that only wrote text.
    /// </summary>
    /// <remarks>
    /// Stored rather than recomputed because it cannot be recomputed: the call ids are issued by
    /// the provider, and they are the only thing tying an answer back to the question. Without
    /// them a reopened conversation could not be continued - the history would contain results
    /// addressed to calls that no longer exist.
    /// </remarks>
    public string? ToolCallsJson { get; set; }

    /// <summary>
    /// On a <see cref="MessageRole.Tool"/> message, the id of the call it answers.
    /// </summary>
    public string? ToolCallId { get; set; }

    /// <summary>
    /// On a <see cref="MessageRole.Tool"/> message, the tool that produced the content.
    /// </summary>
    public string? ToolName { get; set; }

    /// <summary>
    /// On a <see cref="MessageRole.Tool"/> message, whether the tool did what was asked.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Status"/> on purpose. A refused tool call is a complete message -
    /// the refusal is the content, and the model needs it in the history to correct itself -
    /// whereas <see cref="MessageStatus.Failed"/> means the turn broke and its content is not
    /// worth sending. Conflating the two would drop every refusal out of the context and let the
    /// model repeat the same mistake indefinitely.
    /// </remarks>
    public bool? ToolSucceeded { get; set; }

    public Conversation? Conversation { get; set; }

    public ICollection<Attachment> Attachments { get; set; } = [];
}
