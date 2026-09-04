using System.Windows.Media;
using AIClient.Application.DTOs;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIClient.App.ViewModels.Canvas;

/// <summary>
/// One node card on the canvas: what the graph says about a node, plus where it sits.
/// </summary>
/// <remarks>
/// <para>
/// The split inside this class is the whole architecture in miniature. <see cref="Title"/>,
/// <see cref="KindLabel"/> and <see cref="Subtitle"/> are read from the graph and are never
/// written back from here; <see cref="X"/> and <see cref="Y"/> are canvas state and are the only
/// things a drag changes. Nothing on this type can invent a fact about the project.
/// </para>
/// <para>
/// It is long-lived and updated in place rather than rebuilt, because a rebuild would drop the
/// selection every time the indexer touched an unrelated node.
/// </para>
/// </remarks>
public sealed partial class CanvasNodeViewModel : ObservableObject
{
    /// <summary>How much of a long source path is kept before it is elided from the left.</summary>
    private const int PathTail = 38;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _kindLabel = string.Empty;

    [ObservableProperty]
    private string _glyph = "●";

    [ObservableProperty]
    private Brush _kindBrush = Brushes.Gray;

    /// <summary>Kind and source in one line - the card's second row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    [ObservableProperty]
    private double _width = CanvasMetrics.NodeWidth;

    [ObservableProperty]
    private double _height = CanvasMetrics.NodeHeight;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isHovered;

    /// <summary>
    /// The file behind this node is gone. Drawn faded and dashed rather than removed, because the
    /// placement and any hand-made relations around it are still worth keeping.
    /// </summary>
    [ObservableProperty]
    private bool _isMissing;

    [ObservableProperty]
    private bool _isPinned;

    public CanvasNodeViewModel(GraphNode node, CanvasPlacement placement)
    {
        Id = node.Id;
        Apply(node);
        Apply(placement);
    }

    public Guid Id { get; }

    /// <summary>
    /// The last graph state seen for this node. Held so the inspector and the AI actions can read
    /// the full node without a second lookup, and so <see cref="Apply(GraphNode)"/> can tell a
    /// real change from a no-op.
    /// </summary>
    public GraphNode Node { get; private set; } = null!;

    /// <summary>Where the card is, in world coordinates - the rectangle culling and fit both use it.</summary>
    public CanvasBounds Bounds => new(X, Y, Width, Height);

    /// <summary>
    /// What this card is called to anything reading the window rather than looking at it.
    /// </summary>
    /// <remarks>
    /// The cards are drawn by a template inside a non-hit-testable layer, so the only automation
    /// element for a card is the item peer the <c>ItemsControl</c> makes, and that peer takes its
    /// name from the item - hence <see cref="ToString"/> below. Both rows of the card, because a
    /// folder of forty files has forty titles that do not tell each other apart.
    /// </remarks>
    public string AutomationName => $"{Title} · {Subtitle}";

    /// <summary>The card's accessible name. See <see cref="AutomationName"/> for why this is the hook.</summary>
    public override string ToString() => AutomationName;

    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);

    /// <summary>Refreshes the read-only half from a new graph snapshot.</summary>
    public void Apply(GraphNode node)
    {
        Node = node;

        Title = string.IsNullOrWhiteSpace(node.Title) ? node.Key : node.Title;
        KindLabel = CanvasKindVisuals.LabelOf(node.Kind.Value);
        Glyph = CanvasKindVisuals.GlyphOf(node.Kind);
        KindBrush = CanvasKindVisuals.BrushOf(node.Kind);
        IsMissing = node.Status == GraphNodeStatus.Missing;
        Subtitle = BuildSubtitle(node, KindLabel);
    }

    /// <summary>Refreshes the spatial half from stored canvas state.</summary>
    public void Apply(CanvasPlacement placement)
    {
        X = placement.X;
        Y = placement.Y;
        Width = placement.Width ?? CanvasMetrics.NodeWidth;
        Height = placement.Height ?? CanvasMetrics.NodeHeight;
        IsPinned = placement.IsPinned;
    }

    /// <summary>Moves the card. The caller decides when the move is worth persisting.</summary>
    public void MoveTo(double x, double y)
    {
        X = x;
        Y = y;
    }

    /// <summary>
    /// The card's current position as a placement row.
    /// </summary>
    /// <remarks>
    /// <see cref="CanvasPlacement.IsPinned"/> is set because a position that came from a person's
    /// hand should survive the next auto-layout; positions that came from the layout itself are
    /// saved unpinned, so the caller passes <c>pinned: false</c> for those.
    /// </remarks>
    public CanvasPlacement ToPlacement(bool pinned) => new()
    {
        NodeId = Id,
        X = X,
        Y = Y,
        Width = Math.Abs(Width - CanvasMetrics.NodeWidth) < 0.5 ? null : Width,
        Height = Math.Abs(Height - CanvasMetrics.NodeHeight) < 0.5 ? null : Height,
        IsPinned = pinned || IsPinned,
    };

    /// <summary>
    /// Whether a point in world coordinates lands on this card. Used for hit-testing the drag
    /// gesture, which works in world space so that zoom does not change what counts as a hit.
    /// </summary>
    public bool HitTest(double worldX, double worldY) => Bounds.Contains(worldX, worldY);

    private static string BuildSubtitle(GraphNode node, string kindLabel)
    {
        var detail = node.Source?.Value;

        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = node.Summary;
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            return kindLabel;
        }

        detail = detail.Replace('\\', '/').Trim();

        // Elided from the left: the end of a path says what the file is, the start only says
        // where the repository lives.
        if (detail.Length > PathTail)
        {
            detail = "…" + detail[^PathTail..];
        }

        var lines = node.StartLine is { } start
            ? node.EndLine is { } end && end > start ? $" : {start}-{end}" : $" : {start}"
            : string.Empty;

        return $"{kindLabel} · {detail}{lines}";
    }
}
