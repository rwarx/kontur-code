using AIClient.Application.Configuration;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using AIClient.Infrastructure.Configuration;
using AIClient.Infrastructure.Graph;
using AIClient.Infrastructure.Workspace;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// The pass that turns an open folder into the first graph a user ever sees.
/// </summary>
/// <remarks>
/// Over a real temporary directory and the real sandbox, because most of what is asserted here is a
/// claim about the two of them together: that the walk inherits the workspace's refusals rather than
/// re-implementing them, and that a second pass recognises the nodes the first one made. A stubbed
/// file system would let this file agree with itself and say nothing about either.
/// </remarks>
public sealed class WorkspaceGraphIndexerTests : IAsyncLifetime
{
    private readonly StubSettingsService _settings = new();

    private string _scratch = null!;
    private string _root = null!;
    private WorkspaceService _workspace = null!;
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "aiclient-indexer", Guid.CreateVersion7().ToString("n"));
        _root = Path.Combine(_scratch, "AcmeApp");

        Directory.CreateDirectory(_root);

        _db = await TestDatabase.CreateAsync();
        _workspace = new WorkspaceService(
            _settings,
            new AppPaths(Path.Combine(_scratch, "appdata")),
            new RecordingLogger<WorkspaceService>());
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();

        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth failing a run over a leftover temporary directory.
        }
    }

    [Fact]
    public async Task Nothing_is_indexed_until_a_folder_is_open()
    {
        // The state the canvas starts in. The refusal is shown to the user, so it says what to do
        // next and carries no path: an absolute path on this platform holds their account name.
        var graph = _db.Graph();
        var result = await Indexer(graph).IndexAsync(cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Equal("No folder is open. Choose a project folder before indexing it.", result.Error);
        Assert.Equal(0, graph.Current.NodeCount);
    }

    [Fact]
    public async Task The_folder_becomes_a_project_node_with_a_tree_under_it()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "# Acme", Token);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "Program.cs"), "class P;", Token);

        var graph = await IndexAsync();

        var project = Assert.Single(graph.Current.OfKind(GraphNodeKind.Project));
        var titles = graph.Current.Nodes.Select(n => n.Title).OrderBy(t => t).ToList();

        // The folder's own name, never the path it was found at.
        Assert.Equal("AcmeApp", project.Title);
        Assert.Equal(".", project.Key);
        Assert.Equal(["AcmeApp", "Program.cs", "README.md", "src"], titles);
        Assert.Equal(["src", "README.md"], graph.Current.Children(project.Id).Select(n => n.Title));
    }

    [Fact]
    public async Task A_file_is_keyed_by_its_place_in_the_workspace_and_nothing_more()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "Program.cs"), "class P;", Token);

        var graph = await IndexAsync();
        var file = Assert.Single(graph.Current.OfKind(GraphNodeKind.File));

        Assert.Equal("src/Program.cs", file.Key);
        Assert.Equal("src/Program.cs", file.Source?.Value);
        Assert.DoesNotContain(_root, file.Key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Build_output_and_dependency_trees_never_reach_the_graph()
    {
        // Not security - those are refused elsewhere and are not configurable - but the difference
        // between a graph of a project and a graph of its node_modules.
        Directory.CreateDirectory(Path.Combine(_root, "bin", "Debug"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "left-pad"));
        await File.WriteAllTextAsync(Path.Combine(_root, "bin", "Debug", "app.dll"), "MZ", Token);
        await File.WriteAllTextAsync(Path.Combine(_root, "node_modules", "left-pad", "index.js"), "//", Token);
        await File.WriteAllTextAsync(Path.Combine(_root, "Program.cs"), "class P;", Token);

        var graph = await IndexAsync();
        var keys = graph.Current.Nodes.Select(n => n.Key).ToList();

        Assert.Contains("Program.cs", keys);
        Assert.DoesNotContain("bin", keys);
        Assert.DoesNotContain("node_modules", keys);
        Assert.All(keys, key => Assert.DoesNotContain("left-pad", key));
    }

    [Fact]
    public async Task Credentials_and_key_material_are_never_indexed()
    {
        // The indexer has no rule of its own for these: it lists through the sandbox, which will not
        // name them. That is the whole reason it has no access to the disk directly.
        await File.WriteAllTextAsync(Path.Combine(_root, ".env"), "TOKEN=hunter2", Token);
        await File.WriteAllTextAsync(Path.Combine(_root, "id_rsa"), "-----BEGIN", Token);
        await File.WriteAllTextAsync(Path.Combine(_root, "secrets.json"), "{}", Token);
        await File.WriteAllTextAsync(Path.Combine(_root, "server.pfx"), "0", Token);
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        await File.WriteAllTextAsync(Path.Combine(_root, "Program.cs"), "class P;", Token);

        var graph = await IndexAsync();
        var keys = graph.Current.Nodes.Select(n => n.Key).ToList();

        Assert.Equal([".", "Program.cs"], keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Everything_a_pass_writes_is_stamped_as_the_indexers_own()
    {
        // Permission to overwrite is decided per node. A pass that forgot to stamp what it emitted
        // would be refused its own rows on the next walk, and the graph would stop tracking the disk.
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "Program.cs"), "class P;", Token);

        var graph = await IndexAsync();

        Assert.All(graph.Current.Nodes, node => Assert.Equal(GraphOrigin.Indexer, node.Origin));
        Assert.All(graph.Current.Edges, edge => Assert.Equal(GraphOrigin.Indexer, edge.Origin));
        Assert.All(graph.Current.Edges, edge => Assert.Equal(GraphEdgeKind.Contains, edge.Kind));
    }

    [Fact]
    public async Task Indexing_the_same_folder_twice_changes_nothing()
    {
        // Identity is (Kind, Key), so the second pass finds the nodes the first one made. If it did
        // not, every re-index would orphan every placement and the canvas would reshuffle itself.
        await File.WriteAllTextAsync(Path.Combine(_root, "Program.cs"), "class P;", Token);

        var graph = await IndexAsync();
        var before = graph.Current.Nodes.Select(n => n.Id).OrderBy(id => id).ToList();
        var version = graph.Current.Version;

        var second = await Indexer(graph).IndexAsync(cancellationToken: Token);

        Assert.True(second.Success, second.Error);
        Assert.Equal(before, graph.Current.Nodes.Select(n => n.Id).OrderBy(id => id));

        // No change set at all, rather than an empty one: applying that would rebuild every card on
        // the canvas and write a log entry saying nothing happened.
        Assert.Equal(version, graph.Current.Version);
    }

    [Fact]
    public async Task A_file_that_is_gone_is_marked_missing_rather_than_deleted()
    {
        // Deleting the node would take the position somebody chose for it, whatever they filed it
        // under and every note attached to it - and a file removed by a branch switch is usually back
        // within the hour. Missing is a status a person can see and act on; removal is their decision.
        await File.WriteAllTextAsync(Path.Combine(_root, "Program.cs"), "class P;", Token);
        await File.WriteAllTextAsync(Path.Combine(_root, "Legacy.cs"), "class L;", Token);

        var graph = await IndexAsync();
        var legacy = graph.Current.FindByKey(GraphNodeKind.File, "Legacy.cs");

        Assert.NotNull(legacy);

        File.Delete(Path.Combine(_root, "Legacy.cs"));

        var second = await Indexer(graph).IndexAsync(cancellationToken: Token);

        Assert.True(second.Success, second.Error);
        Assert.Equal(1, second.Value?.Missing);
        Assert.Equal(GraphNodeStatus.Missing, graph.Current.Node(legacy.Id)?.Status);
        Assert.Equal(GraphNodeStatus.Active, graph.Current.FindByKey(GraphNodeKind.File, "Program.cs")?.Status);
    }

    [Fact]
    public async Task A_file_that_comes_back_is_the_same_node_again()
    {
        // The other half of not deleting: the node has to come out of Missing on its own, or the card
        // stays greyed out for a file that is plainly there and the status stops meaning anything.
        var path = Path.Combine(_root, "Legacy.cs");

        await File.WriteAllTextAsync(path, "class L;", Token);

        var graph = await IndexAsync();
        var before = graph.Current.FindByKey(GraphNodeKind.File, "Legacy.cs")?.Id;

        File.Delete(path);
        await Indexer(graph).IndexAsync(cancellationToken: Token);
        await File.WriteAllTextAsync(path, "class L;", Token);
        await Indexer(graph).IndexAsync(cancellationToken: Token);

        var after = graph.Current.FindByKey(GraphNodeKind.File, "Legacy.cs");

        Assert.Equal(before, after?.Id);
        Assert.Equal(GraphNodeStatus.Active, after?.Status);
    }

    [Fact]
    public async Task A_node_somebody_took_over_is_left_exactly_as_they_left_it()
    {
        // The graph is not a mirror of the disk. Someone renames a card, describes what it is for, or
        // corrects what the walk guessed; a pass that overwrote any of that would make the graph a
        // cache with extra steps. The pass does not even ask - it emits nothing for that node, which
        // is why the refusal list stays about real problems.
        await File.WriteAllTextAsync(Path.Combine(_root, "Program.cs"), "class P;", Token);

        var graph = await IndexAsync();
        var file = graph.Current.FindByKey(GraphNodeKind.File, "Program.cs");

        Assert.NotNull(file);

        var claimed = await graph.ApplyAsync(
            GraphChangeSet.Create(
                "Describe the entry point",
                GraphOrigin.User,
                [
                    new GraphMutation.UpdateNode(file with
                    {
                        Title = "Entry point",
                        Summary = "Where the application starts.",
                        Origin = GraphOrigin.User,
                    }),
                ]),
            Token);

        Assert.True(claimed.Success, claimed.Error);

        var second = await Indexer(graph).IndexAsync(cancellationToken: Token);
        var after = graph.Current.FindByKey(GraphNodeKind.File, "Program.cs");

        Assert.True(second.Success, second.Error);
        Assert.Empty(second.Value?.Refused ?? []);
        Assert.Equal(file.Id, after?.Id);
        Assert.Equal("Entry point", after?.Title);
        Assert.Equal("Where the application starts.", after?.Summary);
        Assert.Equal(GraphOrigin.User, after?.Origin);
    }

    [Fact]
    public async Task A_project_past_the_cap_is_reported_as_partial()
    {
        // The cap is what keeps the first frame interactive on a repository nobody meant to open. It
        // counts the project node too, so the number here is the number of nodes the graph ends with.
        for (var i = 0; i < 8; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, $"F{i}.cs"), "class F;", Token);
        }

        _settings.With<CanvasSettings>(canvas => canvas.MaxIndexedNodes = 4);

        var graph = await OpenAndLoadAsync();
        var report = await Indexer(graph).IndexAsync(cancellationToken: Token);

        Assert.True(report.Success, report.Error);
        Assert.True(report.Value?.IsTruncated);
        Assert.Equal(4, report.Value?.Nodes);
        Assert.Equal(4, graph.Current.NodeCount);
    }

    [Fact]
    public async Task A_pass_that_stopped_at_the_cap_never_calls_anything_gone()
    {
        // Unseen means "beyond the cap" here, not "deleted". Greying out the tail of a project on the
        // strength of a limit would be indistinguishable, to the user, from losing those files.
        for (var i = 0; i < 8; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, $"F{i}.cs"), "class F;", Token);
        }

        var graph = await IndexAsync();

        Assert.Equal(9, graph.Current.NodeCount);

        _settings.With<CanvasSettings>(canvas => canvas.MaxIndexedNodes = 3);

        var second = await Indexer(graph).IndexAsync(cancellationToken: Token);

        Assert.True(second.Success, second.Error);
        Assert.True(second.Value?.IsTruncated);
        Assert.Equal(0, second.Value?.Missing);
        Assert.Equal(9, graph.Current.NodeCount);
        Assert.All(graph.Current.Nodes, node => Assert.Equal(GraphNodeStatus.Active, node.Status));
    }

    /// <summary>Shorthand for the token every test here passes to every call.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The real indexer over the real sandbox, writing into the graph a test hands it.</summary>
    private WorkspaceGraphIndexer Indexer(GraphService graph) =>
        new(_workspace, graph, _settings, new RecordingLogger<WorkspaceGraphIndexer>());

    /// <summary>Opens the scratch folder and reads the graph, in the order the shell does it.</summary>
    private async Task<GraphService> OpenAndLoadAsync()
    {
        var opened = await _workspace.OpenAsync(_root, Token);

        Assert.True(opened.Success, opened.Error);

        var graph = _db.Graph();

        await graph.LoadAsync(Token);

        return graph;
    }

    /// <summary>One successful pass over the scratch folder, for the tests about its result.</summary>
    private async Task<GraphService> IndexAsync()
    {
        var graph = await OpenAndLoadAsync();
        var result = await Indexer(graph).IndexAsync(cancellationToken: Token);

        Assert.True(result.Success, result.Error);

        return graph;
    }
}
