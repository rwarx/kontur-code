namespace AIClient.Domain.Graph;

/// <summary>
/// What has been picked out, expressed in graph terms.
/// </summary>
/// <remarks>
/// <para>
/// This is the entirety of what a chat turn or an agent learns about the gesture that started it: a
/// set of ids and how far to look around them. No coordinates, no view, no zoom level. A model has
/// no use for where a card sits, and passing it along would be the first step towards a canvas whose
/// layout quietly means something.
/// </para>
/// <para>
/// A set rather than a list: picking three nodes has no order, and if some future action needs one
/// it should say so in its own terms rather than lean on the order a rubber band happened to
/// produce.
/// </para>
/// </remarks>
public sealed record GraphSelection
{
    /// <summary>Nothing picked, which is what an ordinary chat message carries.</summary>
    public static GraphSelection Empty { get; } = new();

    public IReadOnlySet<Guid> NodeIds { get; init; } = new HashSet<Guid>();

    /// <summary>Edges picked on their own - how a person asks about a relationship itself.</summary>
    public IReadOnlySet<Guid> EdgeIds { get; init; } = new HashSet<Guid>();

    /// <summary>
    /// How many hops around the selection to draw in.
    /// </summary>
    /// <remarks>
    /// One by default: asked about AuthService, a person means it and the things it touches, and a
    /// node stripped of its neighbours is a title with nothing to reason from. Raised deliberately
    /// by someone who wants the wider picture - and the token budget still has the last word on how
    /// much of it survives.
    /// </remarks>
    public int Depth { get; init; } = 1;

    public bool IsEmpty => NodeIds.Count == 0 && EdgeIds.Count == 0;

    public static GraphSelection Of(params IReadOnlyList<Guid> nodeIds) =>
        new() { NodeIds = new HashSet<Guid>(nodeIds) };

    public static GraphSelection Nodes(IEnumerable<Guid> nodeIds, int depth = 1) =>
        new() { NodeIds = new HashSet<Guid>(nodeIds), Depth = depth };
}
