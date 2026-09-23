namespace AIClient.Application.Configuration;

/// <summary>
/// What the spatial projection is allowed to cost, and which arrangement produced it.
/// </summary>
/// <remarks>
/// Nothing here describes a fact about a project - the graph owns those. Most of it is the three
/// budgets a projection has to live inside: how much of a workspace is worth turning into nodes,
/// how many of those nodes may be on screen before dragging one stops feeling immediate, and how
/// much of a request a selection may claim before it crowds out the conversation it is meant to
/// inform. <see cref="LayoutRevision"/> is the one piece of bookkeeping, and it lives here rather
/// than in a column because a settings section costs no migration.
/// </remarks>
public sealed class CanvasSettings
{
    /// <summary>Nodes drawn before the surface starts culling to the visible area.</summary>
    /// <remarks>
    /// Not a limit on the graph, only on what is realised as visuals at once. Fifteen hundred
    /// cards is already past the point where a person can find anything by looking, and it is
    /// roughly where WPF's hit-testing and layout stop keeping up with a drag at 60fps.
    /// </remarks>
    public int MaxVisibleNodes { get; set; } = 1500;

    /// <summary>Nodes one indexing pass may create before it stops and says so.</summary>
    /// <remarks>
    /// A workspace root is chosen by hand and can always be a mistake - a home directory, a
    /// checkout with a vendored SDK inside it. The cap turns that mistake into a truncated
    /// index with a message rather than a database that takes a minute to open. Twenty thousand
    /// covers every repository this application is plausibly pointed at.
    /// </remarks>
    public int MaxIndexedNodes { get; set; } = 20_000;

    /// <summary>
    /// Share of a request's context budget the selected subgraph may occupy, from 0 to 1.
    /// </summary>
    /// <remarks>
    /// A selection is a hint about what matters, not the subject of the conversation: the history
    /// still has to fit, or the model answers a question about a graph with no memory of what was
    /// asked. Forty percent is enough for a few files of real source and little enough that a
    /// two-hundred-node marquee degrades to titles instead of evicting the last ten turns.
    /// </remarks>
    public double MaxContextShare { get; set; } = 0.4;

    /// <summary>Depth of relations pulled in around the selection when building context.</summary>
    /// <remarks>
    /// One hop is what makes the block worth having: a file alone says little, a file plus its
    /// folder and the things that depend on it is a description. Two hops over a well-connected
    /// graph is most of the graph, which is why this is not raised by default.
    /// </remarks>
    public int ContextDepth { get; set; } = 1;

    /// <summary>
    /// Which revision of <c>CanvasLayout</c> produced the positions currently stored.
    /// </summary>
    /// <remarks>
    /// Positions outlive the arithmetic that made them. Indexing deliberately never moves a card
    /// that already has a place, so a project arranged by an older revision keeps that shape for
    /// good - which is how a graph laid out as one tall strip stays a strip after the layout
    /// learned to wrap. Comparing this against <c>CanvasLayout.Revision</c> lets the canvas tidy
    /// such a surface exactly once, leaving anything pinned alone. Zero means "arranged before
    /// this was recorded", so every existing installation gets that one pass.
    /// </remarks>
    public int LayoutRevision { get; set; }
}
