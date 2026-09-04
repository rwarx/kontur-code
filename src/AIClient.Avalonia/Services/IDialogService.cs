using System.Threading.Tasks;

namespace AIClient.Avalonia.Services;

/// <summary>
/// File pickers, confirmations and the clipboard, behind an interface.
/// </summary>
/// <remarks>
/// Same seam as the WPF app's <c>IDialogService</c>, reshaped for Avalonia: the storage
/// APIs are async-only, so the folder and file pickers return tasks and the view models
/// that need a folder await them. Keeping the shape here rather than forcing sync wrappers
/// avoids the classic sync-over-async deadlock on the UI thread.
/// </remarks>
public interface IDialogService
{
    /// <summary>Shows a folder picker. Returns the chosen folder, or null when cancelled.</summary>
    Task<string?> OpenFolderAsync(string title);

    /// <summary>Shows an open-file dialog. Returns the chosen paths, empty when cancelled.</summary>
    Task<IReadOnlyList<string>> OpenFilesAsync(string title);

    /// <summary>Yes/no confirmation. True when the user confirms.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK");

    /// <summary>Informational dialog for a failure worth reading.</summary>
    Task ShowErrorAsync(string title, string message, string? technicalDetails = null);

    /// <summary>Copies text to the clipboard, swallowing the transient failures it is prone to.</summary>
    bool CopyToClipboard(string text);
}
