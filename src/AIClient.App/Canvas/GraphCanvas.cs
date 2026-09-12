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
/// hand tool is engaged, and the surface follows the pointer one pixel for one pixel at
/// any zoom; the wheel zooms to the cursor. This split keeps selection and navigation
/// from fighting over the same gesture, which is the classic canvas annoyance.
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

    /// <summary>Roughly a fifth per notch, and smooth for a fast scroll rather than stepped.</summary>
    /// <remarks>
    /// Raised to the wheel's own delta rather than applied once per event: a high-resolution
    /// wheel sends many small deltas, and a fixed step per event would zoom such a mouse in
    /// several times as fast as a notched one. This is the previous interface's constant.
    /// </remarks>
    private const double ZoomPerWheelUnit = 1.0015;

    private const double DragThreshold = 3.0;

    private CanvasController? _controller;
    private readonly CanvasScene _scene = new();

    // World space. One transform for the camera, two layers under it so an edge can never
    // draw over a card: the old renderer got that guarantee from two sequential ItemsControls,
    // and a single children collection in culling order cannot give it.
    private readonly ContainerVisual _content = new();
    private readonly ContainerVisual _edgeLayer = new();
    private readonly ContainerVisual _nodeLayer = new();
    private readonly MatrixTransform _view = new();

    // Screen space: the surface colour, the dot grid over it, the marquee over everything.
    private readonly DrawingVisual _gridVisual = new();
    private readonly DrawingVisual _dotVisual = new();
    private readonly DrawingVisual _overlayVisual = new();

    // The dot field is a tiled brush, so panning and zooming it is two transform writes
    // rather than a redraw of a few thousand rectangles.
    private readonly ScaleTransform _dotScale = new(1, 1);
    private readonly TranslateTransform _dotTranslate = new(0, 0);
    private readonly DrawingBrush _dotBrush;

    private readonly HashSet<string> _attachedNodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _attachedEdges = new(StringComparer.Ordinal);

    // Scratch buffers for culling and dragging. A viewport change happens on every wheel
    // notch and every mouse-move of a pan, so the pass that runs then allocates nothing.
    private readonly HashSet<string> _wantedNodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wantedEdges = new(StringComparer.Ordinal);
    private readonly HashSet<string> _relatedEdges = new(StringComparer.Ordinal);
    private readonly List<string> _detach = [];
    private readonly Dictionary<string, Point> _previewPositions = new(StringComparer.Ordinal);

    private Pen? _marqueePen;

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

        _dotBrush = BuildDotBrush();

        // A single aliased pixel per tile rather than a sub-pixel circle: an ellipse of
        // radius 0.9 has no whole-pixel form, so it rasterises into a soft diamond that
        // changes shape as the surface pans - a background that shimmers.
        RenderOptions.SetEdgeMode(_dotVisual, EdgeMode.Aliased);

        _content.Children.Add(_edgeLayer);
        _content.Children.Add(_nodeLayer);
        _content.Transform = _view;

        AddVisualChild(_gridVisual);
        AddVisualChild(_dotVisual);
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
            _controller.ToolChanged -= OnToolChanged;
        }

        _controller = controller;

        _controller.ViewportChanged += OnViewportChanged;
        _controller.StateChanged += OnStateChanged;
        _controller.SceneChanged += OnSceneChanged;
        _controller.ToolChanged += OnToolChanged;

        ApplyViewportTransform();
        RefreshPalette();

        // First attach is a reset by definition: the scene has never seen this graph.
        _scene.Reset(_controller.Snapshot);
        _attachedNodes.Clear();
        _attachedEdges.Clear();
        SynchronizeCulling();
        ApplySelectionStates();
        RenderVisible();
    }

    protected override int VisualChildrenCount => 4;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _gridVisual,
        1 => _dotVisual,
        2 => _content,
        3 => _overlayVisual,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    // -------------------------------------------------------------- viewport

    /// <summary>
    /// The camera moved. Nothing here re-records a drawing: the world is one transform, the
    /// dot field is a brush transform, and culling only attaches and detaches visuals that
    /// were already drawn.
    /// </summary>
    private void OnViewportChanged(object? sender, EventArgs e)
    {
        ApplyViewportTransform();
        UpdateDotViewport();
        SynchronizeCulling();
        RenderVisible();
        RenderOverlay();

        // Zooming with the wheel is legal mid-drag, and culling can pull in an edge that was
        // off screen when the drag began. Such an edge would otherwise be drawn against the
        // committed positions while its card sits at the preview one - a link with one end
        // adrift.
        if (_isNodeDragActive)
        {
            RefreshDraggedVisuals();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Selection/hover changes re-render only the visuals whose state changed; the
        // controller has already marked them dirty through the scene.
        ApplySelectionStates();
        RenderVisible();
        RenderOverlay();
    }

    private void OnToolChanged(object? sender, EventArgs e)
    {
        // The tool decides the resting cursor; a live pan or marquee overrides it per gesture.
        Cursor = RestingCursor();
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
            _edgeLayer.Children.Clear();
            _nodeLayer.Children.Clear();
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

        // One long-lived transform, written rather than replaced: a new MatrixTransform per
        // mouse-move is a new DependencyObject the composition has to adopt on every frame
        // of a pan.
        _view.Matrix = _controller.View;
    }

    // ------------------------------------------------------------ grid layer

    /// <summary>
    /// The surface colour behind everything. Recorded on resize and on a theme change, and at
    /// no other time - it does not depend on the camera.
    /// </summary>
    private void RenderSurface()
    {
        var size = RenderSize;

        if (size.Width < 1 || size.Height < 1)
        {
            return;
        }

        using var context = _gridVisual.RenderOpen();
        context.DrawRectangle(Brush("Brush.CanvasBackground") ?? Brushes.Transparent, null, new Rect(0, 0, size.Width, size.Height));
    }

    /// <summary>
    /// The dot grid: one rectangle filled with a tiled brush, recorded on resize only.
    /// </summary>
    /// <remarks>
    /// The dots are anchored to the world by the brush's own transform, which is the same pan
    /// and scale the cards get. Drawing them as individual rectangles - one
    /// <c>DrawRectangle</c> per dot, a few thousand of them, re-recorded on every wheel notch
    /// and every mouse-move of a pan - is what made the canvas stutter and the edges tear:
    /// re-recording a visual mid-gesture drops the frame that was being composed.
    /// </remarks>
    private void RenderDots()
    {
        var size = RenderSize;

        if (size.Width < 1 || size.Height < 1)
        {
            return;
        }

        using var context = _dotVisual.RenderOpen();
        context.DrawRectangle(_dotBrush, null, new Rect(0, 0, size.Width, size.Height));
    }

    /// <summary>
    /// Moves the dot field with the camera: two transform writes and an opacity, no redraw.
    /// </summary>
    /// <remarks>
    /// Opacity follows the zoom - values above one clamp - which fades the grid out as the
    /// camera pulls back and the dots would otherwise close into a wash.
    /// </remarks>
    private void UpdateDotViewport()
    {
        if (_controller is null)
        {
            return;
        }

        var zoom = _controller.Zoom;
        var view = _controller.View;

        _dotScale.ScaleX = zoom;
        _dotScale.ScaleY = zoom;
        _dotTranslate.X = view.OffsetX;
        _dotTranslate.Y = view.OffsetY;
        _dotVisual.Opacity = Math.Clamp(zoom, 0, 1);
    }

    /// <summary>The tiled dot, built once: a 24-unit tile carrying a single pixel at its centre.</summary>
    private DrawingBrush BuildDotBrush()
    {
        var geometry = new RectangleGeometry(new Rect(12, 12, 1, 1));
        geometry.Freeze();

        var dot = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80));
        dot.Freeze();

        var drawing = new GeometryDrawing(dot, null, geometry);
        drawing.Freeze();

        var transform = new TransformGroup();
        transform.Children.Add(_dotScale);
        transform.Children.Add(_dotTranslate);

        // Not frozen, and cannot be: the transform is written on every camera move. Freezing
        // the parts that never change is what keeps the write cheap.
        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 24, 24),
            Transform = transform,
        };
    }

    private Brush? Brush(string key) => TryFindResource(key) as Brush;

    // ------------------------------------------------------------- culling

    /// <summary>
    /// Brings the attached set in line with the viewport. Runs on every camera move, so it
    /// allocates nothing and touches no drawing.
    /// </summary>
    private void SynchronizeCulling()
    {
        if (_controller is null)
        {
            return;
        }

        var worldRect = _controller.VisibleWorldRect(RenderSize);

        _wantedNodes.Clear();

        foreach (var id in _scene.Index.Query(worldRect))
        {
            _wantedNodes.Add(id);
        }

        // Remove first: the VisualCollection churns less when additions outnumber removals.
        _detach.Clear();

        foreach (var id in _attachedNodes)
        {
            if (!_wantedNodes.Contains(id))
            {
                _detach.Add(id);
            }
        }

        foreach (var id in _detach)
        {
            if (_scene.FindNode(id) is { IsAttached: true } visual)
            {
                _nodeLayer.Children.Remove(visual.Visual);
                visual.IsAttached = false;
            }

            _attachedNodes.Remove(id);
        }

        foreach (var id in _wantedNodes)
        {
            if (_attachedNodes.Contains(id))
            {
                continue;
            }

            if (_scene.FindNode(id) is { IsAttached: false } visual)
            {
                _nodeLayer.Children.Add(visual.Visual);
                visual.IsAttached = true;
                _attachedNodes.Add(id);
            }
        }

        // Edges are culled by the cached hull of their endpoints rather than their curve
        // bounds: the hull is maintained by the scene whenever a node moves, so this pass is
        // a rectangle test per edge and nothing more.
        _wantedEdges.Clear();

        foreach (var visual in _scene.AllEdgeVisuals())
        {
            if (!visual.Hull.IsEmpty && visual.Hull.IntersectsWith(worldRect))
            {
                _wantedEdges.Add(visual.Edge.Id);
            }
        }

        _detach.Clear();

        foreach (var id in _attachedEdges)
        {
            if (!_wantedEdges.Contains(id))
            {
                _detach.Add(id);
            }
        }

        foreach (var id in _detach)
        {
            if (_scene.FindEdge(id) is { IsAttached: true } visual)
            {
                _edgeLayer.Children.Remove(visual.Visual);
                visual.IsAttached = false;
            }

            _attachedEdges.Remove(id);
        }

        foreach (var id in _wantedEdges)
        {
            if (_attachedEdges.Contains(id))
            {
                continue;
            }

            if (_scene.FindEdge(id) is { IsAttached: false } visual)
            {
                _edgeLayer.Children.Add(visual.Visual);
                visual.IsAttached = true;
                _attachedEdges.Add(id);
            }
        }
    }

    /// <summary>Draws whatever is attached and dirty; a clean visual costs one branch.</summary>
    private void RenderVisible()
    {
        if (_controller is null)
        {
            return;
        }

        foreach (var id in _attachedNodes)
        {
            if (_scene.FindNode(id) is { } visual)
            {
                _scene.RenderNode(visual);
            }
        }

        foreach (var id in _attachedEdges)
        {
            if (_scene.FindEdge(id) is { } visual)
            {
                _scene.RenderEdge(visual, _controller.Snapshot);
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

        // Reused: hover is part of this state, so the pass runs on plain mouse motion over the
        // canvas and must not allocate a set per frame.
        _relatedEdges.Clear();

        if (hasSelection)
        {
            foreach (var edge in _scene.EdgesIncidentTo(selection))
            {
                _relatedEdges.Add(edge);
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
            else if (_relatedEdges.Contains(visual.Edge.Id))
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

    /// <summary>
    /// The marquee, in screen space. Recorded only while a marquee gesture is live; the rest of
    /// the time this visual holds an empty drawing.
    /// </summary>
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
            var fill = Brush("Brush.MarqueeFill") ?? Brushes.Transparent;

            // Built once and frozen: this runs on every mouse-move of the gesture, and a fresh
            // unfrozen Pen per frame is a fresh DependencyObject plus a fresh DashStyle.
            _marqueePen ??= FrozenPen(Brush("Brush.MarqueeStroke") ?? Brushes.Gray, 1, DashStyles.Dash);

            context.DrawRectangle(fill, _marqueePen, _currentMarquee);
        }
    }

    private static Pen FrozenPen(Brush brush, double thickness, DashStyle? dashStyle = null)
    {
        var pen = new Pen(brush, thickness);

        if (dashStyle is not null)
        {
            pen.DashStyle = dashStyle;
            pen.DashCap = PenLineCap.Flat;
        }

        pen.Freeze();

        return pen;
    }

    // --------------------------------------------------------------- input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        // The hand tool owns the left button outright: no hit test, no double-click, no
        // selection. Someone who reached for the hand does not want a card to follow the
        // pointer because the drag began a few pixels too far to the left - that is the
        // whole reason the tool exists, and it is why this branch comes first.
        if (_controller.ActiveTool == CanvasTool.Pan || _isSpacePanning)
        {
            Focus();
            CaptureMouse();
            _pointerDownPosition = e.GetPosition(this);
            _pointerLastPosition = _pointerDownPosition;
            _dragMoved = false;
            _isPanActive = true;
            Cursor = Cursors.SizeAll;
            e.Handled = true;
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
        var hitEdge = _scene.HitEdge(world, _attachedEdges, _controller.Zoom);

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
            // The pointer's own delta, unnegated: the surface goes where the hand goes. The
            // previous interface panned this way (its camera added the raw delta to its pan),
            // and inverting it makes a hand tool behave like a scrollbar.
            _controller.PanBy(delta);
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

        // Idle motion: hover feedback. Nothing on the surface answers a left click while the
        // hand is engaged, so nothing lights up as though it would - and skipping the hit
        // test is free.
        if (_controller.ActiveTool == CanvasTool.Pan || _isSpacePanning)
        {
            _controller.SetHover(null);
            Cursor = Cursors.SizeAll;
            return;
        }

        var hover = _scene.Index.HitNode(_controller.ScreenToWorld(position));
        _controller.SetHover(hover);
        Cursor = hover is null ? Cursors.Arrow : Cursors.Hand;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

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
        Cursor = RestingCursor();

        // Released last, and deliberately: letting go raises LostMouseCapture, which aborts
        // whatever gesture is still running. By this line none is, so the abort is a no-op
        // rather than something that would throw away the move just committed above.
        ReleaseMouseCapture();

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
        _controller.ZoomAt(position, Math.Pow(ZoomPerWheelUnit, e.Delta));
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        // The middle button pans whatever tool is chosen. UIElement routes it through
        // OnMouseDown (there is no OnMouseMiddleButton override to take), so it is claimed
        // here and never reaches the marquee/drag logic that only speaks left. The right
        // button is not a second pan the way it was in the previous interface: this surface
        // has a context menu, and the two would be fighting over the same press.
        if (e.ChangedButton == MouseButton.Middle && _controller is not null)
        {
            CaptureMouse();
            _pointerDownPosition = e.GetPosition(this);
            _pointerLastPosition = _pointerDownPosition;
            _isPanActive = true;
            Cursor = Cursors.SizeAll;
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
            Cursor = RestingCursor();
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        base.OnMouseUp(e);
    }

    /// <summary>
    /// Capture can be taken away mid-gesture - another window grabs it, a dialog opens,
    /// Alt+Tab. The MouseUp that would have ended the gesture then never arrives, so without
    /// this the canvas would go on panning or dragging under a button that is no longer down.
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_controller is not null && (_isPanActive || _isNodeDragActive || _isMarqueeActive))
        {
            // A drag cut short is forgotten rather than committed: the button was never
            // released, so the user never said where the cards should land.
            if (_isNodeDragActive)
            {
                _isNodeDragActive = false;
                _controller.CancelNudge();
                RefreshDraggedVisuals();
            }

            _isPanActive = false;
            _isMarqueeActive = false;
            _currentMarquee = Rect.Empty;
            _controller.SetLiveMarquee(null, null);
            RenderOverlay();
            Cursor = RestingCursor();
        }

        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        // A card left lit after the pointer has gone reads as selected. Gestures are exempt:
        // a captured drag keeps receiving moves out here, and its subject stays hovered.
        if (_controller is not null && !_isPanActive && !_isNodeDragActive && !_isMarqueeActive)
        {
            _controller.SetHover(null);
        }

        base.OnMouseLeave(e);
    }

    /// <summary>The cursor between gestures: the hand tool's, or the plain arrow.</summary>
    private Cursor RestingCursor() =>
        _controller?.ActiveTool == CanvasTool.Pan || _isSpacePanning
            ? Cursors.SizeAll
            : Cursors.Arrow;

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
            Cursor = Cursors.SizeAll;
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
        // Spelled out rather than folded into one pattern: `is Key.Space or Key.System && ...`
        // binds the pattern first, so a plain space release failed the test and left the
        // canvas in pan mode - the left button panning instead of selecting - for good.
        if (e.Key == Key.Space || (e.Key == Key.System && e.SystemKey == Key.Space))
        {
            _isSpacePanning = false;

            // A pan already under way keeps its cursor until the button comes up.
            if (!_isPanActive)
            {
                Cursor = RestingCursor();
            }
        }
    }

    /// <summary>
    /// Space-panning is a held key, and a key held across a focus change never reports its
    /// release here - the mode would outlive the gesture that asked for it.
    /// </summary>
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        if (_isSpacePanning)
        {
            _isSpacePanning = false;

            if (!_isPanActive)
            {
                Cursor = RestingCursor();
            }
        }

        base.OnLostKeyboardFocus(e);
    }

    /// <summary>Re-renders visuals for nodes being dragged: their incident edges follow them.</summary>
    /// <remarks>
    /// <para>Drag positions are preview offsets over the snapshot; the graph is only written on
    /// release, so a cancelled drag snaps back by simply forgetting them.</para>
    /// <para>This runs on every mouse-move of a drag, so it allocates nothing: the preview map is
    /// a reused buffer and a node's transform is written rather than replaced.</para>
    /// </remarks>
    private void RefreshDraggedVisuals()
    {
        if (_controller is null)
        {
            return;
        }

        _previewPositions.Clear();

        foreach (var id in _controller.SelectedNodeIds)
        {
            if (_scene.FindNode(id) is not { } visual)
            {
                continue;
            }

            var offset = _controller.PreviewOffset(id);
            var x = visual.Node.X + offset.X;
            var y = visual.Node.Y + offset.Y;

            if (visual.Visual.Transform is TranslateTransform translate)
            {
                translate.X = x;
                translate.Y = y;
            }
            else
            {
                visual.Visual.Transform = new TranslateTransform(x, y);
            }

            _previewPositions[id] = new Point(x, y);
        }

        // Every attached edge is walked, not only the ones that were attached this pass: an
        // edge whose endpoint is being dragged has to keep its ends on the cards, and missing
        // one is exactly what made the links tear away mid-drag.
        foreach (var edgeId in _attachedEdges)
        {
            if (_scene.FindEdge(edgeId) is not { IsAttached: true } edgeVisual)
            {
                continue;
            }

            if (_previewPositions.ContainsKey(edgeVisual.SourceId)
                || _previewPositions.ContainsKey(edgeVisual.TargetId))
            {
                _scene.RenderEdge(edgeVisual, _controller.Snapshot, _previewPositions);
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
        // back to canvas mode is attach-and-go. The two layers themselves are permanent
        // children of the world root and are emptied rather than removed.
        _edgeLayer.Children.Clear();
        _nodeLayer.Children.Clear();

        foreach (var visual in _scene.AllNodeVisuals())
        {
            visual.IsAttached = false;
        }

        foreach (var visual in _scene.AllEdgeVisuals())
        {
            visual.IsAttached = false;
        }

        _attachedNodes.Clear();
        _attachedEdges.Clear();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderSurface();
        RenderDots();
        UpdateDotViewport();
        SynchronizeCulling();

        // A resize reveals nodes that culling had dropped; without this they attach blank
        // until some other event redraws them.
        RenderVisible();
    }

    /// <summary>Re-resolves the palette (theme change or first load) and redraws.</summary>
    public void RefreshPalette()
    {
        var palette = CanvasPalette.FromResources(this);
        _scene.SetPalette(palette, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // Dropped rather than rebuilt: the marquee is only ever pending during a gesture, and
        // the next RenderOverlay resolves it against the new theme.
        _marqueePen = null;

        RenderSurface();
        RenderDots();
        UpdateDotViewport();
        RenderVisible();
    }
}
