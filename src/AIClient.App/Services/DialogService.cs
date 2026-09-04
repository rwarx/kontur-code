using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace AIClient.App.Services;

/// <summary>Fluent-styled implementation of <see cref="IDialogService"/>.</summary>
public sealed class DialogService : IDialogService
{
    private readonly IContentDialogService _contentDialogService;
    private readonly ILogger<DialogService> _logger;

    public DialogService(IContentDialogService contentDialogService, ILogger<DialogService> logger)
    {
        _contentDialogService = contentDialogService;
        _logger = logger;
    }

    public IReadOnlyList<string> OpenFiles(string filter, bool allowMultiple = true)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter,
            Multiselect = allowMultiple,
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public string? SaveFile(string filter, string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = suggestedFileName,
            AddExtension = true,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? OpenFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,

            // Picking a folder that is not there yet cannot be what was meant: the folder the agent
            // works in has to contain something for it to work on.
            ValidateNames = true,
        };

        if (initialDirectory is { Length: > 0 } start && Directory.Exists(start))
        {
            dialog.InitialDirectory = start;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Delete")
    {
        var result = await _contentDialogService.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = title,
                Content = message,
                PrimaryButtonText = confirmText,
                CloseButtonText = "Cancel",
            },
            CancellationToken.None).ConfigureAwait(true);

        return result == ContentDialogResult.Primary;
    }

    public async Task ShowErrorAsync(string title, string message, string? technicalDetails = null)
    {
        // Technical details go in an expander rather than the message body: section 21 asks
        // for a human explanation with the diagnostic text available but not in the way.
        object content = technicalDetails is { Length: > 0 }
            ? BuildExpandableContent(message, technicalDetails)
            : message;

        await _contentDialogService.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = title,
                Content = content,
                CloseButtonText = "Close",
            },
            CancellationToken.None).ConfigureAwait(true);
    }

    public bool CopyToClipboard(string text)
    {
        // The Windows clipboard is a shared resource another process can hold open, so this
        // fails intermittently for reasons that have nothing to do with the app.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 2)
            {
                Thread.Sleep(40);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not copy to the clipboard.");
                return false;
            }
        }

        return false;
    }

    private static FrameworkElement BuildExpandableContent(string message, string technicalDetails)
    {
        var panel = new System.Windows.Controls.StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(new Wpf.Ui.Controls.CardExpander
        {
            Header = new TextBlock { Text = "Technical details" },
            Content = new System.Windows.Controls.TextBox
            {
                Text = technicalDetails,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MaxHeight = 220,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas"),
                FontSize = 12,
            },
        });

        return panel;
    }
}
