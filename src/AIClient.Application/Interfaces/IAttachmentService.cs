using AIClient.Application.DTOs;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Reads files the user attaches. The MVP accepts text-like files only and inlines
/// their contents into the prompt.
/// </summary>
/// <remarks>
/// This is a security boundary: extension allow-list, size cap, and a binary-content
/// check that rejects files whose extension lies about what they contain. The
/// application never executes an attachment and never reads outside the chosen path.
/// </remarks>
public interface IAttachmentService
{
    /// <summary>Extensions accepted, each including the leading dot and lower-cased.</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>True when the extension is on the allow-list. Cheap pre-check for drag-and-drop.</summary>
    bool IsSupported(string filePath);

    /// <summary>
    /// Validates and reads a file. Returns a failed result rather than throwing, because
    /// "this file is too big" is an expected outcome the UI shows inline.
    /// </summary>
    Task<AttachmentResult> LoadAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Filter string for the Open File dialog, built from the allow-list.</summary>
    string BuildFileDialogFilter();
}

/// <summary>Outcome of reading an attachment.</summary>
/// <param name="Success">False when the file was rejected; <paramref name="ErrorMessage"/> says why.</param>
public sealed record AttachmentResult(
    bool Success,
    NewAttachment? Attachment,
    string? ErrorMessage)
{
    public static AttachmentResult Ok(NewAttachment attachment) => new(true, attachment, null);
    public static AttachmentResult Fail(string error) => new(false, null, error);
}
