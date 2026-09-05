using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AIClient.App.Canvas;

/// <summary>
/// A map of the whole graph the size of a postage stamp: every node as a dot, the viewport
/// as a rectangle, and the world rearranged by dragging either.
/// </summary>
/// <remarks>
/// <para>
/// The minimap redraws its node layer only when the graph version changes and its viewport
/// layer only when the view moves, so it costs two small draw passes per interaction - and
/// nothing per frame. All nodes are drawn, not just visible ones: the minimap's entire job
/// is the part of the graph the canvas is <i>not</i> showing.
/// </para>
/// <para>
/// Pointer handling is deliberately crude - press or drag recentres the viewport - because
/// a minimap that demands precision has missed its own point.
/// </para>
/// </remarks>
public class CanvasMinimap : FrameworkElement
{
    private readonly DrawingVisual _graphVisual = new();
    private readonly DrawingVisual _viewportVisual = new();

    private CanvasController? _controller;
    private Rect _contentBounds;
    private Matrix _worldToMini;

    public CanvasMinimap()
    {
        ClipToBounds = true;
        Width = 160;
        Height = 100;
        Focusable = false;
        Cursor = Cursors.Hand;

        AddVisualChild(_graphVisual);
        AddVisualChild(_viewportVisual);

        MouseLeftButtonDown += OnPointerDown;
        MouseMove += OnPointerMove;
        MouseLeftButtonUp += OnPointerUp;
    }

    public void SetController(CanvasController controller)
    {
        if (_controller is not null)
        {
            _controller.ViewportChanged -= OnViewportChanged;
            _controller.SceneChanged -= OnSceneChanged;
        }

        _controller = controller;
        _controller.ViewportChanged += OnViewportChanged;
        _controller.SceneChanged += OnSceneChanged;

        RedrawGraph();
        RedrawViewport();
    }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _graphVisual,
        1 => _viewportVisual,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RedrawGraph();
        RedrawViewport();
    }

    private void OnSceneChanged(object? sender, SceneChangedEventArgs e)
    {
        RedrawGraph();
        RedrawViewport();
    }

    private void OnViewportChanged(object? sender, EventArgs e) => RedrawViewport();

    /// <summary>
    /// Fits the graph's content rectangle into the minimap's own rectangle with padding, and
    /// remembers the transform for both layers.
    /// </summary>
    private void ComputeTransform()
    {
        if (_controller is null)
        {
            _worldToMini = Matrix.Identity;
            return;
        }

        _contentBounds = GraphProjection.ContentBounds(_controller.Snapshot);

        var size = RenderSize;

        if (_contentBounds.IsEmpty || size.Width < 4 || size.Height < 4)
        {
            _worldToMini = Matrix.Identity;
            return;
        }

        var padding = 6;
        var scaleX = (size.Width - padding * 2) / Math.Max(_contentBounds.Width, 1);
        var scaleY = (size.Height - padding * 2) / Math.Max(_contentBounds.Height, 1);
        var scale = Math.Min(scaleX, scaleY);

        // Centre the scaled content in the minimap.
        var scaledWidth = _contentBounds.Width * scale;
        var scaledHeight = _contentBounds.Height * scale;

        _worldToMini = new Matrix(
            scale, 0, 0, scale,
            padding + (size.Width - padding * 2 - scaledWidth) / 2 - _contentBounds.Left * scale,
            padding + (size.Height - padding * 2 - scaledHeight) / 2 - _contentBounds.Top * scale);
    }

    private void RedrawGraph()
    {
        ComputeTransform();

        using var context = _graphVisual.RenderOpen();

        if (_controller is null || _contentBounds.IsEmpty)
        {
            return;
        }

        context.DrawRectangle(Brush("Brush.SurfaceSunken") ?? Brushes.Transparent, null,
            new Rect(0, 0, RenderSize.Width, RenderSize.Height));

        var nodeBrush = Brush("Brush.Node.File") ?? Brushes.Gray;
        var selectedBrush = Brush("Brush.Accent") ?? Brushes.Gray;

        var selected = _controller.SelectedNodeIds;

        foreach (var node in _controller.Snapshot.Nodes)
        {
            var centre = _worldToMini.Transform(new Point(node.X, node.Y));

            var brush = selected.Contains(node.Id) ? selectedBrush : nodeBrush;
            var radius = selected.Contains(node.Id) ? 2.2 : 1.4;

            context.DrawEllipse(brush, null, centre, radius, radius);
        }
    }

    private void RedrawViewport()
    {
        if (_controller is null)
        {
            return;
        }

        ComputeTransform();

        using var context = _viewportVisual.RenderOpen();

        var size = RenderSize;

        if (size.Width < 4 || size.Height < 4)
        {
            return;
        }

        // The controller remembers the canvas's actual viewport size from the last time
        // the canvas asked for its world rectangle; using the real value keeps the map's
        // viewport box honest when the canvas and the map disagree in aspect.
        var viewportSize = _controller.LastViewportSize;
        var viewportWorld = new Rect(
            new Point(_controller.Offset.X, _controller.Offset.Y),
            new Size(
                viewportSize.Width <= 0 ? 160 / _controller.Zoom : viewportSize.Width / _controller.Zoom,
                viewportSize.Height <= 0 ? 100 / _controller.Zoom : viewportSize.Height / _controller.Zoom));

        var topLeft = _worldToMini.Transform(viewportWorld.TopLeft);
        var bottomRight = _worldToMini.Transform(viewportWorld.BottomRight);

        var rect = new Rect(topLeft, bottomRight);

        var fill = Brush("Brush.MarqueeFill") ?? Brushes.Transparent;
        var stroke = Brush("Brush.Accent") ?? Brushes.Gray;

        var pen = new Pen(stroke, 1);

        context.DrawRectangle(fill, pen, rect);
    }

    private Brush? Brush(string key) => TryFindResource(key) as Brush;

    // ------------------------------------------------------------ pointer

    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        CaptureMouse();
        CenterOn(e.GetPosition(this));
        e.Handled = true;
    }

    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        if (IsMouseCaptured)
        {
            CenterOn(e.GetPosition(this));
            e.Handled = true;
        }
    }

    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void CenterOn(Point miniPoint)
    {
        if (_controller is null || !_worldToMini.HasInverse)
        {
            return;
        }

        // Matrix has no Inverse property: invert a copy - the original stays valid for
        // the next redraw.
        var inverse = _worldToMini;
        inverse.Invert();

        var world = inverse.Transform(miniPoint);

        // Recentre without touching zoom: the map navigates, it does not rescale.
        var viewportSize = _controller.LastViewportSize;
        var halfWidth = (viewportSize.Width <= 0 ? 160 : viewportSize.Width) / _controller.Zoom / 2;
        var halfHeight = (viewportSize.Height <= 0 ? 100 : viewportSize.Height) / _controller.Zoom / 2;

        _controller.SetViewport(_controller.Zoom, world.X - halfWidth, world.Y - halfHeight);
    }
}
