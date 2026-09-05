using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIClient.App.Canvas;
using AIClient.App.Services;
using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;

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
    private readonly CanvasController _controller;

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

    /// <summary>Raised when the user asks to frame the whole graph; the view knows its own size.</summary>
    public event EventHandler? FitRequested;

    /// <summary>Raised when the user asks to frame the current selection.</summary>
    public event EventHandler? FocusSelectionRequested;

    /// <summary>Raised when a node is activated (double-click or Enter): the context surface opens it.</summary>
    public event EventHandler<string>? NodeActivated;

    /// <summary>Raised when the user wants AI's opinion on the current selection.</summary>
    public event EventHandler? AskAiRequested;

    public CanvasViewModel(IGraphService graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        _graph = graph;
        _controller = new CanvasController(this);

        _graph.SnapshotChanged += OnGraphSnapshotChanged;
        _graph.TimelineChanged += OnTimelineChanged;

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
                Title = positions.Count == 1 ? "Move node" : $"Move {positions.Count} nodes",
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
            Title = nodeIds.Count == 1 ? "Remove node" : $"Remove {nodeIds.Count} nodes",
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

    // -------------------------------------------------------------- events

    // Graph events arrive on whatever thread finished the work - the agent's tool call
    // for a plan, a thread-pool re-index. The controller, the Timeline collection and
    // every mirrored observable all belong to the UI thread, so the hop back is taken
    // here, once, at the boundary (see UiThread).
    private void OnGraphSnapshotChanged(object? sender, GraphSnapshot snapshot)
    {
        UiThread.Post(() =>
        {
            _controller.SetSnapshot(snapshot);
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
            0 => _controller.SelectedEdgeId is null ? string.Empty : "1 connection",
            1 => "1 node",
            _ => $"{_controller.SelectedNodeIds.Count} nodes",
        };
    }

    /// <summary>Subscribes the view's canvas element to the controller; called once on load.</summary>
    public void AttachTo(GraphCanvas canvas)
    {
        canvas.SetController(_controller);

        _controller.GestureReceived += OnGesture;

        // The controller existed before the view; viewport changes raised without a
        // listener need one nudge to reach the canvas now that it listens.
        canvas.RefreshPalette();
    }

    /// <summary>Detaches view-only handlers; graph subscriptions stay for the process lifetime.</summary>
    public void DetachFrom(GraphCanvas canvas)
    {
        _controller.GestureReceived -= OnGesture;
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
