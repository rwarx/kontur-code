using AIClient.Domain.Graph;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Where graph snapshots persist between runs.
/// </summary>
/// <remarks>
/// <para>
/// A file rather than a table, on purpose: the graph is one document per workspace, saved
/// whole, and JSON survives a schema tweak better than a migration survives a missing one.
/// The key is whatever identifies the document - a workspace root, in practice - and the
/// store is responsible for turning it into something its file system can live with.
/// </para>
/// <para>
/// An implementation must write atomically (temp file plus move), because the application
/// can be closed mid-save, and half a graph is worse than the previous whole one: the user
/// cannot see what is missing from a canvas that loads.
/// </para>
/// <para>
/// A graph that cannot be read is reported as absent rather than as an error - a corrupt
/// file is recoverable by re-indexing the workspace, and a crash loop at startup is not.
/// </para>
/// </remarks>
public interface IGraphStore
{
    /// <summary>Writes the snapshot under the given key, whole and atomically.</summary>
    /// <remarks>Saving an empty snapshot is legitimate: a cleared canvas is a state worth keeping.</remarks>
    Task SaveAsync(string key, GraphSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the snapshot stored under the given key, or null when nothing is stored or
    /// what is stored cannot be read.
    /// </summary>
    Task<GraphSnapshot?> LoadAsync(string key, CancellationToken cancellationToken = default);
}
