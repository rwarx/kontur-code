using AIClient.Application.DTOs;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Fills the graph from whatever the open workspace turns out to contain.
/// </summary>
/// <remarks>
/// <para>
/// One method, because indexing is one gesture from the user's point of view: point at a folder and
/// wait. What an implementation understands about the contents is its own business - the first one
/// sees files and folders, a later one will see types and members - and neither the Canvas nor the
/// graph needs to know which it is talking to.
/// </para>
/// <para>
/// Everything an indexer writes is stamped <c>GraphOrigin.Indexer</c>, which is what makes running
/// it again safe: the change-set rules refuse to let it alter or remove anything a person or a model
/// created. A second pass over a workspace therefore cannot flatten the component somebody drew or
/// the dependency they corrected by hand.
/// </para>
/// </remarks>
public interface IGraphIndexer
{
    /// <summary>
    /// Walks the open workspace and applies one change set describing what is there.
    /// </summary>
    /// <param name="progress">Reported to as the walk proceeds, on the calling thread's behalf.</param>
    Task<GraphResult<GraphIndexReport>> IndexAsync(
        IProgress<GraphIndexProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
