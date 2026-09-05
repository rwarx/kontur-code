using System.Windows;
using System.Windows.Media;
using AIClient.App.Controls;
using AIClient.Domain.Graph;

namespace AIClient.App.Canvas;

/// <summary>
/// Everything the canvas needs to draw one node, resolved once and reused: brushes per
/// kind, the icon geometry, the typefaces.
/// </summary>
/// <remarks>
/// The renderer works with <see cref="DrawingContext"/>, which takes concrete brushes -
/// there is no DynamicResource at that level. The palette is re-resolved from the
/// element's resources whenever the theme changes, and a full redraw follows; between
/// theme changes the brushes are stable and shared, so per-frame lookups cost nothing.
/// </remarks>
public sealed class CanvasPalette
{
    public required Brush NodeBody { get; init; }
    public required Brush NodeBodyHover { get; init; }
    public required Brush NodeBodySelected { get; init; }
    public required Brush NodeBorder { get; init; }
    public required Brush NodeBorderSelected { get; init; }
    public required Brush NodeTitle { get; init; }
    public required Brush NodeMeta { get; init; }
    public required Brush Edge { get; init; }
    public required Brush EdgeHover { get; init; }
    public required Brush EdgeSelected { get; init; }
    public required Brush EdgeDimmed { get; init; }
    public required Brush Accent { get; init; }
    public required Brush SelectionGlow { get; init; }

    public IReadOnlyDictionary<GraphNodeKind, Brush> KindStrokes { get; init; } =
        new Dictionary<GraphNodeKind, Brush>();

    public IReadOnlyDictionary<GraphNodeKind, Geometry> KindIcons { get; init; } =
        new Dictionary<GraphNodeKind, Geometry>();

    public Typeface NodeTitleFace { get; init; } = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    public Typeface NodeMetaFace { get; init; } = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);

    /// <summary>Builds a palette from an element's resolved resources.</summary>
    /// <remarks>Resource misses degrade to neutral brushes rather than throwing: a canvas that
    /// renders in greyscale is recoverable, a canvas that throws on startup is not.</remarks>
    public static CanvasPalette FromResources(FrameworkElement host)
    {
        Brush Brush(string key, Brush fallback) => host.TryFindResource(key) as Brush ?? fallback;

        var grey = new SolidColorBrush(Color.FromRgb(140, 148, 160));
        var kindStrokes = new Dictionary<GraphNodeKind, Brush>();

        foreach (GraphNodeKind kind in Enum.GetValues<GraphNodeKind>())
        {
            kindStrokes[kind] = Brush($"Brush.Node.{kind}", grey);
        }

        Geometry? Icon(IconKind kind) => host.TryFindResource($"Icon.{kind}") as Geometry;

        var kindIcons = new Dictionary<GraphNodeKind, Geometry?>
        {
            [GraphNodeKind.File] = Icon(IconKind.File),
            [GraphNodeKind.Folder] = Icon(IconKind.Folder),
            [GraphNodeKind.Module] = Icon(IconKind.Code),
            [GraphNodeKind.Service] = Icon(IconKind.Package),
            [GraphNodeKind.Interface] = Icon(IconKind.Link),
            [GraphNodeKind.Data] = Icon(IconKind.Memory),
            [GraphNodeKind.View] = Icon(IconKind.Eye),
            [GraphNodeKind.Test] = Icon(IconKind.Check),
            [GraphNodeKind.Plan] = Icon(IconKind.Sparkle),
            [GraphNodeKind.Task] = Icon(IconKind.Tasks),
            [GraphNodeKind.Agent] = Icon(IconKind.Bot),
            [GraphNodeKind.Model] = Icon(IconKind.Models),
            [GraphNodeKind.External] = Icon(IconKind.Open),
            [GraphNodeKind.Note] = Icon(IconKind.Note),
        };

        return new CanvasPalette
        {
            NodeBody = Brush("Brush.NodeBody", Brushes.Transparent),
            NodeBodyHover = Brush("Brush.NodeBodyHover", Brushes.Transparent),
            NodeBodySelected = Brush("Brush.NodeBodySelected", Brushes.Transparent),
            NodeBorder = Brush("Brush.NodeBorder", grey),
            NodeBorderSelected = Brush("Brush.NodeBorderSelected", grey),
            NodeTitle = Brush("Brush.NodeTitle", Brushes.White),
            NodeMeta = Brush("Brush.NodeMeta", grey),
            Edge = Brush("Brush.Edge", grey),
            EdgeHover = Brush("Brush.EdgeHover", grey),
            EdgeSelected = Brush("Brush.EdgeSelected", grey),
            EdgeDimmed = Brush("Brush.EdgeDimmed", Brushes.Transparent),
            Accent = Brush("Brush.Accent", grey),
            SelectionGlow = Brush("Brush.Accent", grey),
            KindStrokes = kindStrokes,
            // Non-null assertion: an absent icon renders nothing, which the drawing code
            // checks before use; the dictionary's declared non-null type is honoured by
            // never storing nulls - absent entries are simply missing.
            KindIcons = kindIcons
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!),
        };
    }
}
