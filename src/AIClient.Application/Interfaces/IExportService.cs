using AIClient.Application.DTOs;

namespace AIClient.Application.Interfaces;

/// <summary>Formats a conversation for saving to disk.</summary>
public interface IExportService
{
    /// <summary>Renders a conversation. Pure string work - the caller owns the file I/O.</summary>
    string Export(ConversationDetail conversation, ExportFormat format);

    /// <summary>Default file name, with the title sanitised for the file system.</summary>
    string SuggestFileName(ConversationDetail conversation, ExportFormat format);

    /// <summary>Save-dialog filter for the given format.</summary>
    string GetFileDialogFilter(ExportFormat format);
}

public enum ExportFormat
{
    Markdown = 0,
    Json = 1,
    PlainText = 2,
}
