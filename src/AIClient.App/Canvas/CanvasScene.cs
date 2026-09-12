using System.Windows;
using System.Windows.Media;
using AIClient.Domain.Graph;

namespace AIClient.App.Canvas;

/// <summary>
/// The canvas's scene graph: node and edge visuals, the spatial index, and every drawing
/// routine. This is the "render model" stage of the pipeline - it knows graph snapshots
/// and pixels, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Culling.</b> Visuals exist for every node in the graph, but only those intersecting
/// the viewport are attached to the visual tree. The set is synchronised on every viewport
/// change, so a 10,000-node graph renders as fast as the few hundred visible in it. Detached
/// visuals keep their drawings, so panning back costs an attach, not a redraw.
/// </para>
/// <para>
/// <b>Rendering scale.</b> All node content is drawn in world units; the viewport's
/// scale/translate is a single transform on the content root, so zoom re-composes rather
/// than re-rasterises, and text stays crisp at any zoom because glyphs are vector content
/// re-rendered at the composed size.
/// </para>
/// <para>
/// <b>Pens are zoom-independent.</b> Every width here is a world-space constant, exactly as
/// it was when the cards were <c>Border</c>s and the edges were <c>Path</c>s under a
/// <c>ScaleTransform</c>: a stroke thickens with the zoom and nothing has to be re-recorded
/// when the camera moves. Dividing widths by zoom - the obvious way to keep a hairline one
/// device pixel wide - makes every drawing a function of the zoom, so a single wheel notch
/// invalidates the whole scene and the canvas stutters and tears while it rebuilds. The
/// crispness is not worth that, so pens are built once per palette and frozen.
/// </para>
/// </remarks>
public sealed class CanvasScene
{
    // Card metrics, taken from the retained-mode template this renderer replaced so the
    // cards read identically: 8px corner, a 1px neutral border, the kind's colour as a 3px
    // strip on the left, 12px of breathing room, 13/11px title and subtitle.
    private const double TitleFontSize = 13;
    private const double MetaFontSize = 11;
    private const double MetaGap = 3;
    private const double GlyphSize = 12;
    private const double GlyphGap = 7;
    private const double ContentPadding = 12;
    private const double KindStripWidth = 3;
    private const double CardBorderWidth = 1;
    private const double NodeRadius = 8;

    // The selection halo sat outside the card as its own 2px border, inflated by 3, so the
    // card's own edge could stay a single pixel. A centred pen reproduces that ring when its
    // geometry sits half a stroke inside the outer edge.
    private const double HaloInflate = 2;
    private const double HaloWidth = 2;

    // Edges: thin by decree, a little thicker when they matter.
    private const double EdgeWidth = 1.2;
    private const double EdgeHighlightWidth = 1.8;

    /// <summary>Shortest horizontal pull on a control point, so short edges still curve a little.</summary>
    private const double MinCurve = 24;

    /// <summary>Longest pull, so a very wide edge does not bow across half the canvas.</summary>
    private const double MaxCurve = 120;

    private const double ArrowLength = 9;
    private const double ArrowHalfWidth = 4;

    private const double EdgeLabelWidth = 80;
    private const double EdgeLabelFontSize = 10;

    private const double EdgeHitRadius = 7;

    private readonly Dictionary<string, NodeVisual> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EdgeVisual> _edges = new(StringComparer.Ordinal);
    private readonly SpatialIndex _index = new();

    private CanvasPalette _palette = new()
    {
        Background = Brushes.Transparent,
        NodeBody = Brushes.Transparent,
        NodeBodyHover = Brushes.Transparent,
        NodeBodySelected = Brushes.Transparent,
        NodeBorder = Brushes.Gray,
        NodeBorderSelected = Brushes.Gray,
        NodeTitle = Brushes.White,
        NodeMeta = Brushes.Gray,
        Edge = Brushes.Gray,
        EdgeHover = Brushes.Gray,
        EdgeSelected = Brushes.Gray,
        EdgeDimmed = Brushes.Transparent,
        Accent = Brushes.Gray,
        SelectionGlow = Brushes.Gray,
    };

    private double _pixelsPerDip = 1.0;

    // Every pen the renderer uses, built once per palette and frozen. Nothing here depends
    // on the zoom, so a camera move never invalidates them.
    private readonly Dictionary<GraphNodeKind, Pen> _iconPens = [];
    private Pen _nodeBorderPen = null!;
    private Pen _nodeBorderSelectedPen = null!;
    private Pen _haloPen = null!;
    private Pen _edgePen = null!;
    private Pen _edgeHighlightPen = null!;
    private Pen _edgeDimPen = null!;
    private Brush _dimScrim = null!;
    private Brush _edgeLabelPlate = null!;

    public CanvasScene() => RebuildPens();

    public int NodeCount => _nodes.Count;

    public int EdgeCount => _edges.Count;

    public SpatialIndex Index => _index;

    public event EventHandler? SceneInvalidated;

    /// <summary>Applies a whole snapshot when the delta route is not worth computing (load, restore).</summary>
    public void Reset(GraphSnapshot snapshot)
    {
        _nodes.Clear();
        _edges.Clear();
        _index.Rebuild(snapshot.Nodes);

        foreach (var node in snapshot.Nodes)
        {
            var visual = new NodeVisual
            {
                Visual = new DrawingVisual(),
                Node = node,
            };

            PositionNode(visual, node);
            _nodes[node.Id] = visual;
        }

        foreach (var edge in snapshot.Edges)
        {
            _edges[edge.Id] = CreateEdge(edge);
        }

        SceneInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies a projection delta: the only path a normal graph change takes.</summary>
    public void Apply(GraphProjection.Delta delta, GraphSnapshot snapshot)
    {
        foreach (var node in delta.RemovedNodeIds)
        {
            if (_nodes.Remove(node.Id, out var visual))
            {
                visual.IsAttached = false;
            }

            _index.Remove(node.Id);
        }

        foreach (var id in delta.RemovedEdgeIds)
        {
            if (_edges.Remove(id, out var visual))
            {
                visual.IsAttached = false;
            }
        }

        foreach (var node in delta.AddedNodes)
        {
            var visual = new NodeVisual
            {
                Visual = new DrawingVisual(),
                Node = node,
            };

            PositionNode(visual, node);
            _nodes[node.Id] = visual;
            _index.Insert(node);
        }

        foreach (var node in delta.MovedNodes)
        {
            if (_nodes.TryGetValue(node.Id, out var visual))
            {
                var prior = visual.Node;

                visual.Node = node;
                PositionNode(visual, node);
                _index.Update(node);
                MarkIncidentEdgesDirty(node.Id);

                if (prior.Width != node.Width || prior.Height != node.Height)
                {
                    visual.IsDirty = true;
                }
            }
            else
            {
                var created = new NodeVisual
                {
                    Visual = new DrawingVisual(),
                    Node = node,
                };

                PositionNode(created, node);
                _nodes[node.Id] = created;
                _index.Insert(node);
            }
        }

        foreach (var node in delta.ChangedNodes)
        {
            if (_nodes.TryGetValue(node.Id, out var visual))
            {
                visual.Node = node;
                visual.IsDirty = true;
                _index.Update(node);
                MarkIncidentEdgesDirty(node.Id);
            }
        }

        foreach (var edge in delta.AddedEdges)
        {
            _edges[edge.Id] = CreateEdge(edge);
        }

        foreach (var edge in delta.ChangedEdges)
        {
            _edges[edge.Id] = CreateEdge(edge);
        }

        SceneInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-resolves colours after a theme change and redraws everything.</summary>
    public void SetPalette(CanvasPalette palette, double pixelsPerDip)
    {
        _palette = palette;
        _pixelsPerDip = pixelsPerDip;
        RebuildPens();

        foreach (var visual in _nodes.Values)
        {
            visual.IsDirty = true;
        }

        foreach (var visual in _edges.Values)
        {
            visual.IsDirty = true;
        }

        SceneInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public void SetPixelsPerDip(double pixelsPerDip)
    {
        if (Math.Abs(_pixelsPerDip - pixelsPerDip) < 0.01)
        {
            return;
        }

        _pixelsPerDip = pixelsPerDip;

        foreach (var visual in _nodes.Values)
        {
            visual.IsDirty = true;
        }
    }

    /// <summary>
    /// Rebuilds the frozen pen cache from the current palette.
    /// </summary>
    /// <remarks>
    /// Freezing matters more here than anywhere else in the app: a frozen pen skips change
    /// notification and can be shared by every visual, and these are handed to a
    /// <see cref="DrawingContext"/> hundreds of times per redraw.
    /// </remarks>
    private void RebuildPens()
    {
        _nodeBorderPen = Frozen(new Pen(_palette.NodeBorder, CardBorderWidth) { LineJoin = PenLineJoin.Round });
        _nodeBorderSelectedPen = Frozen(new Pen(_palette.NodeBorderSelected, CardBorderWidth) { LineJoin = PenLineJoin.Round });
        _haloPen = Frozen(new Pen(_palette.SelectionGlow, HaloWidth) { LineJoin = PenLineJoin.Round });

        _edgePen = Frozen(EdgePen(_palette.Edge, EdgeWidth));
        _edgeHighlightPen = Frozen(EdgePen(_palette.EdgeSelected, EdgeHighlightWidth));
        _edgeDimPen = Frozen(EdgePen(_palette.EdgeDimmed, EdgeWidth));

        var scrim = new SolidColorBrush(Color.FromArgb(140, 12, 13, 15));
        scrim.Freeze();
        _dimScrim = scrim;

        // The label plate is the canvas surface itself, so the curve it covers disappears
        // under it instead of showing through the text.
        _edgeLabelPlate = _palette.Background;

        _iconPens.Clear();

        foreach (var (kind, brush) in _palette.KindStrokes)
        {
            // The icon geometries are authored on a 16px grid and drawn scaled down, so the
            // stroke has to be divided by that scale to come out at 1.5 world units.
            var pen = new Pen(brush, 1.5 / (GlyphSize / 16))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };

            _iconPens[kind] = Frozen(pen);
        }

        static Pen EdgePen(Brush brush, double width) => new(brush, width)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        static Pen Frozen(Pen pen)
        {
            pen.Freeze();
            return pen;
        }
    }

    // ------------------------------------------------------------ rendering

    /// <summary>Draws one node visual if dirty. Callers drive this per-culled-visual.</summary>
    /// <remarks>
    /// Nothing in here reads the zoom: the drawing is world-space content under one transform,
    /// so a card is recorded when its data or its state changes and at no other time.
    /// </remarks>
    public void RenderNode(NodeVisual nodeVisual)
    {
        if (!nodeVisual.IsDirty)
        {
            return;
        }

        nodeVisual.IsDirty = false;

        var node = nodeVisual.Node;
        var state = nodeVisual.State;
        var palette = _palette;

        using var context = nodeVisual.Visual.RenderOpen();

        // The card. Fill is a state wash; the border stays neutral whatever the kind, so a
        // canvas of two hundred cards does not turn into a paint chart - the kind speaks
        // through the strip and the glyph instead.
        var bodyBrush = state.HasFlag(NodeRenderState.Selected) ? palette.NodeBodySelected
            : state.HasFlag(NodeRenderState.Hovered) ? palette.NodeBodyHover
            : palette.NodeBody;

        var kindBrush = palette.KindStrokes.TryGetValue(node.Kind, out var kindStroke)
            ? kindStroke
            : palette.NodeBorder;

        var bodyRect = new Rect(-node.Width / 2, -node.Height / 2, node.Width, node.Height);

        // Selection halo: a ring outside the card, so the card's own edge can stay a single
        // pixel. One of the four sanctioned glow sites in the product.
        if (state.HasFlag(NodeRenderState.Selected))
        {
            // Rect.Inflate is an instance mutator on a struct: take a copy, grow it.
            var haloRect = bodyRect;
            haloRect.Inflate(HaloInflate, HaloInflate);

            context.DrawRoundedRectangle(
                null, _haloPen, haloRect, NodeRadius + HaloInflate, NodeRadius + HaloInflate);
        }

        var borderPen = state.HasFlag(NodeRenderState.Selected) ? _nodeBorderSelectedPen : _nodeBorderPen;
        context.DrawRoundedRectangle(bodyBrush, borderPen, bodyRect, NodeRadius, NodeRadius);

        // The kind strip, clipped to the card so it follows the rounded left corners exactly
        // the way a Border with a 8,0,0,8 radius did.
        var inner = bodyRect;
        inner.Inflate(-CardBorderWidth, -CardBorderWidth);

        var clip = new RectangleGeometry(inner, NodeRadius - CardBorderWidth, NodeRadius - CardBorderWidth);
        clip.Freeze();

        context.PushClip(clip);
        context.DrawRectangle(kindBrush, null, new Rect(inner.X, inner.Y, KindStripWidth, inner.Height));
        context.Pop();

        DrawNodeContent(context, node, inner);

        // Dimming: a translucent scrim over the whole card rather than a second set of
        // colours. One rect, no text re-render.
        if (state.HasFlag(NodeRenderState.Dimmed))
        {
            context.DrawRoundedRectangle(_dimScrim, null, bodyRect, NodeRadius, NodeRadius);
        }
    }

    /// <summary>
    /// The glyph, title and subtitle: one row of icon plus title, one row of metadata under
    /// it, the pair vertically centred in the card the way the template's centred grid was.
    /// </summary>
    /// <remarks>
    /// <see cref="FormattedText"/> is the most expensive thing this renderer builds, which is
    /// the whole reason the node visual is recorded on change rather than per frame.
    /// </remarks>
    private void DrawNodeContent(DrawingContext context, GraphNode node, Rect inner)
    {
        var palette = _palette;
        var contentLeft = inner.X + KindStripWidth + ContentPadding;
        var textLeft = contentLeft + GlyphSize + GlyphGap;
        var textWidth = inner.Right - ContentPadding - textLeft;

        if (textWidth < 8)
        {
            return;
        }

        var title = Text(FitText(node.Title, textWidth), palette.NodeTitleFace, TitleFontSize, palette.NodeTitle);

        var meta = node.Subtitle ?? node.Kind.ToString().ToLowerInvariant();
        var subtitle = string.IsNullOrEmpty(meta)
            ? null
            : Text(FitText(meta, textWidth), palette.NodeMetaFace, MetaFontSize, palette.NodeMeta);

        var blockHeight = title.Height + (subtitle is null ? 0 : MetaGap + subtitle.Height);

        // A card too short for both lines keeps the title, which is what a centred grid in a
        // clipping border came to anyway.
        if (subtitle is not null && blockHeight > inner.Height)
        {
            subtitle = null;
            blockHeight = title.Height;
        }

        var top = inner.Y + ((inner.Height - blockHeight) / 2);

        if (palette.KindIcons.TryGetValue(node.Kind, out var icon)
            && _iconPens.TryGetValue(node.Kind, out var iconPen))
        {
            var scale = GlyphSize / 16;

            // The shared icon geometries are frozen, so the transform is pushed on the
            // context rather than baked into the geometry - mutating a frozen geometry
            // throws, and pushing a transform costs nothing.
            context.PushTransform(new MatrixTransform(
                scale, 0, 0, scale, contentLeft, top + ((title.Height - GlyphSize) / 2)));
            context.DrawGeometry(null, iconPen, icon);
            context.Pop();
        }

        context.DrawText(title, new Point(textLeft, top));

        if (subtitle is not null)
        {
            context.DrawText(subtitle, new Point(textLeft, top + title.Height + MetaGap));
        }

        FormattedText Text(string value, Typeface face, double size, Brush brush) => new(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            face,
            size,
            brush,
            _pixelsPerDip);
    }

    /// <summary>Draws one edge visual if dirty.</summary>
    /// <param name="previewPositions">Drag-time node centres, keyed by node id, overriding the snapshot.</param>
    public void RenderEdge(
        EdgeVisual edgeVisual,
        GraphSnapshot snapshot,
        IReadOnlyDictionary<string, Point>? previewPositions = null)
    {
        // A drag is the one case where an edge is stale without anything having marked it so:
        // its endpoints are moving under a preview the graph has not been told about yet.
        // Every other call is a no-op unless something actually changed, which is what keeps
        // panning and zooming free of work.
        if (!edgeVisual.IsDirty && previewPositions is null)
        {
            return;
        }

        edgeVisual.IsDirty = false;

        if (!snapshot.TryGetNode(edgeVisual.SourceId, out var source)
            || !snapshot.TryGetNode(edgeVisual.TargetId, out var target))
        {
            using var clear = edgeVisual.Visual.RenderOpen();
            return;
        }

        var state = edgeVisual.State;

        // A dragged node's centre comes from the preview; its rectangle still uses the
        // snapshot size, which is correct because drags do not resize.
        var sourceCentre = previewPositions is not null && previewPositions.TryGetValue(source.Id, out var previewSource)
            ? previewSource
            : new Point(source.X, source.Y);

        var targetCentre = previewPositions is not null && previewPositions.TryGetValue(target.Id, out var previewTarget)
            ? previewTarget
            : new Point(target.X, target.Y);

        var shape = BuildShape(sourceCentre, targetCentre, source, target);
        var highlighted = state.HasFlag(EdgeRenderState.Selected) || state.HasFlag(EdgeRenderState.Related);

        edgeVisual.Curve = shape.Curve;
        edgeVisual.Samples = SampleCurve(shape.Curve);

        using var context = edgeVisual.Visual.RenderOpen();

        var pen = highlighted ? _edgeHighlightPen
            : state.HasFlag(EdgeRenderState.Dimmed) ? _edgeDimPen
            : _edgePen;

        context.DrawGeometry(null, pen, shape.Curve);
        context.DrawGeometry(pen.Brush, null, shape.Arrow);

        // The kind, on a plate over the curve's midpoint, and only while the edge is one the
        // selection is about: a canvas that labels every relation is unreadable.
        if (highlighted)
        {
            DrawEdgeLabel(context, edgeVisual.Edge, shape.Label);
        }
    }

    /// <summary>The edge's kind on a small plate, centred on the curve.</summary>
    private void DrawEdgeLabel(DrawingContext context, GraphEdge edge, Point topLeft)
    {
        var text = new FormattedText(
            string.IsNullOrWhiteSpace(edge.Label) ? EdgeKindLabel(edge.Kind) : edge.Label,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _palette.NodeMetaFace,
            EdgeLabelFontSize,
            _palette.NodeMeta,
            _pixelsPerDip)
        {
            MaxTextWidth = EdgeLabelWidth - 8,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        var plate = new Rect(topLeft.X, topLeft.Y, EdgeLabelWidth, text.Height + 2);

        context.DrawRoundedRectangle(_edgeLabelPlate, null, plate, 3, 3);
        context.DrawText(text, new Point(topLeft.X + ((EdgeLabelWidth - text.WidthIncludingTrailingWhitespace) / 2), topLeft.Y + 1));
    }

    /// <summary>The relationship in a form worth showing a person.</summary>
    private static string EdgeKindLabel(GraphEdgeKind kind) => kind switch
    {
        GraphEdgeKind.Contains => "Contains",
        GraphEdgeKind.Depends => "Depends On",
        GraphEdgeKind.Calls => "Calls",
        GraphEdgeKind.Implements => "Implements",
        GraphEdgeKind.Relates => "Relates To",
        GraphEdgeKind.Plans => "Plans",
        _ => kind.ToString(),
    };

    // ---------------------------------------------------------- hit testing

    /// <summary>The edge whose curve is nearest to a world point, within a generous radius.</summary>
    public string? HitEdge(Point worldPoint, IReadOnlyCollection<string> visibleEdgeIds, double zoom)
    {
        var threshold = EdgeHitRadius / Math.Max(zoom, 0.05);
        string? best = null;
        var bestDistance = double.PositiveInfinity;

        foreach (var id in visibleEdgeIds)
        {
            if (!_edges.TryGetValue(id, out var visual) || visual.Samples.Length == 0)
            {
                continue;
            }

            foreach (var point in visual.Samples)
            {
                var distance = (point - worldPoint).Length;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = id;
                }
            }
        }

        return bestDistance <= threshold ? best : null;
    }

    // ------------------------------------------------------------- queries

    public NodeVisual? FindNode(string id) => _nodes.TryGetValue(id, out var visual) ? visual : null;

    public EdgeVisual? FindEdge(string id) => _edges.TryGetValue(id, out var visual) ? visual : null;

    public IEnumerable<string> EdgeIds() => _edges.Keys;

    public IEnumerable<NodeVisual> AllNodeVisuals() => _nodes.Values;

    public IEnumerable<EdgeVisual> AllEdgeVisuals() => _edges.Values;

    /// <summary>Edge ids whose source or target is in the given set - the neighbourhood of a selection.</summary>
    public IReadOnlyList<string> EdgesIncidentTo(IReadOnlyCollection<string> nodeIds)
    {
        List<string>? incident = null;

        foreach (var edge in _edges.Values)
        {
            if (nodeIds.Contains(edge.SourceId) || nodeIds.Contains(edge.TargetId))
            {
                (incident ??= []).Add(edge.Edge.Id);
            }
        }

        return incident ?? [];
    }

    /// <summary>A node's world rectangle, or empty when the node is gone.</summary>
    public Rect NodeBounds(string nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out var visual))
        {
            return Rect.Empty;
        }

        var node = visual.Node;

        return new Rect(node.X - node.Width / 2, node.Y - node.Height / 2, node.Width, node.Height);
    }

    /// <summary>All edge visuals incident to a node - what "related" highlighting means.</summary>
    public IEnumerable<EdgeVisual> EdgesOf(string nodeId) => _edges.Values.Where(
        edge => edge.SourceId == nodeId || edge.TargetId == nodeId);

    // ------------------------------------------------------------- helpers

    private EdgeVisual CreateEdge(GraphEdge edge)
    {
        var visual = new EdgeVisual
        {
            Visual = new DrawingVisual(),
            Edge = edge,
            SourceId = edge.SourceId,
            TargetId = edge.TargetId,
            IsDirty = true,
            Hull = HullOf(edge.SourceId, edge.TargetId),
        };

        return visual;
    }

    /// <summary>
    /// Where an edge between two nodes can possibly land: both cards, plus the room the bow
    /// and the label need.
    /// </summary>
    /// <remarks>
    /// The curve leaves the hull of the two cards - a control point is pulled up to
    /// <see cref="MaxCurve"/> sideways, and the label sits above the midpoint - so the union is
    /// inflated rather than used as-is. Overestimating costs one extra edge in the visual tree;
    /// underestimating makes an edge vanish while its endpoints are still on screen.
    /// </remarks>
    private Rect HullOf(string sourceId, string targetId)
    {
        var source = NodeBounds(sourceId);
        var target = NodeBounds(targetId);

        if (source.IsEmpty || target.IsEmpty)
        {
            return Rect.Empty;
        }

        var hull = Rect.Union(source, target);
        hull.Inflate(MaxCurve, EdgeLabelFontSize + 24);

        return hull;
    }

    private static void PositionNode(NodeVisual visual, GraphNode node)
    {
        // Mutated in place rather than replaced: a drag writes this on every mouse move, and a
        // fresh Transform per move is a fresh unfrozen DependencyObject the render thread has
        // to pick up.
        if (visual.Visual.Transform is TranslateTransform translate)
        {
            translate.X = node.X;
            translate.Y = node.Y;
            return;
        }

        visual.Visual.Transform = new TranslateTransform(node.X, node.Y);
    }

    private void MarkIncidentEdgesDirty(string nodeId)
    {
        foreach (var edge in EdgesOf(nodeId))
        {
            edge.IsDirty = true;
            edge.Hull = HullOf(edge.SourceId, edge.TargetId);
        }
    }

    /// <summary>
    /// Everything one edge draws, computed together because it all falls out of the same four
    /// points.
    /// </summary>
    private readonly record struct EdgeShape(PathGeometry Curve, Geometry Arrow, Point Label);

    /// <summary>
    /// The curve, its arrowhead and where its label sits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There are no ports and no sockets. An edge leaves the side of the card that faces the
    /// other card and the control points pull horizontally, so a relation reads as
    /// "AuthService depends on UserRepository" rather than as a wire routed between an output
    /// and an input. Anything else - a ray from centre to centre, control points on the
    /// perpendicular - produces a curve that doubles back on itself the moment a dependency
    /// points leftwards.
    /// </para>
    /// <para>
    /// The node positions arrive here as centres; the card rectangle is derived from the
    /// centre and the size, which is the only difference from the retained-mode version this
    /// reproduces (there a card's X/Y were its top-left corner).
    /// </para>
    /// </remarks>
    private static EdgeShape BuildShape(Point sourceCentre, Point targetCentre, GraphNode source, GraphNode target)
    {
        var leftToRight = targetCentre.X >= sourceCentre.X;

        var startX = leftToRight ? sourceCentre.X + (source.Width / 2) : sourceCentre.X - (source.Width / 2);
        var endX = leftToRight ? targetCentre.X - (target.Width / 2) : targetCentre.X + (target.Width / 2);
        var startY = sourceCentre.Y;
        var endY = targetCentre.Y;

        var pull = Math.Clamp(Math.Abs(endX - startX) * 0.5, MinCurve, MaxCurve);
        var direction = leftToRight ? 1 : -1;

        var start = new Point(startX, startY);
        var end = new Point(endX, endY);
        var control1 = new Point(startX + (pull * direction), startY);
        var control2 = new Point(endX - (pull * direction), endY);

        var figure = new PathFigure(start, [new BezierSegment(control1, control2, end, true)], false);

        var curve = new PathGeometry();
        curve.Figures.Add(figure);
        curve.Freeze();

        // The cubic at t = 0.5 reduces to this weighted average - cheap, and exact enough to
        // hang a label on. The offsets centre an 80-wide plate above the curve.
        var label = new Point(
            ((start.X + (3 * control1.X) + (3 * control2.X) + end.X) / 8) - (EdgeLabelWidth / 2),
            ((start.Y + (3 * control1.Y) + (3 * control2.Y) + end.Y) / 8) - 18);

        return new EdgeShape(curve, BuildArrow(end, direction), label);
    }

    /// <summary>A filled triangle at the target end, pointing the way the relationship reads.</summary>
    private static Geometry BuildArrow(Point tip, int direction)
    {
        var back = tip.X - (ArrowLength * direction);

        var figure = new PathFigure(
            tip,
            [
                new LineSegment(new Point(back, tip.Y - ArrowHalfWidth), false),
                new LineSegment(new Point(back, tip.Y + ArrowHalfWidth), false),
            ],
            true);

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        return geometry;
    }

    private static Point[] SampleCurve(PathGeometry curve)
    {
        var flattened = curve.GetFlattenedPathGeometry();
        var points = new List<Point>(16);

        foreach (var figure in flattened.Figures)
        {
            var current = figure.StartPoint;

            points.Add(current);

            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment poly)
                {
                    points.AddRange(poly.Points);
                }
                else if (segment is LineSegment line)
                {
                    points.Add(line.Point);
                }
            }
        }

        return [.. points];
    }

    private static string FitText(string text, double availableWidth)
    {
        const double approxCharWidth = 6.4;

        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var maxChars = Math.Max(4, (int)(availableWidth / approxCharWidth));

        return text.Length <= maxChars ? text : text[..Math.Max(1, maxChars - 1)] + "…";
    }
}
