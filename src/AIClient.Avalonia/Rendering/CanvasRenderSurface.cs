using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AIClient.Avalonia.ViewModels.Canvas;
using AIClient.Application.DTOs;

namespace AIClient.Avalonia.Rendering;

/// <summary>
/// The canvas work surface: one control, every card, drawn by hand.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece the WPF canvas did with an <c>ItemsControl</c> and a data template per
/// node, and the reason the port exists: nine visuals per card stops being a rendering
/// strategy somewhere below two thousand cards. Here there is one control and no per-node
/// visual at all. <see cref="Render"/> walks the view model's culled collections and issues
/// draw commands; the view model decides what is near the camera, what is selected and where
/// a drag is - this class decides nothing.
/// </para>
/// <para>
/// The renderer reads only prepared state: bounds, strings, hex colours, flags. It knows
/// nothing about the graph, the database or the agent, and the only Avalonia types it
/// touches live in this file - a different backend means a different <see cref="Render"/>,
/// not a different canvas.
/// </para>
/// <para>
/// Gestures live here too, and the state machine is the port of the WPF one: press resolves
/// into drag, marquee or nothing; middle and right buttons pan in either tool; the wheel
/// zooms about the pointer. Cards never see the pointer - hover is set after a world-space
/// hit test, which is what keeps hit semantics independent of zoom.
/// </para>
/// </remarks>
public sealed class CanvasRenderSurface : Control
{
    /// <summary>How far the pointer must travel before a press on a card becomes a drag.</summary>
    private const double DragThreshold = 3;

    /// <summary>World grid spacing of the dot grid, and roughly a fifth per wheel notch.</summary>
    private const double GridSpacing = 24;
    private const double ZoomPerNotch = 1.2;

    private readonly record struct CachedText(
        string Glyph,
        string GlyphColour,
        string Title,
        string Subtitle,
        FormattedText GlyphText,
        FormattedText TitleText,
        FormattedText SubtitleText);

    private Gesture _gesture;
    private Point _last;
    private Point _pressed;
    private bool _ctrlDown;
    private CanvasNodeViewModel? _hovered;
    private readonly Dictionary<string, IBrush> _brushes = [];
    private readonly Dictionary<Guid, CachedText> _text = [];

    private enum Gesture
    {
        None,
        Press,
        Drag,
        Marquee,
        Pan,
    }

    public CanvasRenderSurface()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    private CanvasViewModel? ViewModel => DataContext as CanvasViewModel;

    /// <summary>Called by the shell when the theme changes, so cached brushes are rebuilt.</summary>
    public void OnThemeChanged()
    {
        _brushes.Clear();
        _text.Clear();
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == DataContextProperty)
        {
            if (e.OldValue is CanvasViewModel old)
            {
                old.InvalidateRender -= OnInvalidateRender;
            }

            if (e.NewValue is CanvasViewModel fresh)
            {
                fresh.InvalidateRender += OnInvalidateRender;
            }
        }
        else if (e.Property == BoundsProperty &&
                 e.NewValue is Rect bounds &&
                 ViewModel is { } vm)
        {
            // Culling, the AI surface anchor and fit all need the surface size.
            vm.SetSurfaceSize(bounds.Width, bounds.Height);
        }
    }

    private void OnInvalidateRender(object? sender, EventArgs e) => InvalidateVisual();

    // ------------------------------------------------------------------
    // Painting
    // ------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var vm = ViewModel;

        if (vm is null)
        {
            return;
        }

        var surface = new Rect(0, 0, Bounds.Width, Bounds.Height);

        context.FillRectangle(Brush("CanvasBrush", Colors.Transparent), surface);

        DrawGrid(context, vm, surface);

        var camera = vm.Viewport.Normalized();
        var matrix = Matrix.CreateScale(camera.Zoom, camera.Zoom) *
                     Matrix.CreateTranslation(camera.PanX, camera.PanY);

        using (context.PushTransform(matrix))
        {
            DrawEdges(context, vm);
            DrawNodes(context, vm);
        }

        DrawMarquee(context, vm);
    }

    private void DrawGrid(DrawingContext context, CanvasViewModel vm, Rect surface)
    {
        var camera = vm.Viewport.Normalized();
        var spacing = GridSpacing * camera.Zoom;

        // Below this the dots close into a wash; fade the whole grid out as the camera
        // pulls back rather than let it shimmer.
        if (spacing < 5)
        {
            return;
        }

        var brush = Brush("GridDotBrush", Color.Parse("#33808080"));
        var opacity = Math.Clamp(camera.Zoom, 0.25, 1);

        using (context.PushOpacity(opacity))
        {
            var offsetX = camera.PanX % spacing;
            var offsetY = camera.PanY % spacing;
            if (offsetX < 0)
            {
                offsetX += spacing;
            }

            if (offsetY < 0)
            {
                offsetY += spacing;
            }

            for (var x = offsetX; x < surface.Width; x += spacing)
            {
                for (var y = offsetY; y < surface.Height; y += spacing)
                {
                    context.FillRectangle(brush, new Rect(x, y, 1.4, 1.4));
                }
            }
        }
    }

    private void DrawEdges(DrawingContext context, CanvasViewModel vm)
    {
        var normal = PenFor(Brush("StrokeBrush", Colors.Gray), 1.2);
        var highlighted = PenFor(Brush("AccentBrush", Colors.SteelBlue), 1.8);

        foreach (var link in vm.VisibleEdges)
        {
            var pen = link.IsHighlighted ? highlighted : normal;

            var from = link.From.Bounds;
            var to = link.To.Bounds;

            if (to.Left >= from.Right - 1)
            {
                // The ordinary case: the arrow runs left to right between the card edges.
                var start = new Point(from.Right, from.CenterY);
                var end = new Point(to.Left, to.CenterY);
                var dx = (end.X - start.X) * 0.45;

                DrawCurve(context, start,
                    new Point(start.X + dx, start.Y),
                    new Point(end.X - dx, end.Y),
                    end, pen, link.IsHighlighted);
            }
            else
            {
                // A backwards relation: route it vertically instead, so it stays readable
                // rather than doubling back through the card it comes from.
                var start = new Point(from.CenterX, from.Bottom);
                var end = new Point(to.CenterX, to.Top);
                var dy = (end.Y - start.Y) * 0.45;

                DrawCurve(context, start,
                    new Point(start.X, start.Y + dy),
                    new Point(end.X, end.Y - dy),
                    end, pen, link.IsHighlighted);
            }
        }
    }

    private void DrawCurve(
        DrawingContext context,
        Point start,
        Point control1,
        Point control2,
        Point end,
        IPen pen,
        bool highlighted)
    {
        var geometry = new StreamGeometry();

        using (var writer = geometry.Open())
        {
            writer.BeginFigure(start, false);
            writer.CubicBezierTo(control1, control2, end);
            writer.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);

        // The head, as a small triangle on the direction the curve arrives from.
        var angle = Math.Atan2(end.Y - control2.Y, end.X - control2.X);
        var size = 7.0;
        var a1 = new Point(end.X - (size * Math.Cos(angle - 0.42)), end.Y - (size * Math.Sin(angle - 0.42)));
        var a2 = new Point(end.X - (size * Math.Cos(angle + 0.42)), end.Y - (size * Math.Sin(angle + 0.42)));

        var head = new StreamGeometry();
        using (var writer = head.Open())
        {
            writer.BeginFigure(end, true);
            writer.LineTo(a1);
            writer.LineTo(a2);
            writer.EndFigure(true);
        }

        context.DrawGeometry(
            highlighted ? Brush("AccentBrush", Colors.SteelBlue) : Brush("StrokeBrush", Colors.Gray),
            null,
            head);
    }

    private void DrawNodes(DrawingContext context, CanvasViewModel vm)
    {
        foreach (var card in vm.VisibleNodes)
        {
            var rect = new Rect(card.X, card.Y, card.Width, card.Height);
            var kind = Brush(card.KindColour, Colors.SlateGray);

            using (card.IsMissing ? context.PushOpacity(0.45) : default)
            {
                if (card.IsSelected)
                {
                    // Drawn outside the card so the card's own border stays a single pixel.
                    context.DrawRectangle(null, PenFor(Brush("AccentBrush", Colors.SteelBlue), 2), rect.Inflate(3), 10, 10);
                }

                var fill = card.IsHovered ? Brush("SurfaceAltBrush", Colors.DimGray) : Brush("CardBrush", Colors.White);
                context.FillRectangle(fill, rect, 7);
                context.DrawRectangle(
                    null,
                    PenFor(
                        card.IsSelected
                            ? Brush("AccentBrush", Colors.SteelBlue)
                            : Brush("StrokeBrush", Colors.Gray),
                        1),
                    rect,
                    7,
                    7);

                // The kind's colour as a strip rather than a tint, so two hundred cards do
                // not turn into a paint chart.
                context.FillRectangle(kind, new Rect(card.X, card.Y, 3, card.Height), 1.5f);

                DrawCardText(context, card, rect);
            }
        }
    }

    private void DrawCardText(DrawingContext context, CanvasNodeViewModel card, Rect rect)
    {
        var cached = _text.GetValueOrDefault(card.Id);
        var glyphColour = card.KindColour;

        if (cached.Glyph != card.Glyph ||
            cached.GlyphColour != glyphColour ||
            cached.Title != card.Title ||
            cached.Subtitle != card.Subtitle)
        {
            var titleBrush = Brush("TextPrimaryBrush", Colors.Black);
            var subtitleBrush = Brush("TextSecondaryBrush", Colors.Gray);
            var kindBrush = Brush(glyphColour, Colors.SlateGray);

            var maxTextWidth = Math.Max(20, rect.Width - 12 - 16 - 8);

            cached = new CachedText(
                card.Glyph,
                glyphColour,
                card.Title,
                card.Subtitle,
                Text(card.Glyph, 11, FontWeight.Normal, kindBrush),
                Text(Trim(card.Title, 13, FontWeight.SemiBold, titleBrush, maxTextWidth), 13, FontWeight.SemiBold, titleBrush),
                Text(Trim(card.Subtitle, 11, FontWeight.Normal, subtitleBrush, maxTextWidth), 11, FontWeight.Normal, subtitleBrush));

            _text[card.Id] = cached;
        }

        // One row, vertically centred: glyph, title, then the subtitle under the title.
        var glyphX = card.X + 12;
        var textX = glyphX + cached.GlyphText.Width + 7;
        var textWidth = Math.Max(20, rect.Width - 12 - 16 - 8);
        var blockHeight = cached.TitleText.Height + 3 + cached.SubtitleText.Height;
        var textTop = card.Y + ((card.Height - blockHeight) / 2);

        context.DrawText(cached.GlyphText, new Point(glyphX, card.CenterY - (cached.GlyphText.Height / 2)));
        context.DrawText(cached.TitleText, new Point(textX, textTop));
        context.DrawText(cached.SubtitleText, new Point(textX, textTop + cached.TitleText.Height + 3));
    }

    private void DrawMarquee(DrawingContext context, CanvasViewModel vm)
    {
        if (!vm.IsMarqueeVisible)
        {
            return;
        }

        var rect = new Rect(vm.MarqueeX, vm.MarqueeY, vm.MarqueeWidth, vm.MarqueeHeight);

        context.FillRectangle(Brush("MarqueeBrush", Color.FromArgb(0x26, 0x80, 0x80, 0x80)), rect);
        context.DrawRectangle(null, PenFor(Brush("AccentBrush", Colors.SteelBlue), 1), rect);
    }

    /// <summary>A pen over a cached brush. Built per frame; the brushes themselves are not.</summary>
    private static Pen PenFor(IBrush brush, double thickness) => new(brush, thickness, lineCap: PenLineCap.Round);

    // ------------------------------------------------------------------
    // Text
    // ------------------------------------------------------------------

    private static FormattedText Text(string value, double size, FontWeight weight, IBrush brush) =>
        new(
            value,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight),
            size,
            brush);

    /// <summary>Shortens a string until it fits, from the right, the way ellipsis would.</summary>
    private string Trim(string value, double size, FontWeight weight, IBrush brush, double maxWidth)
    {
        if (Text(value, size, weight, brush).Width <= maxWidth)
        {
            return value;
        }

        while (value.Length > 1 && Text(value + "…", size, weight, brush).Width > maxWidth)
        {
            value = value[..^1];
        }

        return value + "…";
    }

    private IBrush Brush(string key, Color fallback)
    {
        if (_brushes.TryGetValue(key, out var cached))
        {
            return cached;
        }

        IBrush brush;

        if (global::Avalonia.Application.Current?.TryGetResource(key, ActualThemeVariant, out var resource) == true &&
            resource is IBrush resolved)
        {
            brush = resolved;
        }
        else
        {
            brush = new SolidColorBrush(fallback);
        }

        _brushes[key] = brush;

        return brush;
    }

    // ------------------------------------------------------------------
    // Gestures
    // ------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var vm = ViewModel;

        if (vm is null)
        {
            return;
        }

        Focus();

        var point = e.GetCurrentPoint(this);
        var at = point.Position;
        var button = point.Properties.PointerUpdateKind;

        _pressed = at;
        _last = at;

        var isMiddleOrRight = button is PointerUpdateKind.MiddleButtonPressed
            or PointerUpdateKind.RightButtonPressed;

        if (vm.Tool == CanvasTool.Pan || isMiddleOrRight)
        {
            // No hit test at all in this mode: a person who chose the hand does not want a
            // card to follow the pointer because they started a few pixels too far left.
            _gesture = Gesture.Pan;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var card = vm.HitTest(vm.ToWorldX(at.X), vm.ToWorldY(at.Y));

        if (card is null)
        {
            _gesture = Gesture.Marquee;
            vm.BeginMarquee(at.X, at.Y);
        }
        else if (e.ClickCount == 2)
        {
            // The second click only opens the file - and deliberately starts no drag, or the
            // card would creep across the canvas while the panel was opening.
            _gesture = Gesture.None;
            _ = vm.OpenCodeAsync(card);
            e.Handled = true;
            return;
        }
        else
        {
            // Selecting before beginning the drag is deliberate: a press on an unselected
            // card makes it the selection, and a press on one of several drags all of them.
            _gesture = Gesture.Press;
            vm.Click(card, _ctrlDown);
            vm.BeginDrag(card);
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var vm = ViewModel;

        if (vm is null)
        {
            return;
        }

        var at = e.GetPosition(this);

        if (_gesture == Gesture.Press &&
            (Math.Abs(at.X - _pressed.X) >= DragThreshold || Math.Abs(at.Y - _pressed.Y) >= DragThreshold))
        {
            _gesture = Gesture.Drag;
        }

        switch (_gesture)
        {
            case Gesture.Drag:
                // Divided by the zoom, so a card stays under the pointer at any magnification.
                vm.DragBy((at.X - _last.X) / vm.Zoom, (at.Y - _last.Y) / vm.Zoom);
                break;

            case Gesture.Marquee:
                vm.UpdateMarquee(at.X, at.Y);
                InvalidateVisual();
                break;

            case Gesture.Pan:
                vm.Pan(at.X - _last.X, at.Y - _last.Y);
                break;

            case Gesture.Press:
                break;

            default:
                UpdateHover(vm, at);
                break;
        }

        _last = at;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var gesture = _gesture;
        _gesture = Gesture.None;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;

        var vm = ViewModel;

        if (vm is null)
        {
            return;
        }

        switch (gesture)
        {
            case Gesture.Marquee:
                vm.EndMarquee(_ctrlDown);
                break;

            case Gesture.Press:
            case Gesture.Drag:
                _ = vm.EndDragAsync();
                break;
        }

        // The pointer may have ended up over a different card than it started on.
        UpdateHover(vm, e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        ClearHover();
        base.OnPointerExited(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var vm = ViewModel;

        if (vm is null)
        {
            return;
        }

        var at = e.GetPosition(this);

        // About the pointer rather than the centre: the thing a person wants a closer look
        // at is the thing they are pointing at.
        vm.ZoomAt(Math.Pow(ZoomPerNotch, e.Delta.Y), at.X, at.Y);
        UpdateHover(vm, at);

        e.Handled = true;
    }

    private void UpdateHover(CanvasViewModel vm, Point at)
    {
        if (vm.Tool == CanvasTool.Pan)
        {
            // Nothing answers a left click in this mode, so nothing should light up as though
            // it would.
            ClearHover();
            return;
        }

        var card = vm.HitTest(vm.ToWorldX(at.X), vm.ToWorldY(at.Y));

        if (ReferenceEquals(card, _hovered))
        {
            return;
        }

        if (_hovered is not null)
        {
            _hovered.IsHovered = false;
        }

        _hovered = card;

        if (card is not null)
        {
            card.IsHovered = true;
        }

        Cursor = card is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
        InvalidateVisual();
    }

    private void ClearHover()
    {
        if (_hovered is not null)
        {
            _hovered.IsHovered = false;
            _hovered = null;
            InvalidateVisual();
        }

        if (_gesture == Gesture.None)
        {
            Cursor = Cursor.Default;
        }
    }

    /// <summary>
    /// Tracks Ctrl for the additive selection. The surface keeps focus while a gesture runs,
    /// so key events land here reliably; reading a global keyboard state from a control is
    /// not an API Avalonia offers, and event args do not carry modifiers on every move.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            _ctrlDown = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            _ctrlDown = false;
        }

        base.OnKeyUp(e);
    }
}
