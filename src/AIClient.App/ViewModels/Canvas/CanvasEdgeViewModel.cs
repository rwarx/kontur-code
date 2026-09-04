using System.Windows;
using System.Windows.Media;
using AIClient.Application.DTOs;
using AIClient.Domain.Graph;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIClient.App.ViewModels.Canvas;

/// <summary>
/// One relationship drawn between two cards.
/// </summary>
/// <remarks>
/// <para>
/// The curve is exposed as a finished <see cref="Geometry"/> rather than as eight coordinates
/// bound into a path in markup. WPF types are legal in this layer, a frozen geometry is cheaper
/// for the render thread than a live <c>PathGeometry</c> rebuilt by the binding engine, and it
/// keeps the drawing rule in one readable method instead of spread across XAML attributes.
/// </para>
/// <para>
/// There are no ports and no sockets. An edge attaches to the side of a card that faces the other
/// card, because the relationship is "AuthService depends on UserRepository" and not a wire
/// carrying data from an output to an input.
/// </para>
/// </remarks>
public sealed partial class CanvasEdgeViewModel : ObservableObject
{
    /// <summary>Shortest horizontal pull on a control point, so short edges still curve a little.</summary>
    private const double MinCurve = 24;

    /// <summary>Longest pull, so a very wide edge does not bow across half the canvas.</summary>
    private const double MaxCurve = 120;

    private const double ArrowLength = 9;
    private const double ArrowHalfWidth = 4;

    [ObservableProperty]
    private Geometry _curve = Geometry.Empty;

    [ObservableProperty]
    private Geometry _arrow = Geometry.Empty;

    /// <summary>An endpoint is selected. Highlighted edges are drawn brighter and show their kind.</summary>
    [ObservableProperty]
    private bool _isHighlighted;

    [ObservableProperty]
    private double _labelX;

    [ObservableProperty]
    private double _labelY;

    public CanvasEdgeViewModel(GraphEdge edge, CanvasNodeViewModel from, CanvasNodeViewModel to)
    {
        Id = edge.Id;
        Edge = edge;
        From = from;
        To = to;
        KindLabel = string.IsNullOrWhiteSpace(edge.Label)
            ? CanvasKindVisuals.LabelOf(edge.Kind.Value)
            : edge.Label!;

        Refresh();
    }

    public Guid Id { get; }

    public GraphEdge Edge { get; private set; }

    public CanvasNodeViewModel From { get; }

    public CanvasNodeViewModel To { get; }

    /// <summary>"Depends On", "Contains" - the edge kind in a form worth showing a person.</summary>
    public string KindLabel { get; private set; }

    /// <summary>The relationship as a sentence, for anything reading the window rather than seeing it.</summary>
    /// <remarks>
    /// Surfaced through <see cref="ToString"/> because the only automation element an edge has is the
    /// item peer of the <c>ItemsControl</c> drawing it, and that peer names itself from the item.
    /// </remarks>
    public string AutomationName => $"{From.Title} {KindLabel} {To.Title}";

    /// <summary>The rectangle the curve occupies, for culling.</summary>
    public CanvasBounds Bounds { get; private set; }

    /// <summary>Picks up a changed label or kind without losing the object identity.</summary>
    public void Apply(GraphEdge edge)
    {
        Edge = edge;
        KindLabel = string.IsNullOrWhiteSpace(edge.Label)
            ? CanvasKindVisuals.LabelOf(edge.Kind.Value)
            : edge.Label!;

        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(AutomationName));
    }

    /// <summary>
    /// Recomputes the curve from the current positions of both cards.
    /// </summary>
    /// <remarks>
    /// Called by the canvas after a drag or a layout rather than driven by property change
    /// notifications on the endpoints: a drag moves every selected card at once, and one explicit
    /// pass over the affected edges is far less work than two subscriptions per edge all firing
    /// mid-gesture.
    /// </remarks>
    public void Refresh()
    {
        // Leave from the side that faces the other card. Anything else produces a curve that
        // doubles back on itself the moment a dependency points leftwards.
        var leftToRight = To.CenterX >= From.CenterX;

        var startX = leftToRight ? From.X + From.Width : From.X;
        var endX = leftToRight ? To.X : To.X + To.Width;
        var startY = From.CenterY;
        var endY = To.CenterY;

        var pull = Math.Clamp(Math.Abs(endX - startX) * 0.5, MinCurve, MaxCurve);
        var direction = leftToRight ? 1 : -1;

        var c1 = new Point(startX + (pull * direction), startY);
        var c2 = new Point(endX - (pull * direction), endY);
        var start = new Point(startX, startY);
        var end = new Point(endX, endY);

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment(c1, c2, end, isStroked: true));

        var curve = new PathGeometry();
        curve.Figures.Add(figure);
        curve.Freeze();
        Curve = curve;

        Arrow = BuildArrow(end, direction);

        // The cubic at t = 0.5 reduces to this weighted average - cheap, and exact enough to hang
        // a label on.
        LabelX = ((start.X + (3 * c1.X) + (3 * c2.X) + end.X) / 8) - 40;
        LabelY = ((start.Y + (3 * c1.Y) + (3 * c2.Y) + end.Y) / 8) - 18;

        Bounds = new CanvasBounds(
            Math.Min(start.X, end.X) - MaxCurve,
            Math.Min(start.Y, end.Y) - 8,
            Math.Abs(end.X - start.X) + (2 * MaxCurve),
            Math.Abs(end.Y - start.Y) + 16);
    }

    /// <summary>The edge's accessible name. See <see cref="AutomationName"/> for why this is the hook.</summary>
    public override string ToString() => AutomationName;

    /// <summary>A filled triangle at the target end, pointing the way the relationship reads.</summary>
    private static Geometry BuildArrow(Point tip, int direction)
    {
        var back = tip.X - (ArrowLength * direction);

        var figure = new PathFigure { StartPoint = tip, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(new Point(back, tip.Y - ArrowHalfWidth), isStroked: false));
        figure.Segments.Add(new LineSegment(new Point(back, tip.Y + ArrowHalfWidth), isStroked: false));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        return geometry;
    }
}
