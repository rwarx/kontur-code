namespace AIClient.App.Services;

/// <summary>
/// File pickers, confirmations and the clipboard, behind an interface.
/// </summary>
/// <remarks>
/// These are the calls that make a ViewModel untestable if invoked directly: a
/// <c>MessageBox.Show</c> in a command handler blocks any test that reaches it. Routing
/// them through a service keeps the ViewModels headless.
/// </remarks>
public interface IDialogService
{
    /// <summary>Shows an open-file dialog. Returns the chosen paths, empty when cancelled.</summary>
    IReadOnlyList<string> OpenFiles(string filter, bool allowMultiple = true);

    /// <summary>Shows a save-file dialog. Returns the chosen path, or null when cancelled.</summary>
    string? SaveFile(string filter, string suggestedFileName);

    /// <summary>Yes/no confirmation. True when the user confirms.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Delete");

    /// <summary>Informational dialog with an optional collapsed technical section.</summary>
    Task ShowErrorAsync(string title, string message, string? technicalDetails = null);

    /// <summary>Copies text to the clipboard, swallowing the transient failures it is prone to.</summary>
    bool CopyToClipboard(string text);
}
