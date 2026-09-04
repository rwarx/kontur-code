using AIClient.Domain.Graph;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Turns a selection on the Canvas into the block of text a model is given about it.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the bridge between the spatial surface and the model. It takes graph terms -
/// a set of ids and a depth - and returns prose, so nothing downstream learns that a canvas exists,
/// and the same block can just as well come from a search result or an agent step.
/// </para>
/// <para>
/// The token budget is not advice. A selection of two hundred nodes, each with a file behind it,
/// would fill any context window several times over, so the implementation degrades what it says
/// about each node until the block fits and only then hands it back.
/// </para>
/// </remarks>
public interface IGraphContextSource
{
    /// <summary>
    /// Describes <paramref name="selection"/> in at most <paramref name="tokenBudget"/> tokens.
    /// </summary>
    /// <param name="selection">What the user pointed at. Empty selections return null.</param>
    /// <param name="tokenBudget">
    /// Tokens the whole prompt may occupy. The block takes a documented share of it, not the lot.
    /// </param>
    /// <returns>The block to inline, or null when there is nothing worth saying.</returns>
    Task<string?> BuildAsync(
        GraphSelection selection,
        int tokenBudget,
        CancellationToken cancellationToken = default);
}
