using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIClient.App.Canvas;
using AIClient.App.Services;
using AIClient.Application.Graph;
using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;
using Localization = AIClient.App.Services.Localization;

namespace AIClient.App.ViewModels;

/// <summary>
/// Presentation state for the canvas mode: what the chrome around the canvas displays, and
/// the commands it offers.
/// </summary>
/// <remarks>
/// <para>
/// The heavy lifting lives in <see cref="CanvasController"/> (interaction state the canvas
/// element drives) and <see cref="IGraphService"/> (the graph itself). This view model is
/// the thin, bindable half: it mirrors controller state into properties the chrome can
/// show, turns menu intents into viewport math, and routes graph edits through the service
/// so that every change - user drag or agent plan - is one pipeline.
/// </para>
/// <para>
/// Viewport and selection are owned by the controller and therefore survive mode switches:
/// leaving canvas mode, coming back, and finding the graph exactly where it was is a
/// property of the architecture, not a feature that had to be built.
/// </para>
/// </remarks>
public sealed partial class CanvasViewModel : ObservableObject, CanvasController.IGraphAccess
{
    private readonly IGraphService _graph;
    private readonly IAppThemeService _theme;
    private readonly CanvasController _controller;

    // Held while the view is attached, so a theme change can re-resolve the renderer's
    // cached palette; the canvas caches brushes rather than binding, so it needs the nudge.
    private GraphCanvas? _attachedCanvas;

    [ObservableProperty]
    private double _zoomPercent = 100;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _hasEdgeSelection;

    [ObservableProperty]
    private string _selectionSummary = string.Empty;

    [ObservableProperty]
    private int _nodeCount;

    [ObservableProperty]
    private int _edgeCount;

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private bool _canRedo;

    [ObservableProperty]
    private bool _isIndexing;

    /// <summary>Timeline entries, newest first; the context surface shows these under the inspector.</summary>
    public ObservableCollection<GraphTimelineEntry> Timeline { get; } = [];

    public CanvasController Controller => _controller;

    public GraphSnapshot Snapshot => _graph.Current;

    /// <summary>Mirrors the controller's active tool so the toolbar toggles can bind one-way.</summary>
    public bool IsSelectTool => _controller.ActiveTool == CanvasTool.Select;

    public bool IsPanTool => _controller.ActiveTool == CanvasTool.Pan;

    /// <summary>Raised when the user asks to frame the whole graph; the view knows its own size.</summary>
    public event EventHandler? FitRequested;

    /// <summary>Raised when the user asks to frame the current selection.</summary>
    public event EventHandler? FocusSelectionRequested;

    /// <summary>Raised when a node is activated (double-click or Enter): the context surface opens it.</summary>
    public event EventHandler<string>? NodeActivated;

    /// <summary>Raised when the user wants AI's opinion on the current selection.</summary>
    public event EventHandler? AskAiRequested;

    public CanvasViewModel(IGraphService graph, IAppThemeService theme)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(theme);

        _graph = graph;
        _theme = theme;
        _controller = new CanvasController(this);

        _graph.SnapshotChanged += OnGraphSnapshotChanged;
        _graph.TimelineChanged += OnTimelineChanged;
        _controller.ToolChanged += OnToolChanged;

        // The canvas is created after the graph service (singleton, eager); bring the
        // controller up to the present immediately rather than waiting for the next change.
        _controller.SetSnapshot(_graph.Current);
        SyncTimeline();
        MirrorState();
    }

    // -------------------------------------------------------- graph access

    /// <summary>One drag, one change set: the timeline stays legible and undo undoes a gesture.</summary>
    void CanvasController.IGraphAccess.CommitNodeMoves(IReadOnlyDictionary<string, Point> positions)
    {
        if (positions.Count == 0)
        {
            return;
        }

        var changes = positions.Select(pair =>
        {
            if (!_graph.Current.TryGetNode(pair.Key, out var node))
            {
                return null as GraphChange;
            }

            return new MoveNode(pair.Key, pair.Value.X, pair.Value.Y) as GraphChange;
        })
            .OfType<GraphChange>()
            .ToList();

        if (changes.Count > 0)
        {
            _ = ApplyAsync(new GraphChangeSet
            {
                Title = positions.Count == 1
                    ? Localization.T("S.History.MoveNode")
                    : Localization.T("S.History.MoveNodes", positions.Count),
                Origin = GraphChangeOrigin.User,
                Changes = changes,
            });
        }
    }

    void CanvasController.IGraphAccess.DeleteNodes(IReadOnlyCollection<string> nodeIds)
    {
        if (nodeIds.Count == 0)
        {
            return;
        }

        _ = ApplyAsync(new GraphChangeSet
        {
            Title = nodeIds.Count == 1
                ? Localization.T("S.History.RemoveNode")
                : Localization.T("S.History.RemoveNodes", nodeIds.Count),
            Description = "Removed from the canvas by the user.",
            Origin = GraphChangeOrigin.User,
            Changes = nodeIds.Select(id => new RemoveNode(id) as GraphChange).ToList(),
        });
    }

    /// <summary>Applies a change set on the UI thread and persists; graph events drive the redraw.</summary>
    public async Task ApplyAsync(GraphChangeSet changeSet)
    {
        await _graph.ApplyAsync(changeSet).ConfigureAwait(true);
        await SaveAsync().ConfigureAwait(true);
    }

    public Task SaveAsync() => _graph.SaveAsync(PersistenceKey);

    /// <summary>The persistence key, supplied by the workspace owner once the root is known.</summary>
    public string PersistenceKey { get; set; } = "workspace-none";

    // ------------------------------------------------------------ commands

    [RelayCommand]
    private void ZoomIn() => _controller.ZoomAt(new Point(400, 300), 1.25);

    [RelayCommand]
    private void ZoomOut() => _controller.ZoomAt(new Point(400, 300), 1 / 1.25);

    [RelayCommand]
    private void ZoomReset() => _controller.SetViewport(1, _controller.Offset.X, _controller.Offset.Y);

    [RelayCommand]
    private void Fit() => FitRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void FocusSelection() => FocusSelectionRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ClearSelection()
    {
        _controller.ClearSelection();
        _controller.ClearEdgeSelection();
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        await _graph.UndoAsync().ConfigureAwait(true);
        await SaveAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RedoAsync()
    {
        await _graph.RedoAsync().ConfigureAwait(true);
        await SaveAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void RemoveSelection() => _controller.DeleteSelection();

    [RelayCommand]
    private void AskAi() => AskAiRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void UseSelectTool() => _controller.SetTool(CanvasTool.Select);

    [RelayCommand]
    private void UsePanTool() => _controller.SetTool(CanvasTool.Pan);

    /// <summary>Re-arranges every node by an automatic layout and frames the result - one undoable change set.</summary>
    [RelayCommand]
    private async Task AutoLayoutAsync()
    {
        var snapshot = _graph.Current;
        if (snapshot.Nodes.Count == 0)
        {
            return;
        }

        var positions = ChooseLayout(snapshot);
        var changes = positions
            .Select(pair => new MoveNode(pair.Key, pair.Value.X, pair.Value.Y) as GraphChange)
            .ToList();
        if (changes.Count == 0)
        {
            return;
        }

        await ApplyAsync(new GraphChangeSet
        {
            Title = Localization.T("S.Canvas.AutoLayout"),
            Description = "Nodes re-arranged by an automatic layout.",
            Origin = GraphChangeOrigin.User,
            Changes = changes,
        }).ConfigureAwait(true);

        FitRequested?.Invoke(this, EventArgs.Empty);
    }

    // Contains-heavy graphs (files and folders) read best layered; small graphs get a force
    // pass; anything large or edgeless falls back to a deterministic grid.
    private static IReadOnlyDictionary<string, (double X, double Y)> ChooseLayout(GraphSnapshot snapshot)
    {
        if (snapshot.Nodes.Count > 500)
        {
            return GraphLayouts.Grid(snapshot);
        }

        if (snapshot.Edges.Any(edge => edge.Kind == GraphEdgeKind.Contains))
        {
            return GraphLayouts.Layered(snapshot);
        }

        var force = GraphLayouts.Force(snapshot);
        return force.Count > 0 ? force : GraphLayouts.Grid(snapshot);
    }

    // -------------------------------------------------------------- events

    // Graph events arrive on whatever thread finished the work - the agent's tool call
    // for a plan, a thread-pool re-index. The controller, the Timeline collection and
    // every mirrored observable all belong to the UI thread, so the hop back is taken
    // here, once, at the boundary (see UiThread).
    private void OnGraphSnapshotChanged(object? sender, GraphSnapshotChangedEventArgs e)
    {
        UiThread.Post(() =>
        {
            _controller.SetSnapshot(e.Snapshot, forceReset: e.IsReload);
            MirrorState();
        });
    }

    private void OnTimelineChanged(object? sender, EventArgs e)
    {
        UiThread.Post(() =>
        {
            SyncTimeline();
            MirrorState();
        });
    }

    private void SyncTimeline()
    {
        Timeline.Clear();

        foreach (var entry in _graph.Timeline)
        {
            Timeline.Add(entry);
        }

        CanUndo = _graph.CanUndo;
        CanRedo = _graph.CanRedo;
    }

    private void MirrorState()
    {
        ZoomPercent = Math.Round(_controller.Zoom * 100);
        HasSelection = _controller.SelectedNodeIds.Count > 0;
        HasEdgeSelection = _controller.SelectedEdgeId is not null;
        NodeCount = _graph.Current.Nodes.Count;
        EdgeCount = _graph.Current.Edges.Count;

        SelectionSummary = _controller.SelectedNodeIds.Count switch
        {
            0 => _controller.SelectedEdgeId is null ? string.Empty : Localization.T("S.Status.Selection.OneConnection"),
            1 => Localization.T("S.Status.Selection.OneNode"),
            _ => Localization.T("S.Status.Selection.Nodes", _controller.SelectedNodeIds.Count),
        };

        OnPropertyChanged(nameof(CountSummary));
    }

    /// <summary>Node and edge counts as one localized line for the status bar.</summary>
    public string CountSummary => NodeCount == 0 && EdgeCount == 0
        ? Localization.T("S.Status.Counts.Empty")
        : Localization.T("S.Status.Counts", NodeCount, EdgeCount);

    /// <summary>Re-reads the computed strings after a language switch.</summary>
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(CountSummary));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    /// <summary>Subscribes the view's canvas element to the controller; called once on load.</summary>
    public void AttachTo(GraphCanvas canvas)
    {
        canvas.SetController(_controller);

        _controller.GestureReceived += OnGesture;

        // A theme change swaps the token dictionary, but the renderer caches resolved
        // brushes; re-read them whenever the effective theme changes while the canvas lives.
        _attachedCanvas = canvas;
        _theme.EffectiveThemeChanged += OnThemeChanged;

        // The controller existed before the view; viewport changes raised without a
        // listener need one nudge to reach the canvas now that it listens.
        canvas.RefreshPalette();
    }

    /// <summary>Detaches view-only handlers; graph subscriptions stay for the process lifetime.</summary>
    public void DetachFrom(GraphCanvas canvas)
    {
        _controller.GestureReceived -= OnGesture;
        _theme.EffectiveThemeChanged -= OnThemeChanged;
        _attachedCanvas = null;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => _attachedCanvas?.RefreshPalette();

    private void OnToolChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsSelectTool));
        OnPropertyChanged(nameof(IsPanTool));
    }

    private void OnGesture(object? sender, GestureEventArgs e)
    {
        switch (e.Kind)
        {
            case GraphCanvas.GestureKind.NodeActivated when e.NodeId is not null:
                NodeActivated?.Invoke(this, e.NodeId);
                break;

            case GraphCanvas.GestureKind.BackgroundDoubleClicked:
                FitRequested?.Invoke(this, EventArgs.Empty);
                break;

            case GraphCanvas.GestureKind.AskAiRequested:
                AskAiRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }
}
