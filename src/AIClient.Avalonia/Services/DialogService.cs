using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace AIClient.Avalonia.Services;

/// <summary>
/// Avalonia storage-backed implementation of the dialogs. Resolves the window from the
/// application lifetime rather than holding one, because the service is a singleton and
/// the window is created after the container.
/// </summary>
public sealed class DialogService : IDialogService
{
    private Window? Window =>
        (global::Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;

    public async Task<string?> OpenFolderAsync(string title)
    {
        if (Window?.StorageProvider is not { } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<IReadOnlyList<string>> OpenFilesAsync(string title)
    {
        if (Window?.StorageProvider is not { } storage)
        {
            return [];
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
        });

        return files
            .Select(file => file.TryGetLocalPath())
            .OfType<string>()
            .ToList();
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK") =>
        Prompt.Show(title, message, confirmText, isError: false);

    public Task ShowErrorAsync(string title, string message, string? technicalDetails = null) =>
        Prompt.Show(title, technicalDetails is null ? message : $"{message}\n\n{technicalDetails}", "OK", isError: true);

    public bool CopyToClipboard(string text)
    {
        try
        {
            if (Window?.Clipboard is { } clipboard)
            {
                _ = clipboard.SetTextAsync(text);
                return true;
            }
        }
        catch (Exception)
        {
            // The clipboard is a shared resource that refuses at will; nothing here is worth a crash.
        }

        return false;
    }

    /// <summary>
    /// The one small modal the shell needs at this stage. A full dialog system - inline
    /// content dialogs, snackbars, queued questions - is the Phase 5 polish pass; this keeps
    /// pickers working without building that twice.
    /// </summary>
    private static class Prompt
    {
        public static async Task<bool> Show(string title, string message, string confirmText, bool isError)
        {
            var window = (global::Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow;

            if (window is null)
            {
                return false;
            }

            var dialog = new PromptWindow
            {
                Title = title,
                Message = message,
                ConfirmText = confirmText,
                IsError = isError,
            };

            return await dialog.ShowDialog<bool>(window);
        }
    }
}
