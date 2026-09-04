namespace AIClient.Application.DTOs;

/// <summary>
/// The spatial half of the projection: where things are, how big, and where the camera is.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this file is a fact about a project. Every type here can be deleted along with its
/// three tables and the graph still knows every node, every relation, every source reference and
/// the whole history of how it got that way - the projection would simply lay the nodes out again
/// on next open. That is the principle stated as a data model rather than as a comment.
/// </para>
/// <para>
/// Geometry is plain <see cref="double"/> rather than <c>Point</c> or <c>Rect</c> because this
/// assembly targets <c>net10.0</c>, where those types do not exist. The restriction is the point:
/// it makes the camera and the layout ordinary arithmetic that a test can check without a window,
/// and it makes it impossible for a WPF concept to drift down into the model by accident.
/// </para>
/// </remarks>
public static class CanvasLayoutMode
{
    /// <summary>Containment laid out left to right, parents centred on their children.</summary>
    public const string Tree = "tree";

    /// <summary>Rows and columns, for a graph with no useful containment.</summary>
    public const string Grid = "grid";

    /// <summary>Auto-layout only fills gaps; existing positions are never touched.</summary>
    public const string Manual = "manual";
}

/// <summary>
/// Sizes the layout and the surface have to agree on.
/// </summary>
/// <remarks>
/// A node card carries an icon, a name, a kind and a source line, which sets the width; anything
/// narrower truncates the one string a person navigates by. The gaps are what keeps an edge
/// readable at the zoom where a whole folder fits on screen.
/// </remarks>
public static class CanvasMetrics
{
    public const double NodeWidth = 220;
    public const double NodeHeight = 68;
    public const double CollapsedHeight = 40;

    /// <summary>Distance between one containment level and the next.</summary>
    public const double LevelGap = 96;

    /// <summary>Distance between two siblings.</summary>
    public const double SiblingGap = 24;

    /// <summary>Breathing room left around content when fitting the camera to it.</summary>
    public const double FitPadding = 64;

    /// <summary>
    /// How many cards a run of siblings may stack before it wraps into another column.
    /// </summary>
    /// <remarks>
    /// A folder of forty files in a single column is a strip four times taller than any screen, and a
    /// project of such folders is a ribbon nobody can navigate. Wrapping trades a little horizontal
    /// room, of which a canvas has plenty, for a shape that fits one. Six is about as far as the eye
    /// follows a column without losing its place.
    /// </remarks>
    public const int WrapColumnAt = 6;
}

/// <summary>An axis-aligned rectangle in graph space.</summary>
/// <remarks>
/// Enough of <c>Rect</c> to fit the camera to content and to decide which cards are worth
/// realising, and no more. <see cref="Empty"/> is a distinct value rather than a zero-sized
/// rectangle at the origin, because "no content" and "one node at (0,0)" call for different
/// answers from <see cref="CanvasViewport.Fit"/>.
/// </remarks>
public readonly record struct CanvasBounds(double X, double Y, double Width, double Height)
{
    /// <summary>No content at all.</summary>
    public static CanvasBounds Empty { get; } = new(0, 0, -1, -1);

    public bool IsEmpty => Width < 0 || Height < 0;

    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + (Width / 2);
    public double CenterY => Y + (Height / 2);

    /// <summary>The smallest rectangle containing both.</summary>
    public CanvasBounds Union(CanvasBounds other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        var left = Math.Min(Left, other.Left);
        var top = Math.Min(Top, other.Top);

        return new CanvasBounds(
            left,
            top,
            Math.Max(Right, other.Right) - left,
            Math.Max(Bottom, other.Bottom) - top);
    }

    /// <summary>The same rectangle grown by <paramref name="margin"/> on every side.</summary>
    public CanvasBounds Inflate(double margin) => IsEmpty
        ? this
        : new CanvasBounds(X - margin, Y - margin, Width + (2 * margin), Height + (2 * margin));

    /// <summary>Whether the two rectangles share any area. Touching edges count.</summary>
    public bool Intersects(CanvasBounds other) =>
        !IsEmpty && !other.IsEmpty &&
        Left <= other.Right && Right >= other.Left &&
        Top <= other.Bottom && Bottom >= other.Top;

    /// <summary>Whether the point lies inside, edges included.</summary>
    public bool Contains(double x, double y) =>
        !IsEmpty && x >= Left && x <= Right && y >= Top && y <= Bottom;

    /// <summary>The rectangle spanned by a set of others.</summary>
    public static CanvasBounds Around(IEnumerable<CanvasBounds> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var result = Empty;

        foreach (var part in parts)
        {
            result = result.Union(part);
        }

        return result;
    }

    /// <summary>The rectangle spanned by two corners, in any order.</summary>
    public static CanvasBounds Between(double x1, double y1, double x2, double y2) => new(
        Math.Min(x1, x2),
        Math.Min(y1, y2),
        Math.Abs(x2 - x1),
        Math.Abs(y2 - y1));
}

/// <summary>
/// Where the camera is: a pan in screen pixels and a scale, mapping graph space to the surface.
/// </summary>
/// <remarks>
/// <para>
/// The convention throughout is <c>screen = world * Zoom + Pan</c>, which is exactly what a WPF
/// <c>ScaleTransform</c> followed by a <c>TranslateTransform</c> does. Keeping the arithmetic here
/// rather than in the view means the two operations that are easy to get subtly wrong - zooming
/// about the cursor and fitting to content - are covered by tests that need no window, and the
/// view is left with nothing but event plumbing.
/// </para>
/// <para>
/// Values are clamped rather than validated. A camera is driven by a mouse wheel and a restored
/// database row, so out-of-range input is normal traffic; refusing it would mean a corrupted row
/// leaves a user with an unusable surface and no way back.
/// </para>
/// </remarks>
public readonly record struct CanvasViewport(double PanX, double PanY, double Zoom)
{
    /// <summary>
    /// Far enough out to see the shape of a real project; a card is a smudge long before this.
    /// </summary>
    /// <remarks>
    /// Chosen against the case it has to survive rather than against what reads well. A few hundred
    /// file nodes are several thousand world pixels tall, and a floor that cannot contain them turns
    /// "Fit to content" into a lie: it clamps, and the content still runs off both edges. Nothing is
    /// legible down here, which is the point - this end of the range is for orientation, and the way
    /// back is a scroll wheel.
    /// </remarks>
    public const double MinZoom = 0.06;

    /// <summary>Close enough to read a card comfortably on a dense display.</summary>
    public const double MaxZoom = 4.0;

    /// <summary>The origin at 1:1.</summary>
    public static CanvasViewport Default { get; } = new(0, 0, 1);

    /// <summary>The same camera with non-finite values dropped and the zoom forced into range.</summary>
    public CanvasViewport Normalized() => new(
        double.IsFinite(PanX) ? PanX : 0,
        double.IsFinite(PanY) ? PanY : 0,
        double.IsFinite(Zoom) ? Math.Clamp(Zoom, MinZoom, MaxZoom) : 1);

    public double ToScreenX(double worldX) => (worldX * Zoom) + PanX;

    public double ToScreenY(double worldY) => (worldY * Zoom) + PanY;

    public double ToWorldX(double screenX) => Zoom <= 0 ? screenX : (screenX - PanX) / Zoom;

    public double ToWorldY(double screenY) => Zoom <= 0 ? screenY : (screenY - PanY) / Zoom;

    /// <summary>Dragging the surface: the world moves with the pointer, one pixel for one pixel.</summary>
    public CanvasViewport Panned(double dx, double dy) =>
        new(PanX + dx, PanY + dy, Zoom);

    /// <summary>
    /// Scales by <paramref name="factor"/> while the graph point under the given screen position
    /// stays exactly where it is.
    /// </summary>
    /// <remarks>
    /// The whole feel of a wheel zoom is this fixed point. Scaling about the top-left corner
    /// instead - which is what a bare <c>ScaleTransform</c> does - sends whatever the user was
    /// looking at off screen, and they spend the next second panning it back.
    /// </remarks>
    public CanvasViewport ZoomedAt(double factor, double screenX, double screenY)
    {
        var current = Normalized();

        if (!double.IsFinite(factor) || factor <= 0)
        {
            return current;
        }

        var target = Math.Clamp(current.Zoom * factor, MinZoom, MaxZoom);

        if (target == current.Zoom)
        {
            return current;
        }

        var worldX = current.ToWorldX(screenX);
        var worldY = current.ToWorldY(screenY);

        return new CanvasViewport(
            screenX - (worldX * target),
            screenY - (worldY * target),
            target);
    }

    /// <summary>The region of graph space a surface of this size is showing.</summary>
    public CanvasBounds VisibleWorld(double surfaceWidth, double surfaceHeight)
    {
        if (surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            return CanvasBounds.Empty;
        }

        var camera = Normalized();

        return CanvasBounds.Between(
            camera.ToWorldX(0),
            camera.ToWorldY(0),
            camera.ToWorldX(surfaceWidth),
            camera.ToWorldY(surfaceHeight));
    }

    /// <summary>Centres the content and scales it to fill the surface, never past 1:1.</summary>
    /// <remarks>
    /// Not zooming past 1:1 is what makes "fit" usable on a graph of three nodes: magnified to fill
    /// a window, three cards look like a mistake. Growing to fill the space is the job of the
    /// layout, not the camera.
    /// </remarks>
    public static CanvasViewport Fit(
        CanvasBounds content,
        double surfaceWidth,
        double surfaceHeight,
        double padding = CanvasMetrics.FitPadding)
    {
        if (content.IsEmpty || surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            return Default;
        }

        var padded = content.Inflate(padding);

        var scale = Math.Min(
            surfaceWidth / Math.Max(padded.Width, 1),
            surfaceHeight / Math.Max(padded.Height, 1));

        var zoom = Math.Clamp(Math.Min(scale, 1), MinZoom, MaxZoom);

        return new CanvasViewport(
            (surfaceWidth / 2) - (padded.CenterX * zoom),
            (surfaceHeight / 2) - (padded.CenterY * zoom),
            zoom);
    }

    /// <summary>Brings the content to the middle of the surface without changing the scale.</summary>
    public CanvasViewport Centered(CanvasBounds content, double surfaceWidth, double surfaceHeight)
    {
        if (content.IsEmpty || surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            return this;
        }

        var camera = Normalized();

        return new CanvasViewport(
            (surfaceWidth / 2) - (content.CenterX * camera.Zoom),
            (surfaceHeight / 2) - (content.CenterY * camera.Zoom),
            camera.Zoom);
    }
}

/// <summary>Where one node sits in one view, and how it is drawn there.</summary>
/// <remarks>
/// Keyed by node rather than by row id: a node has at most one place in a view, and treating that
/// pair as the identity is what lets a save be an upsert and a re-index leave positions alone.
/// <see cref="Accent"/> is a theme key such as <c>"amber"</c>, never a colour literal, so a
/// placement made in the dark theme still looks deliberate in the light one.
/// </remarks>
public sealed record CanvasPlacement
{
    public required Guid NodeId { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }

    /// <summary>Overridden size, or null to use <see cref="CanvasMetrics"/>.</summary>
    public double? Width { get; init; }
    public double? Height { get; init; }

    public bool IsCollapsed { get; init; }

    /// <summary>Theme key for the card's accent, or null for the default.</summary>
    public string? Accent { get; init; }

    /// <summary>Set when the user has placed this node deliberately; auto-layout leaves it alone.</summary>
    public bool IsPinned { get; init; }

    /// <summary>The rectangle this placement occupies, using the default size when none is set.</summary>
    public CanvasBounds Bounds => new(
        X,
        Y,
        Width ?? CanvasMetrics.NodeWidth,
        Height ?? (IsCollapsed ? CanvasMetrics.CollapsedHeight : CanvasMetrics.NodeHeight));

    public static CanvasPlacement At(Guid nodeId, double x, double y) =>
        new() { NodeId = nodeId, X = x, Y = y };
}

/// <summary>A titled frame drawn behind part of a view.</summary>
/// <remarks>
/// Deliberately two-sided. The grouping a person means - "Authentication" - is a graph node of kind
/// <c>Component</c> with <c>Groups</c> edges, because the model has to be able to reason about it;
/// this record is only the rectangle drawn around it, and <see cref="GroupNodeId"/> is the link
/// between them. An area with no group node is legitimate: it is a visual divider and nothing more,
/// which is why it may be deleted with the rest of the spatial state at no cost.
/// </remarks>
public sealed record CanvasArea
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }

    /// <summary>The <c>Component</c> node this frame stands for, when it stands for one.</summary>
    public Guid? GroupNodeId { get; init; }

    public string? Accent { get; init; }
    public int Order { get; init; }

    public CanvasBounds Bounds => new(X, Y, Width, Height);
}

/// <summary>One saved way of looking at the graph.</summary>
/// <remarks>
/// <see cref="RootNodeId"/> together with <see cref="Depth"/> is how "go inside this node" works
/// without an infinite canvas: entering a folder opens a view rooted at it rather than scrolling
/// further across one unbounded surface. The default view has no root, and shows the whole graph.
/// </remarks>
public sealed record CanvasViewState
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The node this view descends from, or null for the whole graph.</summary>
    public Guid? RootNodeId { get; init; }

    /// <summary>Containment levels shown below the root.</summary>
    public int Depth { get; init; } = 2;

    public CanvasViewport Viewport { get; init; } = CanvasViewport.Default;

    /// <summary>One of the <see cref="CanvasLayoutMode"/> constants.</summary>
    public string LayoutMode { get; init; } = CanvasLayoutMode.Tree;

    public IReadOnlyList<CanvasPlacement> Placements { get; init; } = [];
    public IReadOnlyList<CanvasArea> Areas { get; init; } = [];
}
