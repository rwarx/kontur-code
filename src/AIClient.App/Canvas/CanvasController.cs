using System.Windows;
using System.Windows.Media;
using AIClient.Domain.Graph;

namespace AIClient.App.Canvas;

/// <summary>
/// The canvas's interaction state: viewport, selection, hover, drag previews - and the
/// contract <see cref="GraphCanvas"/> drives.
/// </summary>
/// <remarks>
/// <para>
/// This class is deliberately free of XAML concerns (no commands, no bindable properties):
/// it is the piece of the canvas that both sides can see. The ViewModel owns it, mirrors
/// the parts the interface needs to display, and supplies the graph-writing actions
/// through <see cref="IGraphAccess"/>; the canvas element reports gestures into it and
/// follows the state it publishes. That split is what keeps mode switches lossless - the
/// viewport and the selection outlive the visual that drew them.
/// </para>
/// <para>
/// Drag previews are the one stateful subtlety: while the user drags, positions move in
/// preview space (per-node offsets) and nothing is written to the graph until release.
/// The scene keeps drawing from the snapshot; the canvas adds preview offsets when
/// composing transforms, so a cancelled drag (Escape mid-drag, a lost capture) simply
/// forgets the offsets and everything snaps back.
/// </para>
/// </remarks>
public sealed class CanvasController
{
    /// <summary>Graph writes the controller asks its owner to perform.</summary>
    /// <remarks>The controller never touches the graph service: routing writes through the
    /// owner keeps "AI proposes, user commits" and "user drags, timeline remembers" on one
    /// path, rather than giving the input layer a second pen.</remarks>
    public interface IGraphAccess
    {
        /// <summary>Move nodes to their released positions - one change set, one timeline entry.</summary>
        void CommitNodeMoves(IReadOnlyDictionary<string, Point> positions);

        /// <summary>Remove nodes (and, by the model's rule, their edges).</summary>
        void DeleteNodes(IReadOnlyCollection<string> nodeIds);
    }

    public const double MinZoom = 0.06;
    public const double MaxZoom = 3.5;

    private readonly IGraphAccess _access;
    private readonly HashSet<string> _selectedNodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _previewSelectedNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector> _previewOffsets = new(StringComparer.Ordinal);

    public CanvasController(IGraphAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        _access = access;
    }

    public GraphSnapshot Snapshot { get; private set; } = GraphSnapshot.Empty;

    public double Zoom { get; private set; } = 1;

    /// <summary>World coordinate under the viewport's top-left corner.</summary>
    public Vector Offset { get; private set; }

    public string? HoverNodeId { get; private set; }

    public string? SelectedEdgeId { get; private set; }

    public string? PrimarySelectedNodeId { get; private set; }

    public IReadOnlyCollection<string> SelectedNodeIds => _selectedNodes;

    /// <summary>Selection preview while a marquee is being dragged.</summary>
    public IReadOnlyCollection<string> PreviewSelectedNodeIds => _previewSelectedNodes;

    public event EventHandler? ViewportChanged;

    public event EventHandler? StateChanged;

    /// <summary>Raised with the delta to apply, or with a reset when history is discontinuous (load, restore, undo).</summary>
    public event EventHandler<SceneChangedEventArgs>? SceneChanged;

    /// <summary>A gesture the hosting view should act on (inspect, fit, ask).</summary>
    public event EventHandler<GestureEventArgs>? GestureReceived;

    public event EventHandler<MarqueeEventArgs>? MarqueeChanged;

    // ------------------------------------------------------------- snapshot

    /// <summary>Replaces the snapshot, publishing a diff when one is worth computing.</summary>
    public void SetSnapshot(GraphSnapshot snapshot)
    {
        // Discontinuity (undo, load, restore) is judged before the swap: once Snapshot
        // points at the new version, the old one is gone and the question is unanswerable.
        var isReset = snapshot.Version < Snapshot.Version;
        var delta = GraphProjection.Diff(Snapshot, snapshot);

        Snapshot = snapshot;

        // Selections that point at deleted nodes are dropped here, not left for every
        // consumer to notice: one place, one rule.
        PruneSelection();

        SceneChanged?.Invoke(this, new SceneChangedEventArgs(snapshot, delta, isReset));
    }

    // ------------------------------------------------------------- viewport

    public Matrix View => new(
        Zoom, 0, 0, Zoom,
        -Offset.X * Zoom,
        -Offset.Y * Zoom);

    public Point ScreenToWorld(Point screen) => new(
        screen.X / Zoom + Offset.X,
        screen.Y / Zoom + Offset.Y);

    /// <summary>The canvas's last reported size, so the minimap can draw an honest viewport box.</summary>
    public Size LastViewportSize { get; private set; }

    public Rect VisibleWorldRect(Size viewportSize)
    {
        LastViewportSize = viewportSize;

        return new Rect(
            Offset.X,
            Offset.Y,
            viewportSize.Width / Zoom,
            viewportSize.Height / Zoom);
    }

    public void SetViewport(double zoom, double offsetX, double offsetY)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);

        if (Math.Abs(zoom - Zoom) < 0.0001
            && Math.Abs(offsetX - Offset.X) < 0.01
            && Math.Abs(offsetY - Offset.Y) < 0.01)
        {
            return;
        }

        Zoom = zoom;
        Offset = new Vector(offsetX, offsetY);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Zooms by a factor while keeping the world point under a screen point fixed.</summary>
    public void ZoomAt(Point screenPoint, double factor)
    {
        var world = ScreenToWorld(screenPoint);
        var zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);

        Offset = new Vector(world.X - screenPoint.X / zoom, world.Y - screenPoint.Y / zoom);
        Zoom = zoom;
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PanBy(Vector screenDelta)
    {
        Offset -= new Vector(screenDelta.X / Zoom, screenDelta.Y / Zoom);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pure math for "fit this rectangle": the viewport that centers content with padding.</summary>
    public (double Zoom, double OffsetX, double OffsetY) ComputeFit(Rect content, Size viewport, double padding = 80)
    {
        if (content.IsEmpty || viewport.Width < 1 || viewport.Height < 1)
        {
            return (Zoom, Offset.X, Offset.Y);
        }

        var zoom = Math.Min(
            (viewport.Width - padding * 2) / Math.Max(content.Width, 1),
            (viewport.Height - padding * 2) / Math.Max(content.Height, 1));

        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);

        var offsetX = content.Left + content.Width / 2 - viewport.Width / (2 * zoom);
        var offsetY = content.Top + content.Height / 2 - viewport.Height / (2 * zoom);

        return (zoom, offsetX, offsetY);
    }

    // ----------------------------------------------------------- selection

    public void SetSelection(SelectionMode mode, string nodeId)
    {
        switch (mode)
        {
            case SelectionMode.Replace:
                _selectedNodes.Clear();
                _selectedNodes.Add(nodeId);
                PrimarySelectedNodeId = nodeId;
                break;

            case SelectionMode.Toggle:
                if (!_selectedNodes.Remove(nodeId))
                {
                    _selectedNodes.Add(nodeId);
                    PrimarySelectedNodeId = nodeId;
                }
                else if (PrimarySelectedNodeId == nodeId)
                {
                    PrimarySelectedNodeId = _selectedNodes.FirstOrDefault();
                }

                break;

            case SelectionMode.Add:
                _selectedNodes.Add(nodeId);
                PrimarySelectedNodeId = nodeId;
                break;
        }

        SelectedEdgeId = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectEdge(string edgeId)
    {
        SelectedEdgeId = edgeId;
        _selectedNodes.Clear();
        PrimarySelectedNodeId = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectNodes(IReadOnlyCollection<string> nodeIds, bool additive)
    {
        if (!additive)
        {
            _selectedNodes.Clear();
        }

        foreach (var id in nodeIds)
        {
            _selectedNodes.Add(id);
        }

        PrimarySelectedNodeId = _selectedNodes.FirstOrDefault();
        SelectedEdgeId = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectInRect(Rect worldRect)
    {
        var hits = Snapshot.Nodes
            .Where(node => worldRect.Contains(node.X, node.Y))
            .Select(node => node.Id)
            .ToArray();

        SelectNodes(hits, additive: false);
    }

    public void ClearSelection()
    {
        if (_selectedNodes.Count == 0 && PrimarySelectedNodeId is null)
        {
            return;
        }

        _selectedNodes.Clear();
        PrimarySelectedNodeId = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearEdgeSelection()
    {
        if (SelectedEdgeId is null)
        {
            return;
        }

        SelectedEdgeId = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetHover(string? nodeId)
    {
        if (HoverNodeId == nodeId)
        {
            return;
        }

        HoverNodeId = nodeId;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetLiveMarquee(Point? worldTopLeft, Point? worldBottomRight)
    {
        _previewSelectedNodes.Clear();

        if (worldTopLeft is { } topLeft && worldBottomRight is { } bottomRight)
        {
            var rect = new Rect(topLeft, bottomRight);

            foreach (var node in Snapshot.Nodes)
            {
                if (rect.Contains(node.X, node.Y))
                {
                    _previewSelectedNodes.Add(node.Id);
                }
            }
        }

        MarqueeChanged?.Invoke(this, new MarqueeEventArgs(_previewSelectedNodes.Count));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- drag

    /// <summary>Preview offset for a node while it is being dragged, or zero.</summary>
    public Vector PreviewOffset(string nodeId) =>
        _previewOffsets.TryGetValue(nodeId, out var offset) ? offset : default;

    /// <summary>Moves the selection by a world delta without committing anything.</summary>
    public void NudgeSelection(Vector worldDelta, bool previewOnly)
    {
        if (_selectedNodes.Count == 0)
        {
            return;
        }

        foreach (var id in _selectedNodes)
        {
            _previewOffsets.TryGetValue(id, out var current);
            _previewOffsets[id] = current + worldDelta;
        }

        if (!previewOnly)
        {
            CommitNudge();
        }
    }

    /// <summary>Writes the preview offsets into the graph as one move change set.</summary>
    public void CommitNudge()
    {
        if (_previewOffsets.Count == 0)
        {
            return;
        }

        var positions = new Dictionary<string, Point>(_previewOffsets.Count);

        foreach (var (id, offset) in _previewOffsets)
        {
            if (Snapshot.TryGetNode(id, out var node))
            {
                positions[id] = new Point(node.X + offset.X, node.Y + offset.Y);
            }
        }

        _previewOffsets.Clear();
        _access.CommitNodeMoves(positions);
    }

    /// <summary>Forgets a drag without writing anything (lost capture, Escape).</summary>
    public void CancelNudge()
    {
        if (_previewOffsets.Count == 0)
        {
            return;
        }

        _previewOffsets.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteSelection()
    {
        if (_selectedNodes.Count == 0)
        {
            return;
        }

        var doomed = _selectedNodes.ToArray();
        _selectedNodes.Clear();
        PrimarySelectedNodeId = null;
        _access.DeleteNodes(doomed);
    }

    // ------------------------------------------------------------- gestures

    public void NotifyGesture(GraphCanvas.GestureKind kind, string? nodeId)
    {
        GestureReceived?.Invoke(this, new GestureEventArgs(kind, nodeId));
    }

    private void PruneSelection()
    {
        var stale = _selectedNodes.Where(id => !Snapshot.TryGetNode(id, out _)).ToArray();

        foreach (var id in stale)
        {
            _selectedNodes.Remove(id);
        }

        if (PrimarySelectedNodeId is { } primary && !Snapshot.TryGetNode(primary, out _))
        {
            PrimarySelectedNodeId = _selectedNodes.FirstOrDefault();
        }

        if (SelectedEdgeId is { } edge && !Snapshot.TryGetEdge(edge, out _))
        {
            SelectedEdgeId = null;
        }
    }
}

public enum SelectionMode
{
    Replace,
    Toggle,
    Add,
}

public sealed class SceneChangedEventArgs(GraphSnapshot snapshot, GraphProjection.Delta delta, bool isReset) : EventArgs
{
    public GraphSnapshot Snapshot { get; } = snapshot;

    public GraphProjection.Delta Delta { get; } = delta;

    public bool IsReset { get; } = isReset;
}

public sealed class GestureEventArgs(GraphCanvas.GestureKind kind, string? nodeId) : EventArgs
{
    public GraphCanvas.GestureKind Kind { get; } = kind;

    public string? NodeId { get; } = nodeId;
}

public sealed class MarqueeEventArgs(int previewCount) : EventArgs
{
    public int PreviewCount { get; } = previewCount;
}
