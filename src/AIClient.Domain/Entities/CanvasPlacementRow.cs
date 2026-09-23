namespace AIClient.Domain.Entities;

/// <summary>
/// Where one node sits in one view, and how it is drawn there.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here means anything about the project. A node dragged to the top left is not more
/// important than one at the bottom right; a collapsed card is not a simplified concept. Position
/// is a person's memory of where they left something, and treating it as data the AI reads would be
/// the first step towards a canvas whose layout quietly carries meaning nobody wrote down.
/// </para>
/// <para>
/// Keyed by view and node together: the same node appears in several views at different places,
/// which is the point of views. Both foreign keys cascade - a deleted view takes its placements,
/// and a deleted node takes the rectangles that pointed at it.
/// </para>
/// </remarks>
public sealed class CanvasPlacementRow
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ViewId { get; set; }

    public Guid NodeId { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Null means "whatever the card measures to", which is the usual case.</summary>
    public double? Width { get; set; }

    public double? Height { get; set; }

    public bool IsCollapsed { get; set; }

    /// <summary>
    /// A colour the user chose for this card, as a theme key rather than a hex value, so a card
    /// picked out in a light theme is still legible in a dark one.
    /// </summary>
    public string? Accent { get; set; }

    /// <summary>True while the node is pinned in place and the layout must route around it.</summary>
    public bool IsPinned { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public CanvasViewRow? View { get; set; }
}
