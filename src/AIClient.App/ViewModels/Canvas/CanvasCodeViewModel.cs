using System.Text;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels.Canvas;

/// <summary>
/// The code behind a node, read on demand and shown beside the canvas.
/// </summary>
/// <remarks>
/// <para>
/// A node points at a file; it never holds its text. This panel is the one place that follows that
/// pointer, and it follows it through <see cref="IWorkspaceService"/> like everything else in the
/// application that touches the disk - so the sandbox's fence, its protected names and its size cap
/// all apply here without this class having to know what any of them are.
/// </para>
/// <para>
/// Read-only, and not a step towards an editor. It exists so that clicking a card can answer "what
/// is actually in this file" without leaving the canvas. Writing is the agent's path, through
/// approval, and a text box in here that saved would quietly become a second one.
/// </para>
/// </remarks>
public sealed partial class CanvasCodeViewModel : ObservableObject
{
    /// <summary>
    /// How much of a file is read at once.
    /// </summary>
    /// <remarks>
    /// Enough for almost any source file in one go, and small enough that a generated file of four
    /// hundred thousand lines cannot be poured into a text box and freeze the window.
    /// </remarks>
    private const int MaxLines = 1200;

    /// <summary>Lines kept above a node that names a line span, so a method is not flush with the top.</summary>
    private const int ContextLines = 4;

    private readonly IWorkspaceService _workspace;
    private readonly ILogger<CanvasCodeViewModel> _logger;

    /// <summary>The read in flight, cancelled when another node is asked for or the panel closes.</summary>
    private CancellationTokenSource? _load;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>The path as the graph keeps it - relative to the project folder, never absolute.</summary>
    [ObservableProperty]
    private string _path = string.Empty;

    /// <summary>"Lines 1-1200 of 3011" - what is not on screen, said out loud rather than implied.</summary>
    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>The line numbers, as one string standing in its own column beside the text.</summary>
    [ObservableProperty]
    private string _gutter = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>The sandbox's own refusal, or one sentence of ours. Shown in place of the text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public CanvasCodeViewModel(IWorkspaceService workspace, ILogger<CanvasCodeViewModel> logger)
    {
        _workspace = workspace;
        _logger = logger;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    /// <summary>
    /// Opens the panel on the file behind a node.
    /// </summary>
    /// <remarks>
    /// The panel opens before the read finishes, already showing the title and a loading line. A
    /// file on a cold disk takes a moment, and a click that appears to do nothing reads as a click
    /// that did not work.
    /// </remarks>
    public async Task ShowAsync(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.Source is not { } source || source.IsRoot)
        {
            return;
        }

        // A node that names a span starts a few lines above it, so a method does not begin flush
        // against the top edge with its signature as the very first thing visible.
        var start = node.StartLine is { } line ? Math.Max(1, line - ContextLines) : 1;

        Title = string.IsNullOrWhiteSpace(node.Title) ? source.Name : node.Title;
        Path = source.Value;
        Detail = string.Empty;
        Text = string.Empty;
        Gutter = string.Empty;
        Error = null;
        IsLoading = true;
        IsOpen = true;

        var token = Restart();

        try
        {
            var result = await _workspace.ReadAsync(source, start, MaxLines, token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (!result.Success || result.Value is not { } file)
            {
                // The sandbox refuses in a sentence written for a person to read - a protected name,
                // a file past the cap, a path that is no longer there - so it is shown as it came.
                Error = result.Error ?? "That file could not be read.";
                return;
            }

            Text = file.Content;
            Gutter = BuildGutter(file.FirstLine, file.LineCount);
            Detail = Describe(file);
        }
        catch (OperationCanceledException)
        {
            // Another node was asked for, or the panel closed. Whatever is on screen belongs to
            // that newer request, and this one has nothing left to say.
        }
        catch (Exception ex)
        {
            // Never the exception's own text: it would put an absolute path, and with it the
            // account name, into the window.
            _logger.LogWarning(ex, "The code behind a node could not be read.");

            Error = "That file could not be read.";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Closes the panel and abandons whatever it was reading.</summary>
    [RelayCommand]
    public void Close()
    {
        Cancel();

        IsOpen = false;
        IsLoading = false;
        Title = string.Empty;
        Path = string.Empty;
        Detail = string.Empty;
        Text = string.Empty;
        Gutter = string.Empty;
        Error = null;
    }

    /// <summary>How much of the file is on screen, or how long it is when that is all of it.</summary>
    private static string Describe(WorkspaceFile file)
    {
        var last = file.FirstLine + file.LineCount - 1;

        if (file.FirstLine <= 1 && last >= file.TotalLines && !file.IsTruncated)
        {
            return file.TotalLines == 1 ? "1 line" : $"{file.TotalLines} lines";
        }

        return $"Lines {file.FirstLine}-{last} of {file.TotalLines}";
    }

    /// <summary>
    /// The line numbers as a single block of text.
    /// </summary>
    /// <remarks>
    /// One string in a second column, not a row per number: twelve hundred text blocks in an items
    /// control is twelve hundred elements and a visible pause on every open, and all the numbers
    /// have to do is line up with the code - which two boxes sharing a font and a line height
    /// already do.
    /// </remarks>
    private static string BuildGutter(int firstLine, int lineCount)
    {
        if (lineCount <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(lineCount * 5);

        for (var i = 0; i < lineCount; i++)
        {
            builder.Append(firstLine + i).Append('\n');
        }

        return builder.ToString(0, builder.Length - 1);
    }

    /// <summary>Drops the read in flight and hands back the token for the one replacing it.</summary>
    private CancellationToken Restart()
    {
        Cancel();

        var source = new CancellationTokenSource();
        _load = source;

        return source.Token;
    }

    private void Cancel()
    {
        var running = _load;
        _load = null;

        running?.Cancel();
        running?.Dispose();
    }
}
