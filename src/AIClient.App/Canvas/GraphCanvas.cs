using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIClient.Domain.Graph;

namespace AIClient.App.Canvas;

/// <summary>
/// The graph canvas: a <see cref="FrameworkElement"/> that draws the scene through retained
/// visuals and owns every pointer interaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not ItemsControl.</b> One visual per node through an items host means one
/// measure/arrange pass, one element and one template per node - which at graph scale is
/// tens of thousands of layout objects. This element draws nodes as <see cref="DrawingVisual"/>s
/// under a single scale/translate root, culls to the viewport, and moves nodes by writing
/// transforms. The trade is that nothing here is styleable in XAML; that is accepted
/// deliberately, because a canvas is a rendering problem more than a layout problem.
/// </para>
/// <para>
/// <b>What the ViewModel owns.</b> Viewport (zoom, offset), selection and hover all live in
/// the <see cref="CanvasController"/> handed to this element; the element reports gestures
/// to it and follows the state it publishes back. Mode switches preserve everything for
/// free, because the state never lived in the view.
/// </para>
/// <para>
/// <b>Input model.</b> Left-drag on empty space is a marquee; drag on a node drags the
/// selection. Pan is middle-drag, space-drag, or drag with the left button while the
/// hand tool is engaged; the wheel zooms to the cursor. This split keeps selection and
/// navigation from fighting over the same gesture, which is the classic canvas annoyance.
/// </para>
/// </remarks>
public class GraphCanvas : FrameworkElement
{
    /// <summary>Interaction gestures the canvas reports to the controller.</summary>
    public enum GestureKind
    {
        /// <summary>A node was double-clicked: open it in the inspector.</summary>
        NodeActivated,
        /// <summary>Empty canvas was double-clicked: fit content.</summary>
        BackgroundDoubleClicked,
        /// <summary>The selection changed through a gesture rather than a command.</summary>
        SelectionChanged,
        /// <summary>Nodes were dragged and released: commit the move to the graph.</summary>
        MoveCommitted,
        /// <summary>Ask AI about the current selection.</summary>
        AskAiRequested,
    }

    private const double MinZoom = 0.06;
    private const double MaxZoom = 3.5;
    private const double WheelZoomStep = 1.12;
    private const double DragThreshold = 3.0;

    private CanvasController? _controller;
    private readonly CanvasScene _scene = new();
    private readonly ContainerVisual _content = new();
    private readonly DrawingVisual _gridVisual = new();
    private readonly DrawingVisual _overlayVisual = new();
    private readonly HashSet<string> _attachedNodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _attachedEdges = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visibleEdgeIds = new(StringComparer.Ordinal);

    private Point _pointerDownPosition;
    private Point _pointerLastPosition;
    private Point _marqueeStart;
    private bool _isMarqueeActive;
    private bool _isNodeDragActive;
    private bool _isPanActive;
    private bool _isSpacePanning;
    private bool _dragMoved;
    private Rect _currentMarquee;

    public GraphCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        SnapsToDevicePixels = false;
        Cursor = Cursors.Arrow;

        AddVisualChild(_gridVisual);
        AddVisualChild(_content);
        AddVisualChild(_overlayVisual);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;

        // Panning while the space key is held is a mode, not a shortcut: it has to hold
        // for as long as the key does, so the canvas watches the key itself.
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
    }

    /// <summary>Wires the controller whose state this canvas renders and reports to.</summary>
    public void SetController(CanvasController controller)
    {
        if (_controller is not null)
        {
            _controller.ViewportChanged -= OnViewportChanged;
            _controller.StateChanged -= OnStateChanged;
            _controller.SceneChanged -= OnSceneChanged;
        }

        _controller = controller;

        _controller.ViewportChanged += OnViewportChanged;
        _controller.StateChanged += OnStateChanged;
        _controller.SceneChanged += OnSceneChanged;

        ApplyViewportTransform();
        RefreshPalette();

        // First attach is a reset by definition: the scene has never seen this graph.
        _scene.Reset(_controller.Snapshot);
        _attachedNodes.Clear();
        _attachedEdges.Clear();
        _visibleEdgeIds.Clear();
        SynchronizeCulling();
        ApplySelectionStates();
        RenderVisible();
    }

    protected override int VisualChildrenCount => 3;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _gridVisual,
        1 => _content,
        2 => _overlayVisual,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    // -------------------------------------------------------------- viewport

    private void OnViewportChanged(object? sender, EventArgs e)
    {
        ApplyViewportTransform();
        RenderGrid();
        SynchronizeCulling();
        RenderOverlay();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Selection/hover changes re-render only the visuals whose state changed; the
        // controller has already marked them dirty through the scene.
        ApplySelectionStates();
        RenderVisible();
        RenderOverlay();
    }

    private void OnSceneChanged(object? sender, SceneChangedEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        if (e.IsReset)
        {
            _scene.Reset(e.Snapshot);
            _attachedNodes.Clear();
            _attachedEdges.Clear();
            _visibleEdgeIds.Clear();
        }
        else
        {
            _scene.Apply(e.Delta, e.Snapshot);
        }

        SynchronizeCulling();
        ApplySelectionStates();
        RenderVisible();
    }

    private void ApplyViewportTransform()
    {
        if (_controller is null)
        {
            return;
        }

        var view = _controller.View;
        _content.Transform = new MatrixTransform(view);
    }

    // ------------------------------------------------------------ grid layer

    /// <summary>
    /// The grid is drawn in screen space: it must stay pixel-aligned whatever the zoom,
    /// and panning it is a translation of the drawing, not a redraw of the world.
    /// </summary>
    private void RenderGrid()
    {
        if (_controller is null)
        {
            return;
        }

        var view = _controller.View;
        var size = RenderSize;

        if (size.Width < 1 || size.Height < 1)
        {
            return;
        }

        using var context = _gridVisual.RenderOpen();

        context.DrawRectangle(Brush("Brush.CanvasBackground") ?? Brushes.Transparent, null, new Rect(0, 0, size.Width, size.Height));

        // Subtle radial wash: the canvas reads as a lit surface rather than a flat void,
        // without a gradient per node. One brush, centred on the viewport.
        var centre = new Point(size.Width / 2, size.Height / 2);
        var radius = Math.Max(size.Width, size.Height) * 0.75;

        var wash = new RadialGradientBrush(
            Color.FromArgb(16, 56, 201, 165),
            Color.FromArgb(0, 56, 201, 165))
        {
            Center = centre,
            GradientOrigin = centre,
            RadiusX = radius,
            RadiusY = radius,
        };

        context.DrawRectangle(wash, null, new Rect(0, 0, size.Width, size.Height));

        // Dots at the minor grid spacing; spacing doubles while it would fall under ~10
        // screen pixels, so zooming out never produces a moiré field.
        var zoom = _controller.Zoom;
        var spacing = 24.0 * zoom;

        while (spacing < 10)
        {
            spacing *= 2;
        }

        var dotBrush = Brush("Brush.CanvasGrid") ?? Brushes.Gray;
        var origin = new Point(
            (-view.OffsetX * zoom) % spacing,
            (-view.OffsetY * zoom) % spacing);

        if (origin.X < 0)
        {
            origin.X += spacing;
        }

        if (origin.Y < 0)
        {
            origin.Y += spacing;
        }

        var dot = new Rect(0, 0, 1, 1);

        for (var x = origin.X; x < size.Width; x += spacing)
        {
            for (var y = origin.Y; y < size.Height; y += spacing)
            {
                dot.X = x;
                dot.Y = y;
                context.DrawRectangle(dotBrush, null, dot);
            }
        }
    }

    private Brush? Brush(string key) => TryFindResource(key) as Brush;

    // ------------------------------------------------------------- culling

    private void SynchronizeCulling()
    {
        if (_controller is null)
        {
            return;
        }

        var worldRect = _controller.VisibleWorldRect(RenderSize);
        var nodeIds = _scene.Index.Query(worldRect);

        var wantedNodes = new HashSet<string>(nodeIds, StringComparer.Ordinal);

        // Remove first: the VisualCollection churns less when additions outnumber removals.
        foreach (var id in _attachedNodes.Where(id => !wantedNodes.Contains(id)).ToArray())
        {
            if (_scene.FindNode(id) is { IsAttached: true } visual)
            {
                _content.Children.Remove(visual.Visual);
                visual.IsAttached = false;
            }

            _attachedNodes.Remove(id);
        }

        foreach (var id in wantedNodes)
        {
            if (_attachedNodes.Contains(id))
            {
                continue;
            }

            if (_scene.FindNode(id) is { IsAttached: false } visual)
            {
                _content.Children.Add(visual.Visual);
                visual.IsAttached = true;
                _attachedNodes.Add(id);
            }
        }

        // Edges are culled by the union rectangle of their endpoints rather than their
        // curve bounds: the curve can leave the segment's hull, and pulling one extra
        // edge into the tree is cheaper than computing a bezier hull per edge.
        _visibleEdgeIds.Clear();

        var candidates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edgeId in _scene.EdgeIds())
        {
            if (_scene.FindEdge(edgeId) is not { } edge)
            {
                continue;
            }

            var sourceRect = _scene.NodeBounds(edge.SourceId);
            var targetRect = _scene.NodeBounds(edge.TargetId);

            if (sourceRect.IsEmpty || targetRect.IsEmpty)
            {
                continue;
            }

            var hull = Rect.Union(sourceRect, targetRect);

            if (hull.IntersectsWith(worldRect))
            {
                candidates.Add(edgeId);
            }
        }

        foreach (var id in _attachedEdges.Where(id => !candidates.Contains(id)).ToArray())
        {
            if (_scene.FindEdge(id) is { IsAttached: true } visual)
            {
                _content.Children.Remove(visual.Visual);
                visual.IsAttached = false;
            }

            _attachedEdges.Remove(id);
        }

        foreach (var id in candidates)
        {
            if (_attachedEdges.Contains(id))
            {
                continue;
            }

            if (_scene.FindEdge(id) is { IsAttached: false } visual)
            {
                _content.Children.Add(visual.Visual);
                visual.IsAttached = true;
                _attachedEdges.Add(id);
                _visibleEdgeIds.Add(id);
            }
            else if (_scene.FindEdge(id) is { IsAttached: true })
            {
                _visibleEdgeIds.Add(id);
            }
        }
    }

    private void RenderVisible()
    {
        if (_controller is null)
        {
            return;
        }

        var zoom = _controller.Zoom;

        foreach (var id in _attachedNodes)
        {
            if (_scene.FindNode(id) is { } visual)
            {
                _scene.RenderNode(visual, zoom);
            }
        }

        foreach (var id in _attachedEdges)
        {
            if (_scene.FindEdge(id) is { } visual)
            {
                _scene.RenderEdge(visual, _controller.Snapshot, zoom);
            }
        }
    }

    private void ApplySelectionStates()
    {
        if (_controller is null)
        {
            return;
        }

        var selection = _controller.SelectedNodeIds;
        var hasSelection = selection.Count > 0;
        var hover = _controller.HoverNodeId;
        var relatedEdges = new HashSet<string>(StringComparer.Ordinal);

        if (hasSelection)
        {
            foreach (var edge in _scene.EdgesIncidentTo(selection))
            {
                relatedEdges.Add(edge);
            }
        }

        foreach (var visual in _scene.AllNodeVisuals())
        {
            var state = NodeRenderState.Default;

            if (selection.Contains(visual.Node.Id))
            {
                state |= NodeRenderState.Selected;
            }
            else if (hasSelection)
            {
                state |= NodeRenderState.Dimmed;
            }

            if (hover == visual.Node.Id)
            {
                // Hover beats dimming visually: the hovered node is where the eye is.
                state &= ~NodeRenderState.Dimmed;
                state |= NodeRenderState.Hovered;
            }

            if (visual.State != state)
            {
                visual.State = state;
                visual.IsDirty = true;
            }
        }

        foreach (var visual in _scene.AllEdgeVisuals())
        {
            var state = EdgeRenderState.Default;

            if (_controller.SelectedEdgeId == visual.Edge.Id)
            {
                state |= EdgeRenderState.Selected;
            }
            else if (relatedEdges.Contains(visual.Edge.Id))
            {
                state |= EdgeRenderState.Related;
            }
            else if (hasSelection)
            {
                state |= EdgeRenderState.Dimmed;
            }

            if (visual.State != state)
            {
                visual.State = state;
                visual.IsDirty = true;
            }
        }
    }

    // ------------------------------------------------------------- overlay

    private void RenderOverlay()
    {
        using var context = _overlayVisual.RenderOpen();

        if (_controller is null)
        {
            return;
        }

        // The marquee is drawn in screen space: it is a viewport gesture, not a world one.
        if (_isMarqueeActive && _currentMarquee.Width > 1 && _currentMarquee.Height > 1)
        {
            var stroke = Brush("Brush.MarqueeStroke") ?? Brushes.Gray;
            var fill = Brush("Brush.MarqueeFill") ?? Brushes.Transparent;

            var pen = new Pen(stroke, 1)
            {
                DashStyle = DashStyles.Dash,
            };

            context.DrawRectangle(fill, pen, _currentMarquee);
        }
    }

    // --------------------------------------------------------------- input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        // A double-click arrives as a second press with ClickCount 2; FrameworkElement
        // has no OnMouseDoubleClick (that is a Control concern), and counting is the
        // platform-neutral way to catch it.
        if (e.ClickCount >= 2)
        {
            var worldHit = _controller.ScreenToWorld(e.GetPosition(this));
            var hitNode = _scene.Index.HitNode(worldHit);

            if (hitNode is not null)
            {
                _controller.NotifyGesture(GestureKind.NodeActivated, hitNode);
            }
            else
            {
                _controller.NotifyGesture(GestureKind.BackgroundDoubleClicked, null);
            }

            e.Handled = true;
            return;
        }

        Focus();
        CaptureMouse();
        _pointerDownPosition = e.GetPosition(this);
        _pointerLastPosition = _pointerDownPosition;
        _dragMoved = false;

        var world = _controller.ScreenToWorld(_pointerDownPosition);

        var hitEdge = _scene.HitEdge(world, [.. _visibleEdgeIds], _controller.Zoom);

        if (_isSpacePanning)
        {
            _isPanActive = true;
            Cursor = Cursors.ScrollAll;
            e.Handled = true;
            return;
        }

        if (_scene.Index.HitNode(world) is { } nodeId)
        {
            _isNodeDragActive = true;

            if (!_controller.SelectedNodeIds.Contains(nodeId))
            {
                _controller.SetSelection(
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                        ? SelectionMode.Toggle
                        : SelectionMode.Replace,
                    nodeId);
            }

            _controller.SetHover(nodeId);
        }
        else if (hitEdge is { } edgeId)
        {
            _controller.SelectEdge(edgeId);
        }
        else
        {
            _isMarqueeActive = true;
            _marqueeStart = _pointerDownPosition;
            _currentMarquee = Rect.Empty;

            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _controller.ClearSelection();
                _controller.ClearEdgeSelection();
            }
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        var delta = position - _pointerLastPosition;
        _pointerLastPosition = position;

        var moved = (position - _pointerDownPosition).Length > DragThreshold;

        if (moved)
        {
            _dragMoved = true;
        }

        if (_isPanActive)
        {
            _controller.PanBy(new Vector(-delta.X, -delta.Y));
            e.Handled = true;
            return;
        }

        if (_isNodeDragActive && _dragMoved)
        {
            var worldDelta = new Vector(delta.X / _controller.Zoom, delta.Y / _controller.Zoom);
            _controller.NudgeSelection(worldDelta, previewOnly: true);
            RefreshDraggedVisuals();
            e.Handled = true;
            return;
        }

        if (_isMarqueeActive && _dragMoved)
        {
            _currentMarquee = new Rect(
                Math.Min(_marqueeStart.X, position.X),
                Math.Min(_marqueeStart.Y, position.Y),
                Math.Abs(position.X - _marqueeStart.X),
                Math.Abs(position.Y - _marqueeStart.Y));

            RenderOverlay();
            _controller.SetLiveMarquee(_controller.ScreenToWorld(_currentMarquee.TopLeft),
                _controller.ScreenToWorld(_currentMarquee.BottomRight));
            e.Handled = true;
            return;
        }

        // Idle motion: hover feedback.
        var world = _controller.ScreenToWorld(position);
        var hover = _scene.Index.HitNode(world);
        _controller.SetHover(hover);
        Cursor = hover is null ? Cursors.Arrow : Cursors.Hand;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        ReleaseMouseCapture();

        if (_isNodeDragActive && _dragMoved)
        {
            _controller.CommitNudge();
        }

        if (_isMarqueeActive && _dragMoved)
        {
            var worldRect = new Rect(
                _controller.ScreenToWorld(new Point(_currentMarquee.X, _currentMarquee.Y)),
                _controller.ScreenToWorld(new Point(
                    _currentMarquee.X + _currentMarquee.Width,
                    _currentMarquee.Y + _currentMarquee.Height)));

            _controller.SelectInRect(worldRect);
        }

        _isNodeDragActive = false;
        _isMarqueeActive = false;
        _isPanActive = false;
        _currentMarquee = Rect.Empty;
        _controller.SetLiveMarquee(null, null);
        RenderOverlay();
        Cursor = Cursors.Arrow;

        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        // The context menu is owned by the hosting view, which knows the commands; the
        // canvas only publishes what is under the pointer so the menu can be shaped.
        var world = _controller.ScreenToWorld(e.GetPosition(this));
        var node = _scene.Index.HitNode(world);

        _controller.SetHover(node);
        e.Handled = false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        Focus();
        var position = e.GetPosition(this);
        _controller.ZoomAt(position, e.Delta > 0 ? WheelZoomStep : 1 / WheelZoomStep);
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        // The middle button pans. UIElement routes it through OnMouseDown (there is no
        // OnMouseMiddleButton override to take), so it is claimed here and never reaches
        // the marquee/drag logic that only speaks left.
        if (e.ChangedButton == MouseButton.Middle && _controller is not null)
        {
            CaptureMouse();
            _pointerDownPosition = e.GetPosition(this);
            _pointerLastPosition = _pointerDownPosition;
            _isPanActive = true;
            Cursor = Cursors.ScrollAll;
            e.Handled = true;
            return;
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _isPanActive)
        {
            _isPanActive = false;
            Cursor = Cursors.Arrow;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        base.OnMouseUp(e);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        if (e.Key == Key.System && e.SystemKey == Key.Space
            || e.Key == Key.Space)
        {
            _isSpacePanning = true;
            Cursor = Cursors.ScrollAll;
            e.Handled = true;
            return;
        }

        const double nudge = 2;
        const double largeNudge = 16;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? largeNudge : nudge;

        switch (e.Key)
        {
            case Key.Left:
                _controller.NudgeSelection(new Vector(-step / _controller.Zoom, 0), previewOnly: false);
                e.Handled = true;
                break;
            case Key.Right:
                _controller.NudgeSelection(new Vector(step / _controller.Zoom, 0), previewOnly: false);
                e.Handled = true;
                break;
            case Key.Up:
                _controller.NudgeSelection(new Vector(0, -step / _controller.Zoom), previewOnly: false);
                e.Handled = true;
                break;
            case Key.Down:
                _controller.NudgeSelection(new Vector(0, step / _controller.Zoom), previewOnly: false);
                e.Handled = true;
                break;
            case Key.Enter:
                _controller.NotifyGesture(GestureKind.NodeActivated, _controller.PrimarySelectedNodeId);
                e.Handled = true;
                break;
            case Key.Escape:
                if (_isNodeDragActive)
                {
                    _controller.CancelNudge();
                    _isNodeDragActive = false;
                    RefreshDraggedVisuals();
                }
                else
                {
                    _controller.ClearSelection();
                    _controller.ClearEdgeSelection();
                }

                e.Handled = true;
                break;
            case Key.Delete:
            case Key.Back:
                _controller.DeleteSelection();
                e.Handled = true;
                break;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.System && e.SystemKey == Key.Space)
        {
            _isSpacePanning = false;
            Cursor = Cursors.Arrow;
        }
    }

    /// <summary>Re-renders visuals for nodes being dragged: their incident edges follow them.</summary>
    /// <remarks>Drag positions are preview offsets over the snapshot; the graph is only
    /// written on release, so a cancelled drag snaps back by simply forgetting them.</remarks>
    private void RefreshDraggedVisuals()
    {
        if (_controller is null)
        {
            return;
        }

        var preview = new Dictionary<string, Point>();

        foreach (var id in _controller.SelectedNodeIds)
        {
            if (_scene.FindNode(id) is not { } visual)
            {
                continue;
            }

            var offset = _controller.PreviewOffset(id);

            visual.Visual.Transform = new TranslateTransform(visual.Node.X + offset.X, visual.Node.Y + offset.Y);
            preview[id] = new Point(visual.Node.X + offset.X, visual.Node.Y + offset.Y);
        }

        foreach (var edgeId in _visibleEdgeIds)
        {
            if (_scene.FindEdge(edgeId) is not { IsAttached: true } edgeVisual)
            {
                continue;
            }

            if (_controller.SelectedNodeIds.Contains(edgeVisual.SourceId)
                || _controller.SelectedNodeIds.Contains(edgeVisual.TargetId))
            {
                _scene.RenderEdge(edgeVisual, _controller.Snapshot, _controller.Zoom, preview);
            }
        }
    }

    // ------------------------------------------------------------- plumbing

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _scene.SetPixelsPerDip(dpi.PixelsPerDip);
        RefreshPalette();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        // Visuals stay alive in the scene; only the tree attachment is dropped, so coming
        // back to canvas mode is attach-and-go.
        foreach (var id in _attachedNodes.Concat(_attachedEdges).ToArray())
        {
            _content.Children.Clear();
            _attachedNodes.Clear();
            _attachedEdges.Clear();
            _visibleEdgeIds.Clear();
            break;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderGrid();
        SynchronizeCulling();
    }

    /// <summary>Re-resolves the palette (theme change or first load) and redraws.</summary>
    public void RefreshPalette()
    {
        var palette = CanvasPalette.FromResources(this);
        _scene.SetPalette(palette, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        RenderGrid();
        RenderVisible();
    }
}
