namespace AIClient.Domain.Entities;

/// <summary>
/// A titled rectangle drawn behind a group of cards.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately two-faced, and the two faces are in different tables. The grouping a person means
/// by "Authentication" - that these things belong together - is a node in the graph of kind
/// <c>component</c> with <c>groups</c> edges, because the AI has to be able to reason about it. The
/// frame around it is this row: geometry and a caption, nothing to reason about.
/// </para>
/// <para>
/// <see cref="GroupNodeId"/> links the two when both exist, and is null when only the frame does.
/// A frame with no node behind it is allowed on purpose: sometimes a person wants a line drawn
/// around part of a diagram without asserting that it is a component of the system.
/// </para>
/// </remarks>
public sealed class CanvasAreaRow
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ViewId { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>The component this frame stands for, or null for a purely visual divider.</summary>
    public Guid? GroupNodeId { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    /// <summary>Theme key rather than a hex value, for the same reason as a placement's accent.</summary>
    public string? Accent { get; set; }

    /// <summary>Paint order, so nested frames stay nested.</summary>
    public int Order { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public CanvasViewRow? View { get; set; }
}
