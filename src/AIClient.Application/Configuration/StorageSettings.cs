namespace AIClient.Application.Configuration;

/// <summary>Where data lives and how much of it is allowed.</summary>
public sealed class StorageSettings
{
    /// <summary>
    /// Largest attachment accepted, in bytes. Files above this are rejected outright
    /// rather than truncated, so the user is never silently sent a partial file.
    /// </summary>
    public long MaxAttachmentBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Characters of an attachment inlined into the prompt. Text above this is truncated
    /// with an explicit marker so the model is told the file was cut.
    /// </summary>
    public int MaxAttachmentCharacters { get; set; } = 120_000;

    /// <summary>Copy attachments into the app's store instead of referencing the original path.</summary>
    public bool CopyAttachmentsToStore { get; set; } = true;

    /// <summary>Minimum log level as a <c>Microsoft.Extensions.Logging.LogLevel</c> name.</summary>
    public string MinimumLogLevel { get; set; } = "Information";

    /// <summary>Log files older than this are deleted at startup.</summary>
    public int LogRetentionDays { get; set; } = 7;
}
