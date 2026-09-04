using AIClient.Domain.Graph;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIClient.Avalonia.ViewModels.Canvas;

/// <summary>
/// One relation on the canvas: which two cards it joins and whether it is lit.
/// </summary>
/// <remarks>
/// Deliberately lighter than its WPF counterpart. The old edge view model carried a frozen
/// <c>PathGeometry</c>; this one carries only identity and state, because the renderer draws
/// the curve from the endpoint cards' live bounds - the geometry is recomputed for the culled
/// set on every render pass, which is cheap, and a dragged card then never needs to notify
/// its edges at all.
/// </remarks>
public sealed partial class CanvasEdgeViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isHighlighted;

    public CanvasEdgeViewModel(GraphEdge edge, CanvasNodeViewModel from, CanvasNodeViewModel to)
    {
        Id = edge.Id;
        From = from;
        To = to;
        KindLabel = CanvasKindVisuals.LabelOf(edge.Kind.Value);
    }

    public Guid Id { get; }

    public CanvasNodeViewModel From { get; }

    public CanvasNodeViewModel To { get; }

    /// <summary><c>Depends On</c>, <c>Contains</c> - the relation read as words.</summary>
    public string KindLabel { get; private set; }

    /// <summary>Refreshes the semantic half from a new edge. Endpoints are fixed for life.</summary>
    public void Apply(GraphEdge edge) =>
        KindLabel = CanvasKindVisuals.LabelOf(edge.Kind.Value);

    /// <summary>
    /// A no-op kept so call sites read the same as they did in the WPF port. The curve is a
    /// renderer decision now; there is nothing for the edge to recompute.
    /// </summary>
    public void Touch()
    {
    }
}
