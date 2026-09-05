using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIClient.App.Controls;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Markdown;
using AIClient.Domain.Workspace;

namespace AIClient.App.ViewModels;

/// <summary>
/// The code view's open documents: tabs over workspace files, read-only, highlighted.
/// </summary>
/// <remarks>
/// <para>
/// A full editor is a product of its own; what the workspace needs is to <i>read</i> what
/// the graph and the agent are talking about. Each tab is one <see
/// cref="IWorkspaceService.ReadAsync"/> call - the same capped, safe read the agent's
/// tools get - rendered through the same highlighter the chat already uses for code
/// blocks. Nothing here can modify a file, and that is not a gap: changes arrive through
/// the agent's approval flow, which shows diffs where they belong.
/// </para>
/// <para>
/// Tabs are kept in the view model, so switching workspace modes keeps every document,
/// its scroll position included (the views stay alive and merely hide). Reopening a file
/// focuses its existing tab rather than stacking a second copy.
/// </para>
/// </remarks>
public sealed partial class CodeViewModel : ObservableObject
{
    private const int ReadLineCount = 1200;

    private readonly IWorkspaceService _workspace;

    [ObservableProperty]
    private CodeTabViewModel? _activeTab;

    partial void OnActiveTabChanged(CodeTabViewModel? oldValue, CodeTabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
        }

        if (newValue is not null)
        {
            newValue.IsActive = true;
        }

        ActiveTabChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the front document changes: the host re-renders the code surface.</summary>
    public event EventHandler? ActiveTabChanged;

    [ObservableProperty]
    private bool _hasTabs;

    public ObservableCollection<CodeTabViewModel> Tabs { get; } = [];

    public event EventHandler<CodeTabViewModel>? TabActivated;

    public CodeViewModel(IWorkspaceService workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
    }

    /// <summary>Opens a file as a tab, or focuses it if it is already open.</summary>
    public async Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var existing = Tabs.FirstOrDefault(tab => tab.Path == path);

        if (existing is not null)
        {
            ActiveTab = existing;
            TabActivated?.Invoke(this, existing);
            return;
        }

        var tab = new CodeTabViewModel(path);

        Tabs.Add(tab);
        ActiveTab = tab;
        HasTabs = Tabs.Count > 0;
        TabActivated?.Invoke(this, tab);

        await tab.LoadAsync(_workspace, ReadLineCount, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CloseTab(CodeTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);

        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);

        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = Tabs.Count > 0
                ? Tabs[Math.Clamp(index, 0, Tabs.Count - 1)]
                : null;
        }

        HasTabs = Tabs.Count > 0;
    }

    [RelayCommand]
    private void CloseAll()
    {
        Tabs.Clear();
        ActiveTab = null;
        HasTabs = false;
    }

    /// <summary>Reloads the active tab's content - the agent may have rewritten the file.</summary>
    [RelayCommand]
    private async Task ReloadActiveAsync(CancellationToken cancellationToken)
    {
        if (ActiveTab is { } tab)
        {
            await tab.LoadAsync(_workspace, ReadLineCount, cancellationToken).ConfigureAwait(true);
        }
    }
}

/// <summary>One open document: the file, its highlighted lines, and its load state.</summary>
public sealed partial class CodeTabViewModel : ObservableObject
{
    [ObservableProperty]
    private IReadOnlyList<IReadOnlyList<CodeToken>> _lines = [];

    [ObservableProperty]
    private CodeLoadState _state = CodeLoadState.Loading;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isTruncated;

    [ObservableProperty]
    private int _totalLines;

    [ObservableProperty]
    private int _firstLine = 1;

    public CodeTabViewModel(string path)
    {
        Path = path;
        Title = System.IO.Path.GetFileName(path);
        Language = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        Icon = WorkspaceIcons.ForFile(path);
    }

    public string Path { get; }

    public string Title { get; }

    /// <summary>Set by the document host when this tab is the front one.</summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// The gutter's text: 1..N, one per line, joined with newlines. Built once per load;
    /// the gutter and the code share a face and a line height, so they stay aligned.
    /// </summary>
    public string LineNumbers => string.Join("\n", Enumerable.Range(FirstLine, Lines.Count).Select(n => n.ToString()));

    /// <summary>The file extension, which is also the highlighter's language id.</summary>
    public string Language { get; }

    public IconKind Icon { get; }

    /// <summary>The workspace-relative path, minus the file name - the tab's breadcrumb.</summary>
    public string Directory => Path.Contains('/')
        ? Path[..Path.LastIndexOf('/')]
        : string.Empty;

    internal async Task LoadAsync(IWorkspaceService workspace, int lineCount, CancellationToken cancellationToken)
    {
        State = CodeLoadState.Loading;
        Error = null;

        WorkspaceResult<WorkspaceFile> result;

        try
        {
            result = await workspace.ReadAsync(
                WorkspacePath.Parse(Path),
                startLine: 1,
                lineCount,
                cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            State = CodeLoadState.Failed;
            Error = ex.Message;
            return;
        }

        if (!result.Success || result.Value is null)
        {
            State = CodeLoadState.Failed;
            Error = result.Error ?? "The file could not be read.";
            return;
        }

        var file = result.Value;

        Lines = SyntaxHighlighter.Highlight(file.Content, Language);
        FirstLine = file.FirstLine;
        TotalLines = file.TotalLines;
        IsTruncated = file.IsTruncated;
        State = CodeLoadState.Loaded;
        OnPropertyChanged(nameof(LineNumbers));
    }
}

public enum CodeLoadState
{
    Loading,
    Loaded,
    Failed,
}
