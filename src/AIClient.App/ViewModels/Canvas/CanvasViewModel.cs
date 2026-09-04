using System.Collections.ObjectModel;
using System.IO;
using AIClient.App.Services;
using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Domain.Graph;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels.Canvas;

/// <summary>
/// The canvas: a spatial projection of the knowledge graph, and the surface a person uses to
/// pick a part of their project out and ask about it.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of state live here and they are not equal. The graph is the truth and arrives from
/// <see cref="IGraphService"/>; this class only reads it, and every change to it goes back as a
/// change set through that service. Positions, the camera and the selection are canvas state,
/// owned here, persisted through <see cref="ICanvasViewStore"/>, and losing all of it would cost
/// a tidy diagram and not one fact about the project.
/// </para>
/// <para>
/// The AI path is a handoff, not a second pipeline: a selection plus a prompt is raised as
/// <see cref="AiRequested"/>, the shell hands it to the existing chat, and the graph block is
/// assembled by <c>IGraphContextSource</c> inside the existing context build.
/// </para>
/// </remarks>
public sealed partial class CanvasViewModel : ObservableObject
{
    /// <summary>
    /// Below this many nodes everything is realised and culling is skipped: the diff costs more
    /// than the elements save, and a small graph should never flicker while panning.
    /// </summary>
    private const int CullThreshold = 400;

    /// <summary>World-space margin kept around the viewport so cards appear before they scroll in.</summary>
    private const double CullMargin = 240;

    private const double ZoomStep = 1.25;

    /// <summary>
    /// How long the camera has to sit still before it is written down. Panning fires continuously;
    /// without this the store would take a write per mouse move.
    /// </summary>
    private static readonly TimeSpan ViewportSaveDelay = TimeSpan.FromMilliseconds(700);

    /// <summary>The starting count, and what the status line falls back to between passes.</summary>
    private static readonly GraphIndexProgress Nothing = new() { Nodes = 0, Edges = 0 };

    private readonly IGraphService _graph;
    private readonly IGraphIndexer _indexer;
    private readonly ICanvasViewStore _store;
    private readonly IWorkspaceService _workspace;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogger<CanvasViewModel> _logger;

    /// <summary>Every card, visible or not, by node id.</summary>
    private readonly Dictionary<Guid, CanvasNodeViewModel> _cards = [];

    private readonly Dictionary<Guid, CanvasEdgeViewModel> _links = [];

    /// <summary>Which links touch a node, so a drag refreshes only the curves that moved.</summary>
    private readonly Dictionary<Guid, List<CanvasEdgeViewModel>> _linksByNode = [];

    private readonly HashSet<Guid> _selected = [];
    private readonly HashSet<Guid> _shownNodes = [];
    private readonly HashSet<Guid> _shownLinks = [];

    /// <summary>The cards a drag is moving, captured when the gesture starts.</summary>
    private readonly List<CanvasNodeViewModel> _dragging = [];

    private CanvasViewState? _view;
    private CancellationTokenSource? _viewportSave;

    /// <summary>The latest count reported by a running index, for the status line.</summary>
    private GraphIndexProgress _indexed = Nothing;

    private bool _initialized;
    private bool _dragMoved;
    private double _marqueeAnchorX;
    private double _marqueeAnchorY;

    /// <summary>The camera. World and screen are related by <c>screen = world * Zoom + Pan</c>.</summary>
    [ObservableProperty]
    private CanvasViewport _viewport = CanvasViewport.Default;

    /// <summary>Size of the drawing surface in device-independent pixels, reported by the view.</summary>
    [ObservableProperty]
    private double _surfaceWidth;

    [ObservableProperty]
    private double _surfaceHeight;

    [ObservableProperty]
    private bool _isIndexing;

    /// <summary>True once the graph has been read, so "empty" can be told apart from "not read yet".</summary>
    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private string _graphStatus = string.Empty;

    [ObservableProperty]
    private string _selectionStatus = string.Empty;

    /// <summary>Where the selection sits in the graph - not in the filesystem.</summary>
    [ObservableProperty]
    private string _breadcrumb = "Project";

    /// <summary>A refusal or a failure, shown in the status strip and cleared by the next action.</summary>
    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private bool _isMarqueeVisible;

    [ObservableProperty]
    private double _marqueeX;

    [ObservableProperty]
    private double _marqueeY;

    [ObservableProperty]
    private double _marqueeWidth;

    [ObservableProperty]
    private double _marqueeHeight;

    /// <summary>
    /// The AI surface is anchored to the selection rather than parked in a panel, because the
    /// question a person is about to ask is about what they just picked out.
    /// </summary>
    [ObservableProperty]
    private bool _isAiSurfaceVisible;

    [ObservableProperty]
    private double _aiSurfaceX;

    [ObservableProperty]
    private double _aiSurfaceY;

    /// <summary>"AuthService" for one card, "3 nodes · 4 relations" for several.</summary>
    [ObservableProperty]
    private string _aiSurfaceHeader = string.Empty;

    /// <summary>What the left mouse button does. Chosen in the toolbar, not inferred from a modifier.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectTool))]
    [NotifyPropertyChangedFor(nameof(IsPanTool))]
    private CanvasTool _tool = CanvasTool.Select;

    public CanvasViewModel(
        IGraphService graph,
        IGraphIndexer indexer,
        ICanvasViewStore store,
        IWorkspaceService workspace,
        ISettingsService settings,
        IDialogService dialogs,
        CanvasCodeViewModel code,
        ILogger<CanvasViewModel> logger)
    {
        _graph = graph;
        _indexer = indexer;
        _store = store;
        _workspace = workspace;
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;

        Code = code;

        // Subscribed once, for the life of the process: this view model is a singleton and its
        // state outlives any visit to the page, so hooking up on navigation would either miss
        // changes made elsewhere or pile up duplicate handlers.
        _graph.Changed += OnGraphChanged;
        _workspace.RootChanged += OnWorkspaceRootChanged;

        UpdateGraphStatus();
    }

    /// <summary>
    /// Raised when a person asks the AI about a selection. The shell forwards it to the existing
    /// chat; nothing here talks to a provider.
    /// </summary>
    public event EventHandler<CanvasAiRequest>? AiRequested;

    /// <summary>Raised so the shell can point the inspector at whatever is selected.</summary>
    public event EventHandler<CanvasSelection>? SelectionChanged;

    /// <summary>
    /// The file behind a card, docked beside the surface.
    /// </summary>
    /// <remarks>
    /// Held rather than raised as an event, unlike the inspector: the panel belongs to the canvas
    /// column and is opened by a gesture on the surface, so routing it through the shell would add a
    /// hop without moving the decision anywhere useful.
    /// </remarks>
    public CanvasCodeViewModel Code { get; }

    /// <summary>The cards currently worth realising. Bound by the view; culled, not virtualised.</summary>
    public ObservableCollection<CanvasNodeViewModel> VisibleNodes { get; } = [];

    public ObservableCollection<CanvasEdgeViewModel> VisibleEdges { get; } = [];

    public double Zoom => Viewport.Zoom;

    public double PanX => Viewport.PanX;

    public double PanY => Viewport.PanY;

    public string ZoomText => $"{Viewport.Zoom * 100:0}%";

    public bool IsSelectTool => Tool == CanvasTool.Select;

    public bool IsPanTool => Tool == CanvasTool.Pan;

    public int NodeCount => _cards.Count;

    public bool HasSelection => _selected.Count > 0;

    public bool IsSingleSelection => _selected.Count == 1;

    public bool IsMultiSelection => _selected.Count > 1;

    /// <summary>
    /// Nothing to draw and nothing on the way - the state the empty screen speaks for. Distinct
    /// from "not loaded", which shows nothing at all rather than an invitation.
    /// </summary>
    public bool IsEmpty => IsLoaded && !IsIndexing && _cards.Count == 0;

    public bool HasContent => _cards.Count > 0;

    public bool IsWorkspaceOpen => _workspace.IsOpen;

    /// <summary>The folder being projected, shown on the empty screen and in the status strip.</summary>
    public string WorkspaceName
    {
        get
        {
            var root = _workspace.Root;
            if (string.IsNullOrWhiteSpace(root))
            {
                return "No folder open";
            }

            var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? root : name;
        }
    }

    /// <summary>The single selected card, or null when the selection is empty or wider than one.</summary>
    public CanvasNodeViewModel? Focused =>
        _selected.Count == 1 && _cards.TryGetValue(_selected.First(), out var card) ? card : null;

    /// <summary>
    /// Reads the graph and the stored canvas state. Safe to call on every visit to the page; only
    /// the first call does anything.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            await _graph.LoadAsync(cancellationToken).ConfigureAwait(true);
            _view = await _store.GetDefaultAsync(cancellationToken).ConfigureAwait(true);

            Viewport = _view.Viewport.Normalized();
            Sync(_graph.Current);

            if (HasContent && _settings.Current.Canvas.LayoutRevision < CanvasLayout.Revision)
            {
                // The stored positions came from arithmetic this build no longer uses, and nothing
                // else will ever ask for them again: indexing only places what has no place. One
                // pass here, pinned cards untouched, and the surface matches the code that draws it.
                // The camera is refitted with it, because it was pointed at a shape that is gone.
                await ArrangeAsync().ConfigureAwait(true);
            }
            else if (_view.Viewport == CanvasViewport.Default && HasContent)
            {
                // A stored camera is trusted; a default one is not. Landing on an empty corner of a
                // graph someone has never arranged is the single easiest way to make the canvas look
                // broken on first open.
                FitToContent();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The canvas could not be loaded.");
            Notice = "The canvas could not be loaded. The graph is still intact - try reopening the page.";
        }
        finally
        {
            IsLoaded = true;
            UpdateGraphStatus();
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>Called by the view when its size changes, so culling and the AI surface stay honest.</summary>
    public void SetSurfaceSize(double width, double height)
    {
        if (Math.Abs(SurfaceWidth - width) < 0.5 && Math.Abs(SurfaceHeight - height) < 0.5)
        {
            return;
        }

        SurfaceWidth = width;
        SurfaceHeight = height;
    }

    public double ToWorldX(double screenX) => Viewport.ToWorldX(screenX);

    public double ToWorldY(double screenY) => Viewport.ToWorldY(screenY);

    public double ToScreenX(double worldX) => Viewport.ToScreenX(worldX);

    public double ToScreenY(double worldY) => Viewport.ToScreenY(worldY);

    /// <summary>Moves the camera by a screen-space delta - what a drag of the surface produces.</summary>
    public void Pan(double deltaX, double deltaY) => Viewport = Viewport.Panned(deltaX, deltaY);

    /// <summary>
    /// Zooms about a screen point, keeping whatever is under it fixed. Anchoring on the cursor is
    /// what makes a wheel feel like a lens rather than a scrollbar.
    /// </summary>
    public void ZoomAt(double factor, double screenX, double screenY) =>
        Viewport = Viewport.ZoomedAt(factor, screenX, screenY);

    [RelayCommand]
    private void ZoomIn() => ZoomAt(ZoomStep, SurfaceWidth / 2, SurfaceHeight / 2);

    [RelayCommand]
    private void ZoomOut() => ZoomAt(1 / ZoomStep, SurfaceWidth / 2, SurfaceHeight / 2);

    /// <summary>Back to 1:1, centred on the content rather than on the origin.</summary>
    [RelayCommand]
    private void ResetView()
    {
        var content = ContentBounds();
        Viewport = content.IsEmpty || SurfaceWidth <= 0
            ? CanvasViewport.Default
            : CanvasViewport.Default.Centered(content, SurfaceWidth, SurfaceHeight);
    }

    [RelayCommand]
    private void FitToContent()
    {
        var content = ContentBounds();
        if (content.IsEmpty || SurfaceWidth <= 0 || SurfaceHeight <= 0)
        {
            return;
        }

        Viewport = CanvasViewport.Fit(content, SurfaceWidth, SurfaceHeight);
    }

    [RelayCommand]
    private void ZoomToSelection()
    {
        var content = SelectionBounds();
        if (content.IsEmpty || SurfaceWidth <= 0 || SurfaceHeight <= 0)
        {
            return;
        }

        Viewport = CanvasViewport.Fit(content, SurfaceWidth, SurfaceHeight);
    }

    /// <summary>
    /// Selects one node and brings it into view, keeping the current zoom.
    /// </summary>
    /// <remarks>
    /// How the inspector's relation list navigates: clicking "→ Depends On · UserRepository" should
    /// land on that card. Zoom is left alone deliberately - jumping the scale as well as the position
    /// makes it hard to tell where you ended up.
    /// </remarks>
    public void Focus(Guid nodeId)
    {
        if (!_cards.TryGetValue(nodeId, out var card))
        {
            return;
        }

        _selected.Clear();
        _selected.Add(nodeId);

        if (SurfaceWidth > 0 && SurfaceHeight > 0)
        {
            Viewport = Viewport.Centered(card.Bounds, SurfaceWidth, SurfaceHeight);
        }

        // The card may have been outside the culled set until the camera moved.
        RebuildVisible();
        AfterSelectionChanged();
    }

    /// <summary>Makes the left button pick cards - what it does when the canvas opens.</summary>
    [RelayCommand]
    private void UseSelectTool() => Tool = CanvasTool.Select;

    /// <summary>Makes the left button move the camera from anywhere on the surface, cards included.</summary>
    [RelayCommand]
    private void UsePanTool() => Tool = CanvasTool.Pan;

    /// <summary>The topmost card under a world point, or null for empty space.</summary>
    public CanvasNodeViewModel? HitTest(double worldX, double worldY)
    {
        // Reverse order so the card drawn last - the one visibly on top - wins the hit.
        for (var i = VisibleNodes.Count - 1; i >= 0; i--)
        {
            if (VisibleNodes[i].HitTest(worldX, worldY))
            {
                return VisibleNodes[i];
            }
        }

        return null;
    }

    /// <summary>
    /// A click on a card, or on nothing. <paramref name="additive"/> is the Ctrl modifier: it adds
    /// to the selection and takes away from it, which is what every other canvas in the world does.
    /// </summary>
    public void Click(CanvasNodeViewModel? card, bool additive)
    {
        if (card is null)
        {
            if (!additive)
            {
                ClearSelection();
            }

            return;
        }

        if (additive)
        {
            if (!_selected.Remove(card.Id))
            {
                _selected.Add(card.Id);
            }
        }
        else if (!_selected.Contains(card.Id) || _selected.Count > 1)
        {
            // Clicking a card that is already the whole selection leaves it alone, so that the
            // press which begins a drag does not reset what is being dragged.
            _selected.Clear();
            _selected.Add(card.Id);
        }

        AfterSelectionChanged();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        if (_selected.Count == 0)
        {
            return;
        }

        _selected.Clear();
        AfterSelectionChanged();
    }

    /// <summary>
    /// What Escape does: takes the newest thing off the screen, one press at a time.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ClearSelectionCommand"/> on purpose. Clicking empty space has to
    /// clear the selection whether or not the code panel is open, while Escape should close the panel
    /// before it touches the selection - one key doing both jobs would get one of them wrong.
    /// </remarks>
    [RelayCommand]
    private void Dismiss()
    {
        if (Code.IsOpen)
        {
            Code.Close();
            return;
        }

        ClearSelection();
    }

    /// <summary>Starts a rubber band at a screen point.</summary>
    public void BeginMarquee(double screenX, double screenY)
    {
        _marqueeAnchorX = screenX;
        _marqueeAnchorY = screenY;
        MarqueeX = screenX;
        MarqueeY = screenY;
        MarqueeWidth = 0;
        MarqueeHeight = 0;
        IsMarqueeVisible = true;
    }

    public void UpdateMarquee(double screenX, double screenY)
    {
        MarqueeX = Math.Min(_marqueeAnchorX, screenX);
        MarqueeY = Math.Min(_marqueeAnchorY, screenY);
        MarqueeWidth = Math.Abs(screenX - _marqueeAnchorX);
        MarqueeHeight = Math.Abs(screenY - _marqueeAnchorY);
    }

    /// <summary>
    /// Ends the rubber band and selects what it touched. A band smaller than a few pixels is
    /// treated as a click on empty space, so a slightly shaky click still deselects.
    /// </summary>
    public void EndMarquee(bool additive)
    {
        IsMarqueeVisible = false;

        if (MarqueeWidth < 4 && MarqueeHeight < 4)
        {
            if (!additive)
            {
                ClearSelection();
            }

            return;
        }

        var world = CanvasBounds.Between(
            ToWorldX(MarqueeX),
            ToWorldY(MarqueeY),
            ToWorldX(MarqueeX + MarqueeWidth),
            ToWorldY(MarqueeY + MarqueeHeight));

        if (!additive)
        {
            _selected.Clear();
        }

        foreach (var card in _cards.Values)
        {
            // Touched, not enclosed: dragging a band across a column of cards should catch them
            // all without having to reach past the edges of the outermost ones.
            if (card.Bounds.Intersects(world))
            {
                _selected.Add(card.Id);
            }
        }

        AfterSelectionChanged();
    }

    /// <summary>
    /// Captures what a drag will move. Dragging a selected card moves the whole selection;
    /// dragging an unselected one moves just it, and leaves the selection alone.
    /// </summary>
    public void BeginDrag(CanvasNodeViewModel card)
    {
        _dragging.Clear();
        _dragMoved = false;

        if (_selected.Contains(card.Id))
        {
            foreach (var id in _selected)
            {
                if (_cards.TryGetValue(id, out var selected))
                {
                    _dragging.Add(selected);
                }
            }
        }
        else
        {
            _dragging.Add(card);
        }
    }

    /// <summary>Moves the captured cards by a world-space delta.</summary>
    public void DragBy(double worldDeltaX, double worldDeltaY)
    {
        if (_dragging.Count == 0)
        {
            return;
        }

        _dragMoved = true;

        foreach (var card in _dragging)
        {
            card.MoveTo(card.X + worldDeltaX, card.Y + worldDeltaY);
        }

        RefreshLinks(_dragging);
        UpdateAiSurface();
    }

    /// <summary>
    /// Ends the gesture and writes the new positions down. Pinned, because a position someone
    /// chose by hand outranks anything the layout would like to do later.
    /// </summary>
    public async Task EndDragAsync()
    {
        if (_dragging.Count == 0)
        {
            return;
        }

        var moved = _dragMoved;
        var cards = _dragging.ToList();

        _dragging.Clear();
        _dragMoved = false;

        if (moved)
        {
            await SavePlacementsAsync(cards, pinned: true).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Chooses the folder to project. The canvas is where this gesture belongs: without a folder
    /// there is nothing to index, and until now the app only mentioned folders in a settings hint.
    /// </summary>
    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var picked = _dialogs.OpenFolder("Choose the project folder to index");
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        var opened = await _workspace.OpenAsync(picked).ConfigureAwait(true);
        if (!opened.Success)
        {
            // The sandbox's own words: it refuses a folder for reasons worth reading, such as it
            // being the app's own data directory.
            Notice = opened.Error ?? "That folder cannot be used as a project folder.";
            return;
        }

        Notice = null;
        OnPropertyChanged(nameof(IsWorkspaceOpen));
        OnPropertyChanged(nameof(WorkspaceName));

        await IndexWorkspaceAsync().ConfigureAwait(true);
    }

    /// <summary>Walks the open folder and writes what it finds into the graph.</summary>
    [RelayCommand]
    private async Task IndexWorkspaceAsync()
    {
        if (IsIndexing)
        {
            return;
        }

        if (!_workspace.IsOpen)
        {
            // Opening a folder indexes it, so this is a redirect and not a second attempt.
            await OpenFolderAsync().ConfigureAwait(true);
            return;
        }

        var wasEmpty = !HasContent;

        IsIndexing = true;
        Notice = null;
        _indexed = Nothing;
        UpdateGraphStatus();

        try
        {
            // Progress arrives on the UI thread because Progress<T> captures this context, so the
            // count in the status strip climbs while the walk runs.
            var progress = new Progress<GraphIndexProgress>(report =>
            {
                _indexed = report;
                UpdateGraphStatus();
            });

            var result = await _indexer.IndexAsync(progress).ConfigureAwait(true);

            if (!result.Success)
            {
                Notice = result.Error ?? "The project could not be indexed.";
            }
            else if (result.Value is { } report)
            {
                Notice = report.IsTruncated
                    ? $"Indexed the first {report.Nodes} nodes - the project is larger than the current limit."
                    : report.Refused.Count > 0
                        ? $"Indexed {report.Nodes} nodes. {report.Refused.Count} path(s) were skipped."
                        : null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Indexing the workspace failed.");
            Notice = "The project could not be indexed.";
        }
        finally
        {
            IsIndexing = false;
            UpdateGraphStatus();
            OnPropertyChanged(nameof(IsEmpty));
        }

        // The graph raised Changed while the walk ran, so the cards already exist by now; this only
        // points the camera at them.
        if (HasContent)
        {
            FitToContent();

            // A first index placed every card with the arithmetic this build ships, so the surface
            // is already current and the catch-up pass on the next open would have nothing to do.
            // A re-index is not stamped: it only adds to a surface whose revision was settled when
            // the page opened.
            if (wasEmpty)
            {
                await RecordLayoutRevisionAsync().ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// Tidies the cards that nobody has arranged by hand.
    /// </summary>
    /// <remarks>
    /// Pinned placements survive untouched, which is the whole compromise: the button is worth
    /// having on a freshly indexed project, and worth nothing if it throws away the arrangement
    /// someone spent ten minutes on.
    /// </remarks>
    [RelayCommand]
    private async Task AutoLayoutAsync()
    {
        if (!HasContent)
        {
            return;
        }

        await ArrangeAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Recomputes every unpinned position, points the camera at the result, and writes both down.
    /// </summary>
    /// <remarks>
    /// Shared by the "Auto Layout" button and by the one-time pass in <see cref="InitializeAsync"/>
    /// that catches up a surface arranged by an older <see cref="CanvasLayout.Revision"/>. One body
    /// for both, because "tidy this graph" should not mean two slightly different things depending
    /// on who asked.
    /// </remarks>
    private async Task ArrangeAsync()
    {
        foreach (var placement in CanvasLayout.Arrange(_graph.Current, LivePlacements()))
        {
            if (_cards.TryGetValue(placement.NodeId, out var card))
            {
                card.Apply(placement);
            }
        }

        RefreshLinks(_cards.Values);
        RebuildVisible();
        UpdateAiSurface();
        FitToContent();

        await SavePlacementsAsync(_cards.Values, pinned: false).ConfigureAwait(true);
        await RecordLayoutRevisionAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Notes which revision of the arithmetic the stored positions now match.
    /// </summary>
    /// <remarks>
    /// A failure here is not worth a message: the worst it costs is one more arranging pass on the
    /// next open, and the arrangement itself has already been saved.
    /// </remarks>
    private async Task RecordLayoutRevisionAsync()
    {
        if (_settings.Current.Canvas.LayoutRevision == CanvasLayout.Revision)
        {
            return;
        }

        try
        {
            await _settings
                .UpdateAsync<CanvasSettings>(canvas => canvas.LayoutRevision = CanvasLayout.Revision)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The canvas layout revision could not be recorded.");
        }
    }

    /// <summary>
    /// Hands a selection and a question to the existing chat.
    /// </summary>
    /// <remarks>
    /// The action is a string rather than an enum because it comes from a button in markup and
    /// adding a fifth question should not be a type change. Nothing here builds a prompt for a
    /// provider: the graph block is assembled later, inside the ordinary context build.
    /// </remarks>
    [RelayCommand]
    private void AskAi(string? action)
    {
        if (_selected.Count == 0)
        {
            return;
        }

        var selection = GraphSelection.Nodes([.. _selected], _settings.Current.Canvas.ContextDepth);
        AiRequested?.Invoke(this, new CanvasAiRequest(selection, CanvasAiPrompts.For(action), SelectionLabel()));
    }

    /// <summary>Shows the file behind the focused card in the OS file manager.</summary>
    /// <remarks>
    /// The app has no editor of its own, so revealing the file is the honest version of "open
    /// source". The path is rebuilt from the workspace root rather than stored absolute, which
    /// keeps the graph portable between machines.
    /// </remarks>
    [RelayCommand]
    private void OpenSource()
    {
        if (Focused?.Node is not { } node)
        {
            return;
        }

        Notice = SourceLauncher.Reveal(_workspace.Root, node);
    }

    /// <summary>Opens the code behind a card. The double-click on the surface arrives here.</summary>
    public Task OpenCodeAsync(CanvasNodeViewModel? card) =>
        card?.Node is { } node ? ShowCodeAsync(node) : Task.CompletedTask;

    /// <summary>Opens the code behind a node the shell names by id, for the inspector's button.</summary>
    public Task ShowCodeAsync(Guid nodeId) =>
        _cards.TryGetValue(nodeId, out var card) ? OpenCodeAsync(card) : Task.CompletedTask;

    /// <summary>Opens the code behind the focused card - what Enter does.</summary>
    [RelayCommand]
    private Task OpenSelectedCodeAsync() => OpenCodeAsync(Focused);

    /// <summary>
    /// Opens the file behind a node in the panel beside the surface.
    /// </summary>
    /// <remarks>
    /// Both refusals are said out loud in the status strip rather than swallowed. A double-click that
    /// opens nothing and explains nothing is indistinguishable from one the application missed, and
    /// the two cases a person will actually hit - a folder, and a node with no file at all - are not
    /// failures worth a dialog either.
    /// </remarks>
    private async Task ShowCodeAsync(GraphNode node)
    {
        Notice = null;

        if (node.Kind == GraphNodeKind.Folder || node.Kind == GraphNodeKind.Project)
        {
            Notice = "A folder has no code to show.";
            return;
        }

        if (node.Source is null)
        {
            Notice = "This node has no file behind it.";
            return;
        }

        await Code.ShowAsync(node).ConfigureAwait(true);
    }

    /// <summary>How the selection is described in the chat when a question is handed over.</summary>
    private string SelectionLabel()
    {
        if (Focused is { } single)
        {
            return single.Title;
        }

        var relations = RelationCount();
        return relations > 0
            ? $"{_selected.Count} nodes · {relations} relations"
            : $"{_selected.Count} nodes";
    }

    /// <summary>
    /// The graph changed under us - an index run, an applied change set, a reload.
    /// </summary>
    /// <remarks>
    /// The event is raised off the UI thread by contract, so the hop happens here and once.
    /// <c>UiThread.Post</c> runs inline when it is already on the dispatcher, which is why an
    /// indexing run started from a button still finishes with the cards in place.
    /// </remarks>
    private void OnGraphChanged(object? sender, GraphChangedEventArgs e) =>
        UiThread.Post(() =>
        {
            Sync(e.Snapshot);
            UpdateGraphStatus();
            OnPropertyChanged(nameof(IsEmpty));
        });

    private void OnWorkspaceRootChanged(object? sender, string? root) =>
        UiThread.Post(() =>
        {
            // The panel is showing a file from the folder that just closed, and a path relative to a
            // root that no longer exists is worse than nothing.
            Code.Close();

            OnPropertyChanged(nameof(IsWorkspaceOpen));
            OnPropertyChanged(nameof(WorkspaceName));
            UpdateBreadcrumb();
        });

    /// <summary>
    /// Brings the cards in line with a snapshot: update what exists, create what is new, drop what
    /// is gone, then rebuild the links.
    /// </summary>
    /// <remarks>
    /// Updating in place rather than rebuilding is what keeps a selection - and a drag in progress
    /// - alive across an index run that touched something else entirely.
    /// </remarks>
    private void Sync(GraphSnapshot snapshot)
    {
        var stored = _view?.Placements.ToDictionary(p => p.NodeId) ?? [];
        List<CanvasNodeViewModel> newcomers = [];

        foreach (var node in snapshot.Nodes)
        {
            if (_cards.TryGetValue(node.Id, out var card))
            {
                card.Apply(node);
                continue;
            }

            card = new CanvasNodeViewModel(
                node,
                stored.TryGetValue(node.Id, out var placement) ? placement : CanvasPlacement.At(node.Id, 0, 0));

            _cards[node.Id] = card;

            if (!stored.ContainsKey(node.Id))
            {
                newcomers.Add(card);
            }
        }

        foreach (var id in _cards.Keys.Where(id => !snapshot.TryGetNode(id, out _)).ToList())
        {
            _cards.Remove(id);
            _selected.Remove(id);
        }

        if (newcomers.Count > 0)
        {
            PlaceNewcomers(snapshot, newcomers);
        }

        RebuildLinks(snapshot);
        RebuildVisible();
        UpdateBreadcrumb();
        UpdateAiSurface();

        OnPropertyChanged(nameof(NodeCount));
        OnPropertyChanged(nameof(HasContent));
    }

    /// <summary>
    /// Gives cards that have never been positioned a place to stand, and writes it down.
    /// </summary>
    /// <remarks>
    /// The layout is asked only about the nodes it needs to answer for; everything already placed
    /// is handed to it as fixed, so a new file appears in a free slot instead of shuffling the
    /// whole diagram.
    /// </remarks>
    private void PlaceNewcomers(GraphSnapshot snapshot, List<CanvasNodeViewModel> newcomers)
    {
        var pending = newcomers.Select(card => card.Id).ToHashSet();

        var settled = _cards.Values
            .Where(card => !pending.Contains(card.Id))
            .Select(card => card.ToPlacement(false))
            .ToList();

        foreach (var placement in CanvasLayout.PlaceMissing(snapshot, settled))
        {
            if (_cards.TryGetValue(placement.NodeId, out var card))
            {
                card.Apply(placement);
            }
        }

        SaveQuietly(newcomers.Select(card => card.ToPlacement(false)).ToList());
    }

    /// <summary>Rebuilds the link objects and the node-to-link index, then redraws every curve.</summary>
    private void RebuildLinks(GraphSnapshot snapshot)
    {
        HashSet<Guid> live = [];

        foreach (var edge in snapshot.Edges)
        {
            // An edge to a node that is not on the canvas has nothing to join. This happens
            // legitimately while a change set is being applied in pieces.
            if (!_cards.TryGetValue(edge.FromId, out var from) || !_cards.TryGetValue(edge.ToId, out var to))
            {
                continue;
            }

            live.Add(edge.Id);

            if (_links.TryGetValue(edge.Id, out var link))
            {
                link.Apply(edge);
            }
            else
            {
                _links[edge.Id] = new CanvasEdgeViewModel(edge, from, to);
            }
        }

        foreach (var id in _links.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _links.Remove(id);
        }

        _linksByNode.Clear();

        foreach (var link in _links.Values)
        {
            Index(link.From.Id, link);
            Index(link.To.Id, link);
            link.Refresh();
        }

        void Index(Guid nodeId, CanvasEdgeViewModel link)
        {
            if (!_linksByNode.TryGetValue(nodeId, out var list))
            {
                list = [];
                _linksByNode[nodeId] = list;
            }

            list.Add(link);
        }
    }

    /// <summary>Redraws only the curves attached to the given cards - what a drag needs.</summary>
    private void RefreshLinks(IEnumerable<CanvasNodeViewModel> cards)
    {
        foreach (var card in cards)
        {
            if (_linksByNode.TryGetValue(card.Id, out var links))
            {
                foreach (var link in links)
                {
                    link.Refresh();
                }
            }
        }
    }

    /// <summary>
    /// Decides which cards and curves are worth having as live elements.
    /// </summary>
    /// <remarks>
    /// A canvas panel cannot virtualise - it has no rows to recycle - so the culling happens here
    /// instead, and the collections the view binds to hold only what is near the camera. Below
    /// <see cref="CullThreshold"/> nodes the whole graph stays realised, because the diff would
    /// cost more than the elements it saves and panning should never make cards blink.
    /// </remarks>
    private void RebuildVisible()
    {
        HashSet<Guid> keepNodes;

        if (_cards.Count <= CullThreshold || SurfaceWidth <= 0 || SurfaceHeight <= 0)
        {
            keepNodes = [.. _cards.Keys];
        }
        else
        {
            var world = Viewport.VisibleWorld(SurfaceWidth, SurfaceHeight);
            var padded = new CanvasBounds(
                world.X - CullMargin,
                world.Y - CullMargin,
                world.Width + (2 * CullMargin),
                world.Height + (2 * CullMargin));

            var cap = Math.Max(50, _settings.Current.Canvas.MaxVisibleNodes);

            keepNodes = [.. _cards.Values
                .Where(card => card.Bounds.Intersects(padded))
                .Take(cap)
                .Select(card => card.Id)];
        }

        Reconcile(VisibleNodes, _shownNodes, keepNodes, id => _cards.GetValueOrDefault(id));

        // A curve is kept when either end is on screen: half a line leading off the edge is how a
        // person sees that there is more graph in that direction.
        HashSet<Guid> keepLinks = [.. _links.Values
            .Where(link => keepNodes.Contains(link.From.Id) || keepNodes.Contains(link.To.Id))
            .Select(link => link.Id)];

        Reconcile(VisibleEdges, _shownLinks, keepLinks, id => _links.GetValueOrDefault(id));
    }

    /// <summary>
    /// Brings a bound collection in line with a set of ids by adding and removing, never by
    /// clearing: a clear would drop every realised element and make the surface flash.
    /// </summary>
    private static void Reconcile<T>(
        ObservableCollection<T> shown,
        HashSet<Guid> shownIds,
        HashSet<Guid> keep,
        Func<Guid, T?> resolve)
        where T : class
    {
        if (shownIds.SetEquals(keep))
        {
            return;
        }

        for (var i = shown.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(IdOf(shown[i])))
            {
                shown.RemoveAt(i);
            }
        }

        foreach (var id in keep)
        {
            if (!shownIds.Contains(id) && resolve(id) is { } item)
            {
                shown.Add(item);
            }
        }

        shownIds.Clear();

        foreach (var item in shown)
        {
            shownIds.Add(IdOf(item));
        }

        static Guid IdOf(T item) => item switch
        {
            CanvasNodeViewModel card => card.Id,
            CanvasEdgeViewModel link => link.Id,
            _ => Guid.Empty,
        };
    }

    /// <summary>
    /// One place where everything downstream of a selection is brought up to date: the cards'
    /// own flags, the highlighted curves, the status line, the breadcrumb, the AI surface, and the
    /// inspector at the far end of an event.
    /// </summary>
    private void AfterSelectionChanged()
    {
        foreach (var card in _cards.Values)
        {
            card.IsSelected = _selected.Contains(card.Id);
        }

        foreach (var link in _links.Values)
        {
            link.IsHighlighted = _selected.Contains(link.From.Id) || _selected.Contains(link.To.Id);
        }

        var relations = RelationCount();

        SelectionStatus = _selected.Count switch
        {
            0 => string.Empty,
            1 => Focused?.KindLabel ?? "1 node",
            _ => $"{_selected.Count} nodes · {relations} relations",
        };

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSingleSelection));
        OnPropertyChanged(nameof(IsMultiSelection));
        OnPropertyChanged(nameof(Focused));

        UpdateBreadcrumb();
        UpdateAiSurface();

        SelectionChanged?.Invoke(this, new CanvasSelection(
            [.. _selected],
            Focused?.Node,
            relations));
    }

    /// <summary>
    /// How many relations live inside the selection. Edges with one end outside are not counted:
    /// "3 nodes · 4 relations" should describe the thing being asked about, not its surroundings.
    /// </summary>
    private int RelationCount() =>
        _links.Values.Count(link => _selected.Contains(link.From.Id) && _selected.Contains(link.To.Id));

    /// <summary>
    /// Parks the AI surface just above the selection, in screen coordinates, and keeps it on
    /// screen when the selection is half off it.
    /// </summary>
    private void UpdateAiSurface()
    {
        var bounds = SelectionBounds();

        if (bounds.IsEmpty || SurfaceWidth <= 0)
        {
            IsAiSurfaceVisible = false;
            return;
        }

        const double width = 300;
        const double gap = 14;

        var centre = ToScreenX(bounds.CenterX);
        var top = ToScreenY(bounds.Top);

        AiSurfaceX = Math.Clamp(centre - (width / 2), 12, Math.Max(12, SurfaceWidth - width - 12));
        AiSurfaceY = Math.Max(12, top - gap - (IsMultiSelection ? 92 : 40));
        AiSurfaceHeader = SelectionLabel();
        IsAiSurfaceVisible = true;
    }

    /// <summary>The counts in the bottom strip, and the one line that says work is happening.</summary>
    private void UpdateGraphStatus()
    {
        if (IsIndexing)
        {
            GraphStatus = _indexed.Nodes > 0
                ? $"Indexing project…   {_indexed.Nodes} nodes"
                : "Indexing project…";
            return;
        }

        var nodes = _cards.Count;
        var relations = _links.Count;

        GraphStatus = nodes == 0
            ? "No graph yet"
            : $"{nodes} nodes · {relations} relations";
    }

    /// <summary>
    /// The path through the graph to whatever is selected - "Project / src / Auth".
    /// </summary>
    /// <remarks>
    /// Walked with <c>Parent</c> on the snapshot, so it follows containment in the graph. It agrees
    /// with the folder tree today only because the only indexer so far is a folder walk; when a
    /// class node arrives, its parent is the file that declares it and the crumb says so.
    /// </remarks>
    private void UpdateBreadcrumb()
    {
        var project = _workspace.IsOpen ? WorkspaceName : "Project";

        if (Focused is not { } card)
        {
            Breadcrumb = project;
            return;
        }

        List<string> crumbs = [];
        var snapshot = _graph.Current;
        var current = card.Node;

        while (current is not null && crumbs.Count < 5)
        {
            crumbs.Insert(0, string.IsNullOrWhiteSpace(current.Title) ? current.Key : current.Title);
            current = snapshot.Parent(current.Id);
        }

        // The graph's own root is usually the folder that was indexed, so prepending the project
        // name again would say it twice.
        if (crumbs.Count > 0 && string.Equals(crumbs[0], project, StringComparison.OrdinalIgnoreCase))
        {
            Breadcrumb = string.Join("  /  ", crumbs);
            return;
        }

        Breadcrumb = string.Join("  /  ", crumbs.Prepend(project));
    }

    private CanvasBounds ContentBounds() => CanvasBounds.Around(_cards.Values.Select(card => card.Bounds));

    private CanvasBounds SelectionBounds() => CanvasBounds.Around(
        _selected.Select(id => _cards.GetValueOrDefault(id)).OfType<CanvasNodeViewModel>().Select(card => card.Bounds));

    /// <summary>Where every card is right now - the input the layout treats as fixed.</summary>
    private List<CanvasPlacement> LivePlacements() =>
        [.. _cards.Values.Select(card => card.ToPlacement(false))];

    private async Task SavePlacementsAsync(IEnumerable<CanvasNodeViewModel> cards, bool pinned)
    {
        if (_view is null)
        {
            return;
        }

        try
        {
            await _store
                .SavePlacementsAsync(_view.Id, cards.Select(card => card.ToPlacement(pinned)), CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A lost position is a cosmetic loss and never worth interrupting anyone over; the
            // graph itself was not involved in this write at all.
            _logger.LogWarning(ex, "Canvas placements could not be saved.");
        }
    }

    /// <summary>Persists placements produced by the layout, without making the caller async.</summary>
    private void SaveQuietly(List<CanvasPlacement> placements)
    {
        if (_view is null || placements.Count == 0)
        {
            return;
        }

        var viewId = _view.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                await _store.SavePlacementsAsync(viewId, placements, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Canvas placements could not be saved.");
            }
        });
    }

    /// <summary>
    /// Writes the camera down once it stops moving. Each change cancels the previous wait, so a
    /// long pan costs one write instead of hundreds.
    /// </summary>
    private void ScheduleViewportSave()
    {
        if (_view is null)
        {
            return;
        }

        _viewportSave?.Cancel();
        _viewportSave?.Dispose();

        var source = new CancellationTokenSource();
        _viewportSave = source;

        var viewId = _view.Id;
        var viewport = Viewport;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ViewportSaveDelay, source.Token).ConfigureAwait(false);
                await _store.SaveViewportAsync(viewId, viewport, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later move. Nothing to report.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The canvas viewport could not be saved.");
            }
        });
    }

    partial void OnViewportChanged(CanvasViewport value)
    {
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(PanX));
        OnPropertyChanged(nameof(PanY));
        OnPropertyChanged(nameof(ZoomText));

        RebuildVisible();
        UpdateAiSurface();
        ScheduleViewportSave();
    }

    partial void OnSurfaceWidthChanged(double value) => RebuildVisible();

    partial void OnSurfaceHeightChanged(double value) => RebuildVisible();

    partial void OnIsIndexingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnIsLoadedChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

}

/// <summary>
/// A question about a selection, on its way to the existing chat.
/// </summary>
/// <param name="Selection">Ids and a depth - never geometry, and never inlined file text.</param>
/// <param name="Prompt">The question, or empty when the person is going to type their own.</param>
/// <param name="Label">How to describe the selection in the chat's notice bar.</param>
public sealed record CanvasAiRequest(GraphSelection Selection, string Prompt, string Label);

/// <summary>
/// What is selected on the canvas, for the inspector at the other end of the event.
/// </summary>
/// <param name="NodeIds">Everything selected.</param>
/// <param name="Node">The node to show in detail, when exactly one is selected.</param>
/// <param name="RelationCount">Relations wholly inside the selection.</param>
public sealed record CanvasSelection(IReadOnlyList<Guid> NodeIds, GraphNode? Node, int RelationCount);
