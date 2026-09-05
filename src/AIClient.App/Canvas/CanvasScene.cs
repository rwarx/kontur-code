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
/// <b>Pens.</b> Hairline widths are divided by zoom so they render as whole pixels at any
/// magnification: a 1px border should be a 1px border, not a blurry half-pixel.
/// </para>
/// </remarks>
public sealed class CanvasScene
{
    private const double TitleFontSize = 12.5;
    private const double MetaFontSize = 10.5;
    private const double IconSize = 14;
    private const double IconPadding = 11;
    private const double TextPadding = 8;
    private const double NodeRadius = 5;
    private const double EdgeHitRadius = 7;
    private const double NodeMinWidth = 96;

    private readonly Dictionary<string, NodeVisual> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EdgeVisual> _edges = new(StringComparer.Ordinal);
    private readonly SpatialIndex _index = new();

    private CanvasPalette _palette = new()
    {
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

    // ------------------------------------------------------------ rendering

    /// <summary>Draws one node visual if dirty. Callers drive this per-culled-visual.</summary>
    public void RenderNode(NodeVisual nodeVisual, double zoom)
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

        // The rounded body. Fill is a state wash, border is the node's kind stroke.
        var bodyBrush = state.HasFlag(NodeRenderState.Selected) ? palette.NodeBodySelected
            : state.HasFlag(NodeRenderState.Hovered) ? palette.NodeBodyHover
            : palette.NodeBody;

        var borderBrush = state.HasFlag(NodeRenderState.Selected) ? palette.NodeBorderSelected
            : palette.KindStrokes.TryGetValue(node.Kind, out var kindStroke) ? kindStroke
            : palette.NodeBorder;

        // Hairlines stay hairlines: the visual tree scales by zoom below this drawing.
        var borderPen = new Pen(borderBrush, 1 / Math.Max(zoom, 0.05));
        borderPen.LineJoin = PenLineJoin.Round;

        var bodyRect = new Rect(-node.Width / 2, -node.Height / 2, node.Width, node.Height);

        // Selection glow: two concentric strokes rather than a bitmap effect. Bitmap
        // effects force software rendering; strokes cost nothing and read as a glow at
        // low alpha. This is one of the four sanctioned glow sites in the product.
        if (state.HasFlag(NodeRenderState.Selected))
        {
            // Rect.Inflate is an instance mutator on a struct: take copies, grow them.
            var glowRect = bodyRect;
            glowRect.Inflate(3, 3);

            var outerGlowRect = bodyRect;
            outerGlowRect.Inflate(6, 6);

            var glowPen = new Pen(palette.SelectionGlow, 1.5 / Math.Max(zoom, 0.05))
            {
                LineJoin = PenLineJoin.Round,
            };

            var outerPen = new Pen(Translucent(palette.SelectionGlow, 64), 1.5 / Math.Max(zoom, 0.05))
            {
                LineJoin = PenLineJoin.Round,
            };

            context.DrawRoundedRectangle(null, glowPen, glowRect, NodeRadius + 3, NodeRadius + 3);
            context.DrawRoundedRectangle(null, outerPen, outerGlowRect, NodeRadius + 6, NodeRadius + 6);
        }

        context.DrawRoundedRectangle(bodyBrush, borderPen, bodyRect, NodeRadius, NodeRadius);

        // Kind icon, top-left, in the kind's stroke colour.
        var iconTop = -node.Height / 2 + 9;
        var iconLeft = -node.Width / 2 + IconPadding;

        if (palette.KindIcons.TryGetValue(node.Kind, out var icon))
        {
            var scale = IconSize / 16;

            var iconPen = new Pen(borderBrush, 1.5 / scale / Math.Max(zoom, 0.05));
            iconPen.StartLineCap = PenLineCap.Round;
            iconPen.EndLineCap = PenLineCap.Round;
            iconPen.LineJoin = PenLineJoin.Round;

            // The shared icon geometries are frozen, so the transform is pushed on the
            // context rather than baked into the geometry - mutating a frozen geometry
            // throws, and pushing a transform costs nothing.
            var matrix = new Matrix(scale, 0, 0, scale, iconLeft - 1, iconTop - 1);

            context.PushTransform(new MatrixTransform(matrix));
            context.DrawGeometry(null, iconPen, icon);
            context.Pop();
        }

        // Title. Bold, primary; measured once and cached per render - FormattedText is
        // the single most expensive thing here and it is only rebuilt when the node's
        // content changes, never for moves.
        var titleTop = iconTop - 2;
        var title = new FormattedText(
            FitText(node.Title, node.Width - IconPadding * 2 - TextPadding),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            palette.NodeTitleFace,
            TitleFontSize,
            palette.NodeTitle,
            _pixelsPerDip);

        context.DrawText(title, new Point(iconLeft + IconSize + 4, titleTop));

        // Subtitle/metadata line: kind name, or the path when there is one.
        var meta = node.Subtitle ?? node.Kind.ToString().ToLowerInvariant();
        var metaTop = titleTop + TitleFontSize + 4;

        if (node.Height >= 48 && !string.IsNullOrEmpty(meta))
        {
            var metaText = new FormattedText(
                FitText(meta, node.Width - IconPadding * 2 - 8),
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                palette.NodeMetaFace,
                MetaFontSize,
                palette.NodeMeta,
                _pixelsPerDip);

            context.DrawText(metaText, new Point(iconLeft + IconSize + 4, metaTop));
        }

        // Dimming: a translucent scrim over the whole node, rather than redrawing the
        // node in different colours. One rect, no text re-render.
        if (state.HasFlag(NodeRenderState.Dimmed))
        {
            var scrim = new SolidColorBrush(Color.FromArgb(140, 12, 13, 15));
            scrim.Freeze();
            context.DrawRoundedRectangle(scrim, null, bodyRect, NodeRadius, NodeRadius);
        }
    }

    /// <summary>Draws one edge visual if dirty.</summary>
    /// <param name="previewPositions">Drag-time node centres, keyed by node id, overriding the snapshot.</param>
    public void RenderEdge(
        EdgeVisual edgeVisual,
        GraphSnapshot snapshot,
        double zoom,
        IReadOnlyDictionary<string, Point>? previewPositions = null)
    {
        // Edges follow dragged nodes live, so during a drag they are always stale.
        edgeVisual.IsDirty = false;

        if (!snapshot.TryGetNode(edgeVisual.SourceId, out var source)
            || !snapshot.TryGetNode(edgeVisual.TargetId, out var target))
        {
            using var clear = edgeVisual.Visual.RenderOpen();
            return;
        }

        var state = edgeVisual.State;
        var palette = _palette;

        // A dragged node's centre comes from the preview; its rectangle still uses the
        // snapshot size, which is correct because drags do not resize.
        var sourceCentre = previewPositions is not null && previewPositions.TryGetValue(source.Id, out var previewSource)
            ? previewSource
            : new Point(source.X, source.Y);

        var targetCentre = previewPositions is not null && previewPositions.TryGetValue(target.Id, out var previewTarget)
            ? previewTarget
            : new Point(target.X, target.Y);

        var curve = BuildCurve(sourceCentre, targetCentre, source, target);

        edgeVisual.Curve = curve;
        edgeVisual.Samples = SampleCurve(curve);

        using var context = edgeVisual.Visual.RenderOpen();

        var brush = state.HasFlag(EdgeRenderState.Selected) ? palette.EdgeSelected
            : state.HasFlag(EdgeRenderState.Related) ? palette.EdgeSelected
            : state.HasFlag(EdgeRenderState.Dimmed) ? palette.EdgeDimmed
            : palette.Edge;

        var width = state.HasFlag(EdgeRenderState.Selected) || state.HasFlag(EdgeRenderState.Related)
            ? 1.6 / Math.Max(zoom, 0.05)
            : 0.9 / Math.Max(zoom, 0.05);

        var pen = new Pen(brush, width);
        pen.LineJoin = PenLineJoin.Round;
        pen.StartLineCap = PenLineCap.Round;
        pen.EndLineCap = PenLineCap.Round;

        context.DrawGeometry(null, pen, curve);

        // Related edges get a soft halo, the same one-trick glow as selected nodes.
        if (state.HasFlag(EdgeRenderState.Related) || state.HasFlag(EdgeRenderState.Selected))
        {
            var halo = new Pen(Translucent(palette.EdgeSelected, 46), 4 / Math.Max(zoom, 0.05))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };

            context.DrawGeometry(null, halo, curve);
        }

        // Arrowhead at the target, drawn only while zoomed in enough for it to read.
        if (zoom >= 0.45)
        {
            DrawArrowHead(context, brush, curve, source, target, 1 / Math.Max(zoom, 0.05));
        }
    }

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
        };

        return visual;
    }

    private static void PositionNode(NodeVisual visual, GraphNode node)
    {
        visual.Visual.Transform = new TranslateTransform(node.X, node.Y);
    }

    private void MarkIncidentEdgesDirty(string nodeId)
    {
        foreach (var edge in EdgesOf(nodeId))
        {
            edge.IsDirty = true;
        }
    }

    /// <summary>
    /// A gentle cubic curve between two nodes, endpoints pulled to the nodes' boundaries so
    /// lines visibly terminate at shapes rather than centres.
    /// </summary>
    private static PathGeometry BuildCurve(Point sourceCentre, Point targetCentre, GraphNode source, GraphNode target)
    {
        var start = sourceCentre;
        var end = targetCentre;

        var direction = end - start;

        if (direction.Length < 0.001)
        {
            var degenerate = new PathGeometry();
            degenerate.Figures.Add(new PathFigure(start, [new LineSegment(end, true)], false));
            return degenerate;
        }

        start = PushToBoundary(start, direction, source);
        end = PushToBoundary(end, -direction, target);

        // The control points sit on the perpendicular, a fraction of the distance out:
        // enough curve that parallel edges never overlap, not enough to look looped.
        var unit = direction / direction.Length;
        var perpendicular = new Vector(-unit.Y, unit.X);
        var bow = Math.Min(direction.Length * 0.14, 60) * (CurveSide(source, target) ? 1 : -1);

        var control1 = start + unit * (direction.Length * 0.3) + perpendicular * bow;
        var control2 = end - unit * (direction.Length * 0.3) + perpendicular * bow;

        var figure = new PathFigure(
            start,
            [new BezierSegment(control1, control2, end, true)],
            false);

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        return geometry;
    }

    /// <summary>Curves bow alternately so A→B and B→A do not lie on top of each other.</summary>
    private static bool CurveSide(GraphNode a, GraphNode b) =>
        string.Compare(a.Id, b.Id, StringComparison.Ordinal) > 0;

    /// <summary>Pulls a centre point out to where the ray leaves the node's rectangle.</summary>
    private static Point PushToBoundary(Point centre, Vector direction, GraphNode node)
    {
        var halfWidth = node.Width / 2;
        var halfHeight = node.Height / 2;

        var dx = direction.X;
        var dy = direction.Y;

        var xScale = Math.Abs(dx) < 0.0001 ? double.PositiveInfinity : halfWidth / Math.Abs(dx);
        var yScale = Math.Abs(dy) < 0.0001 ? double.PositiveInfinity : halfHeight / Math.Abs(dy);

        var scale = Math.Min(xScale, yScale);

        return centre + direction * scale;
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

    private static void DrawArrowHead(
        DrawingContext context,
        Brush brush,
        PathGeometry curve,
        GraphNode source,
        GraphNode target,
        double penWidth)
    {
        // A short tangent at the curve's end gives the arrow its direction.
        var end = new Point(target.X, target.Y);
        var direction = new Point(source.X, source.Y) - end;

        var length = direction.Length;

        if (length < 0.001)
        {
            return;
        }

        var unit = new Vector(direction.X / length, direction.Y / length);

        // Pull back so the arrow tip sits at the boundary.
        var halfWidth = target.Width / 2;
        var halfHeight = target.Height / 2;
        var xScale = Math.Abs(unit.X) < 0.0001 ? double.PositiveInfinity : halfWidth / Math.Abs(unit.X);
        var yScale = Math.Abs(unit.Y) < 0.0001 ? double.PositiveInfinity : halfHeight / Math.Abs(unit.Y);
        var scale = Math.Min(xScale, yScale);

        var tip = end + unit * scale;
        var size = 5.5;

        var perpendicular = new Vector(-unit.Y, unit.X);

        var left = tip - unit * size + perpendicular * (size * 0.45);
        var right = tip - unit * size - perpendicular * (size * 0.45);

        var arrow = new StreamGeometry();

        using (var stream = arrow.Open())
        {
            stream.BeginFigure(tip, true, true);
            stream.LineTo(left, true, false);
            stream.LineTo(right, true, false);
        }

        arrow.Freeze();

        context.DrawGeometry(brush, null, arrow);
    }

    /// <summary>A copy of a palette brush at reduced opacity, for halo strokes.</summary>
    /// <remarks>Pens have no opacity, and freezing is what makes these cheap to reuse within a render pass.</remarks>
    private static Brush Translucent(Brush source, byte alpha)
    {
        if (source is SolidColorBrush solid)
        {
            var faded = new SolidColorBrush(Color.FromArgb(alpha, solid.Color.R, solid.Color.G, solid.Color.B));
            faded.Freeze();
            return faded;
        }

        return source;
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
