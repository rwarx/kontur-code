using AIClient.Application.DTOs;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Reads and writes the spatial state of a view: positions, sizes and the camera.
/// </summary>
/// <remarks>
/// <para>
/// A separate contract from <see cref="IGraphService"/> on purpose, and the split is the principle
/// made physical. The graph is held in memory and read on every hover; positions are written on
/// every drag and matter only to one surface. Nothing on this interface can create a node, an edge
/// or a relation of any kind, so no amount of use can put a fact about the project somewhere the
/// model cannot see it.
/// </para>
/// <para>
/// Writes are last-one-wins upserts with no version check. Two windows dragging the same card is
/// not a case worth reconciling: the loser's position is a position, not information.
/// </para>
/// </remarks>
public interface ICanvasViewStore
{
    /// <summary>
    /// The view the Canvas opens on, created empty on first use.
    /// </summary>
    /// <remarks>
    /// Never returns null: a first launch has no rows, and the alternative would be every caller
    /// handling "no view yet" before it can draw anything.
    /// </remarks>
    Task<CanvasViewState> GetDefaultAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores the given placements, replacing any that exist for the same nodes.</summary>
    /// <remarks>
    /// Placements not mentioned are left alone, so a drag can save one card without rewriting the
    /// whole surface.
    /// </remarks>
    Task SavePlacementsAsync(
        Guid viewId,
        IEnumerable<CanvasPlacement> placements,
        CancellationToken cancellationToken = default);

    /// <summary>Stores where the camera was left.</summary>
    Task SaveViewportAsync(
        Guid viewId,
        CanvasViewport viewport,
        CancellationToken cancellationToken = default);
}
