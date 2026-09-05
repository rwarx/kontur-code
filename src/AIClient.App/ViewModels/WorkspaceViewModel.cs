using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIClient.App.Graph;
using AIClient.Application.Graph;
using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels;

/// <summary>
/// The workspace: one folder, one graph over it, the modes that show it, and the glue that
/// keeps every surface pointing at the same thing.
/// </summary>
/// <remarks>
/// <para>
/// This is the composition point of the whole shell. Modes (canvas, graph, files, code,
/// chat, models, tasks, settings) are switches over shared state, not pages with their own
/// copies: the file the outline selects is the tab code opens is the node the canvas
/// focuses, because they all live here and only their views differ.
/// </para>
/// <para>
/// Graph lifecycle is the other half: when the workspace root changes, the previous
/// folder's graph is persisted under its own key, the new folder's is loaded (or indexed
/// fresh on first contact), and every later change - refresh, plan drawing, user edit -
/// saves under the current key. Nothing about that is visible here beyond two calls; the
/// pipeline does the rest.
/// </para>
/// </remarks>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspace;
    private readonly IGraphService _graph;
    private readonly WorkspaceGraphIndexer _indexer;
    private readonly IDialogSurface _dialogs;
    private readonly ILogger<WorkspaceViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDocumentTabs))]
    [NotifyPropertyChangedFor(nameof(IsSpatialMode))]
    private WorkspaceMode _mode = WorkspaceMode.Chat;

    [ObservableProperty]
    private bool _hasWorkspace;

    [ObservableProperty]
    private string _workspaceName = string.Empty;

    [ObservableProperty]
    private string _workspaceRoot = string.Empty;

    /// <summary>True while a re-index is running; the status bar shows it.</summary>
    [ObservableProperty]
    private bool _isIndexing;

    public CanvasViewModel Canvas { get; }

    public GraphOutlineViewModel Outline { get; }

    public FilesViewModel Files { get; }

    public CodeViewModel Code { get; }

    public ContextPanelViewModel Context { get; }

    /// <summary>Raised when a file should be opened in the code view and the mode switched to it.</summary>
    public event EventHandler<string>? OpenInCodeRequested;

    /// <summary>Raised when the user asks the AI about something the workspace knows.</summary>
    public event EventHandler<string>? AskAiRequested;

    public WorkspaceViewModel(
        IWorkspaceService workspace,
        IGraphService graph,
        WorkspaceGraphIndexer indexer,
        IDialogSurface dialogs,
        CanvasViewModel canvas,
        GraphOutlineViewModel outline,
        FilesViewModel files,
        CodeViewModel code,
        ContextPanelViewModel context,
        ILogger<WorkspaceViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        _workspace = workspace;
        _graph = graph;
        _indexer = indexer;
        _dialogs = dialogs;
        _logger = logger;

        Canvas = canvas;
        Outline = outline;
        Files = files;
        Code = code;
        Context = context;

        // Surfaces talk to each other through the workspace, never to each other's
        // internals: that is what keeps "open" and "focus" consistent everywhere.
        Files.FileActivated += OnFileActivated;
        Context.OpenPathRequested += OnFileActivated;
        Context.FocusNodeRequested += OnFocusNodeRequested;
        Context.AskAiRequested += OnContextAskAi;
        Outline.NodeActivated += OnOutlineNodeActivated;
        Canvas.AskAiRequested += OnCanvasAskAi;

        _workspace.RootChanged += OnRootChanged;
        SyncRoot();
    }

    /// <summary>The five modes that share the workspace tab strip.</summary>
    public bool IsSpatialMode => Mode is WorkspaceMode.Canvas
        or WorkspaceMode.Graph
        or WorkspaceMode.Files
        or WorkspaceMode.Code
        or WorkspaceMode.Chat;

    public bool ShowsDocumentTabs => Mode == WorkspaceMode.Code;

    [RelayCommand]
    public void SwitchMode(WorkspaceMode mode) => Mode = mode;

    /// <summary>
    /// The empty canvas's second door: ask the agent for a plan to draw. Lands in the
    /// composer as a starting point, not a sent message - the user decides.
    /// </summary>
    [RelayCommand]
    private void AskForPlan() =>
        AskAiRequested?.Invoke(this,
            "Plan a small feature for this workspace. Explore the folder first, then submit the plan so I can see it drawn on the canvas.");

    // --------------------------------------------------------- workspace IO

    [RelayCommand]
    private async Task OpenWorkspaceAsync(CancellationToken cancellationToken)
    {
        var folder = await _dialogs.OpenFolderAsync("Choose the workspace folder").ConfigureAwait(true);

        if (folder is null)
        {
            return;
        }

        var result = await _workspace.OpenAsync(folder, cancellationToken).ConfigureAwait(true);

        if (!result.Success)
        {
            await _dialogs.ShowErrorAsync("Cannot open that folder", result.Error ?? "It was refused.").ConfigureAwait(true);
            return;
        }

        // RootChanged has done the graph swap already; this is only the user-facing cue.
        SwitchMode(WorkspaceMode.Canvas);
    }

    [RelayCommand]
    private async Task CloseWorkspaceAsync(CancellationToken cancellationToken)
    {
        await _workspace.CloseAsync(cancellationToken).ConfigureAwait(true);
        await _graph.SaveAsync(WorkspaceGraphKeys.FromWorkspaceRoot(_workspace.Root)).ConfigureAwait(true);
    }

    /// <summary>Re-indexes the workspace into the graph, positions and all.</summary>
    [RelayCommand]
    public async Task RefreshGraphAsync(CancellationToken cancellationToken)
    {
        if (!_workspace.IsOpen || IsIndexing)
        {
            return;
        }

        IsIndexing = true;

        try
        {
            var result = await _indexer.RebuildAsync(cancellationToken).ConfigureAwait(true);

            if (result.Rejected.Count > 0)
            {
                _logger.LogInformation("Indexer rejections: {Reasons}", string.Join("; ", result.Rejected));
            }

            await Canvas.SaveAsync().ConfigureAwait(true);
        }
        finally
        {
            IsIndexing = false;
        }
    }

    private async void OnRootChanged(object? sender, string? root)
    {
        // RootChanged can arrive from a background thread (the service persists settings
        // before raising); every subscriber below touches UI state.
        await AIClient.App.Services.UiThread.RunAsync(async () =>
        {
            // Save whatever was on the canvas under the OLD key before adopting the new
            // root - closing one workspace and opening another must not lose the first
            // one's plan drawings.
            var previousKey = Canvas.PersistenceKey;

            if (_graph.Current.Nodes.Count > 0 && previousKey != "workspace-none")
            {
                await _graph.SaveAsync(previousKey).ConfigureAwait(true);
            }

            var key = WorkspaceGraphKeys.FromWorkspaceRoot(root);
            Canvas.PersistenceKey = key;

            var loaded = await _graph.LoadAsync(key).ConfigureAwait(true);

            if (loaded.Nodes.Count == 0 && !string.IsNullOrEmpty(root))
            {
                // First contact with this folder: reflect it onto the canvas immediately.
                await RefreshGraphCommand.ExecuteAsync(null).ConfigureAwait(true);
            }

            SyncRoot();
            Context.SetWorkspace(root);
        }).ConfigureAwait(true);
    }

    private void SyncRoot()
    {
        HasWorkspace = _workspace.IsOpen;
        WorkspaceRoot = _workspace.Root ?? string.Empty;
        WorkspaceName = HasWorkspace && WorkspaceRoot.Length > 0
            ? System.IO.Path.GetFileName(WorkspaceRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar))
            : "No workspace";
    }

    // -------------------------------------------------------------- routing

    private void OnFileActivated(object? sender, string path)
    {
        OpenInCodeRequested?.Invoke(this, path);
        _ = Code.OpenAsync(path);
        Mode = WorkspaceMode.Code;
    }

    private void OnFocusNodeRequested(object? sender, string nodeId)
    {
        Mode = WorkspaceMode.Canvas;
        Canvas.Controller.SetSelection(AIClient.App.Canvas.SelectionMode.Replace, nodeId);
        Canvas.FocusSelectionCommand.Execute(null);
    }

    private void OnOutlineNodeActivated(object? sender, string nodeId) =>
        OnFocusNodeRequested(sender, nodeId);

    private void OnContextAskAi(object? sender, string prompt) => AskAiRequested?.Invoke(this, prompt);

    private void OnCanvasAskAi(object? sender, EventArgs e) => AskAiRequested?.Invoke(this, "Explain the selected parts of this workspace");

    /// <summary>
    /// Raises <see cref="AskAiRequested"/> on the shell's behalf - the Ctrl+I path asks
    /// about the canvas selection with graph context the composer cannot build itself.
    /// </summary>
    public void RequestAskAi(string prompt) => AskAiRequested?.Invoke(this, prompt);

    /// <summary>Brings the workspace to a consistent state on startup.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var key = WorkspaceGraphKeys.FromWorkspaceRoot(_workspace.Root);
        Canvas.PersistenceKey = key;

        var loaded = await _graph.LoadAsync(key, cancellationToken).ConfigureAwait(true);

        if (loaded.Nodes.Count == 0 && _workspace.IsOpen)
        {
            await RefreshGraphAsync(cancellationToken).ConfigureAwait(true);
        }

        Context.SetWorkspace(_workspace.Root);
        await Files.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
    }
}

/// <summary>The dialog surface the workspace needs: folder picking and an error box.</summary>
/// <remarks>An interface rather than a service reference, so the view model stays testable
/// and the shell decides what "asking the user" means.</remarks>
public interface IDialogSurface
{
    Task<string?> OpenFolderAsync(string title);

    Task ShowErrorAsync(string title, string message);
}

/// <summary>The workspace's modes. Five share the tab strip; the rest are full surfaces.</summary>
public enum WorkspaceMode
{
    Canvas,
    Graph,
    Files,
    Code,
    Chat,
    Models,
    Tasks,
    Settings,
}
