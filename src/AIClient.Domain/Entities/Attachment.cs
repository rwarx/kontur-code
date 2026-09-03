namespace AIClient.Domain.Entities;

/// <summary>
/// A file attached to a user message. In the MVP only text-like files are supported and
/// their contents are inlined into the prompt; the entity already carries what a future
/// binary/image attachment would need.
/// </summary>
public sealed class Attachment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid MessageId { get; set; }

    /// <summary>File name only, never a full path - the path is not shown to the model.</summary>
    public required string FileName { get; set; }

    public required string MimeType { get; set; }

    /// <summary>Size in bytes of the original file on disk.</summary>
    public long Size { get; set; }

    /// <summary>
    /// Path inside the application's attachment store (not the user's original location).
    /// Files are copied in so the conversation stays readable if the original is moved.
    /// </summary>
    public string? StoredPath { get; set; }

    /// <summary>
    /// Extracted text for text-like files. Inlined into the prompt by the context builder.
    /// Null for binary attachments, which the MVP rejects.
    /// </summary>
    public string? TextContent { get; set; }

    /// <summary>True when the file was larger than the ingest limit and only a prefix was kept.</summary>
    public bool IsTruncated { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Message? Message { get; set; }
}
