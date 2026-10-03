using AIClient.Application.Interfaces;

namespace AIClient.Server;

/// <summary>
/// Keeps the graph store in step with the open workspace: loads the workspace's
/// graph when a folder opens, saves it after every mutation.
/// </summary>
/// <remarks>
/// Mirrors what the WPF shell's workspace view model does with
/// <c>WorkspaceGraphKeys</c>, minus the view layer. One key per workspace root,
/// so switching folders never mixes drawings.
/// </remarks>
public sealed class GraphPersistence
{
    private readonly IGraphService _graph;
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<GraphPersistence> _logger;
    private string _currentKey = "workspace:none";

    public GraphPersistence(
        IGraphService graph,
        IWorkspaceService workspace,
        ILogger<GraphPersistence> logger)
    {
        _graph = graph;
        _workspace = workspace;
        _logger = logger;

        _workspace.RootChanged += OnRootChanged;
    }

    public static string KeyForRoot(string? root) =>
        string.IsNullOrEmpty(root) ? "workspace:none" : "workspace:" + root;

    /// <summary>Loads the graph for the already-open workspace. Called once at startup.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _currentKey = KeyForRoot(_workspace.Root);
        await _graph.LoadAsync(_currentKey, ct).ConfigureAwait(false);
    }

    /// <summary>Persists the current graph under the current workspace key.</summary>
    public Task SaveCurrentAsync(CancellationToken ct = default) =>
        _graph.SaveAsync(_currentKey, ct);

    private void OnRootChanged(object? sender, string? root)
    {
        // Fire-and-forget is honest here: the event has no async contract, and a
        // failed save is logged rather than thrown into whoever opened the folder.
        _ = SwapAsync(root);
    }

    private async Task SwapAsync(string? root)
    {
        try
        {
            await _graph.SaveAsync(_currentKey).ConfigureAwait(false);
            _currentKey = KeyForRoot(root);
            await _graph.LoadAsync(_currentKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The graph could not be swapped for the new workspace root.");
        }
    }
}
