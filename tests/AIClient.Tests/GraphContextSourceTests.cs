using AIClient.Application.Configuration;
using AIClient.Application.Services;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using AIClient.Domain.Workspace;
using AIClient.Infrastructure.Configuration;
using AIClient.Infrastructure.Graph;
using AIClient.Infrastructure.Workspace;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// The one bridge between a selection on the Canvas and the model, and the ladder it walks down.
/// </summary>
/// <remarks>
/// <para>
/// Over the real graph store and the real sandbox. Both matter to what is asserted: the block is
/// built from a snapshot, so a node that is gone has to fall out of it on its own, and every excerpt
/// is read through the workspace, so a selection must not become a way to read a key file.
/// </para>
/// <para>
/// The budget assertions use the same estimator the builder uses. Not a tautology - the claim being
/// tested is that the builder degrades until its own measurement fits, and a block that overran
/// would prove it never checked.
/// </para>
/// </remarks>
public sealed class GraphContextSourceTests : IAsyncLifetime
{
    private readonly StubSettingsService _settings = new();

    private string _scratch = null!;
    private string _root = null!;
    private WorkspaceService _workspace = null!;
    private TestDatabase _db = null!;
    private GraphService _graph = null!;

    public async ValueTask InitializeAsync()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "aiclient-graph-context", Guid.CreateVersion7().ToString("n"));
        _root = Path.Combine(_scratch, "AcmeApp");

        Directory.CreateDirectory(_root);

        _db = await TestDatabase.CreateAsync();
        _graph = _db.Graph();

        await _graph.LoadAsync();

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
    public async Task An_ordinary_chat_message_carries_no_block_at_all()
    {
        // The guarantee the whole plan rests on: with nothing selected the send path is the one it
        // was before any of this existed. Null rather than an empty string, so nothing is inlined.
        Assert.Null(await Source().BuildAsync(GraphSelection.Empty, 8_000, Token));
    }

    [Fact]
    public async Task A_selection_of_nodes_that_are_gone_says_nothing()
    {
        // A selection is a gesture that already happened. By the time it reaches a model the nodes
        // may have been removed by a re-index, and inventing a block about them would be worse than
        // sending none.
        await AddAsync(FileNode("src/Program.cs"));

        var stale = GraphSelection.Of(Guid.CreateVersion7(), Guid.CreateVersion7());

        Assert.Null(await Source().BuildAsync(stale, 8_000, Token));
    }

    [Fact]
    public async Task A_budget_with_no_room_for_a_block_gets_none()
    {
        // Below the floor even a list of titles would be cut mid-node, and a block that names one of
        // five selected nodes reads as though the other four were not picked.
        var node = FileNode("src/Program.cs");

        await AddAsync(node);

        Assert.Null(await Source().BuildAsync(GraphSelection.Of(node.Id), 120, Token));
    }

    [Fact]
    public async Task A_selected_file_arrives_with_its_text()
    {
        // The richest rung, and the point of the feature: asked about one file, the model gets the
        // file rather than its name.
        await OpenAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, "Program.cs"), "class Program { }", Token);

        var node = FileNode("Program.cs");

        await AddAsync(node);

        var block = await Source().BuildAsync(GraphSelection.Of(node.Id), 8_000, Token);

        Assert.NotNull(block);
        Assert.Contains("<graph-context>", block, StringComparison.Ordinal);
        Assert.Contains("<file name=\"Program.cs\"", block, StringComparison.Ordinal);
        Assert.Contains("class Program { }", block, StringComparison.Ordinal);
        Assert.EndsWith("</graph-context>", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_the_question_already_carries_is_not_quoted_a_second_time()
    {
        // Asking from the canvas attaches the selection's files to the message, and an attachment is
        // inlined whole. Quoting the head of the same file inside the block spends the budget twice on
        // one file and leaves the model to work out whether the two copies differ. The node stays in
        // the block - its relations are the reason it was selected - only its text goes.
        await OpenAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, "Attached.cs"), "class Attached { }", Token);
        await File.WriteAllTextAsync(Path.Combine(_root, "Other.cs"), "class Other { }", Token);

        var attached = FileNode("Attached.cs");
        var other = FileNode("Other.cs");

        await AddAsync(attached, other);

        var block = await Source().BuildAsync(
            GraphSelection.Nodes([attached.Id, other.Id]),
            8_000,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Attached.cs" },
            Token);

        Assert.NotNull(block);
        Assert.Contains("<file name=\"Other.cs\"", block, StringComparison.Ordinal);
        Assert.Contains("class Other { }", block, StringComparison.Ordinal);
        Assert.DoesNotContain("<file name=\"Attached.cs\"", block, StringComparison.Ordinal);
        Assert.DoesNotContain("class Attached { }", block, StringComparison.Ordinal);

        // Named, and by the same string the attachment is named by, so the two agree.
        Assert.Contains("Attached.cs", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_selection_of_one_attached_file_falls_back_to_describing_it()
    {
        // The single-file case, which is the common one: one click on "Explain" over one card. With its
        // only excerpt suppressed there is nothing left to quote, so the block drops a rung and says
        // where the file is and what it relates to - the half the attachment does not carry.
        await OpenAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, "Program.cs"), "class Program { }", Token);

        var node = FileNode("Program.cs") with { Summary = "The entry point." };

        await AddAsync(node);

        var block = await Source().BuildAsync(
            GraphSelection.Of(node.Id),
            8_000,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "program.cs" },
            Token);

        Assert.NotNull(block);
        Assert.DoesNotContain("<file", block, StringComparison.Ordinal);
        Assert.DoesNotContain("class Program { }", block, StringComparison.Ordinal);
        Assert.Contains("Program.cs", block, StringComparison.Ordinal);
        Assert.Contains("The entry point.", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_read_from_disk_until_a_folder_is_open()
    {
        // The Canvas can hold a graph from a previous session with no workspace open behind it. The
        // block is still worth sending - it just describes the nodes instead of quoting them.
        await File.WriteAllTextAsync(Path.Combine(_root, "Program.cs"), "class Program { }", Token);

        var node = FileNode("Program.cs");

        await AddAsync(node);

        var block = await Source().BuildAsync(GraphSelection.Of(node.Id), 8_000, Token);

        Assert.NotNull(block);
        Assert.DoesNotContain("<file", block, StringComparison.Ordinal);
        Assert.Contains("Program.cs", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_the_sandbox_will_not_open_is_described_rather_than_quoted()
    {
        // Pointing a node at a key file is the obvious way to try to launder a read through the
        // Canvas, and the answer is that the excerpt goes through the same sandbox as everything
        // else. The node is still in the block: what it is called is not a secret, its contents are.
        await OpenAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, ".env"), "TOKEN=hunter2", Token);

        var node = FileNode(".env") with { Title = "Environment" };

        await AddAsync(node);

        var block = await Source().BuildAsync(GraphSelection.Of(node.Id), 8_000, Token);

        Assert.NotNull(block);
        Assert.DoesNotContain("hunter2", block, StringComparison.Ordinal);
        Assert.DoesNotContain("<file", block, StringComparison.Ordinal);
        Assert.Contains("Environment", block, StringComparison.Ordinal);

        // Nor does the refusal itself travel: it names an absolute path on this platform, which
        // holds the user's account name.
        Assert.DoesNotContain(_root, block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_node_whose_file_has_gone_says_so()
    {
        // The one fact about a node that changes what an answer may promise, so it survives every
        // rung of the ladder.
        var node = FileNode("Legacy.cs") with { Status = GraphNodeStatus.Missing };

        await AddAsync(node);

        var block = await Source().BuildAsync(GraphSelection.Of(node.Id), 8_000, Token);

        Assert.NotNull(block);
        Assert.Contains("(missing)", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_selection_too_wide_to_quote_falls_back_to_where_things_are()
    {
        // Past a handful of files the budget splits so thinly that each excerpt is a heading and two
        // lines, which tells a model less than the path and the summary would.
        await OpenAsync();

        var nodes = new List<GraphNode>();

        for (var i = 0; i < 9; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, $"F{i}.cs"), $"class F{i} {{ }}", Token);

            nodes.Add(FileNode($"F{i}.cs"));
        }

        await AddAsync([.. nodes]);

        var block = await Source().BuildAsync(GraphSelection.Nodes(nodes.Select(n => n.Id)), 24_000, Token);

        Assert.NotNull(block);
        Assert.DoesNotContain("<file", block, StringComparison.Ordinal);
        Assert.Contains("F0.cs", block, StringComparison.Ordinal);
        Assert.Contains("Selected: 9 nodes", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_too_long_for_the_budget_is_clipped_rather_than_dropped()
    {
        // All-or-nothing would cost the selection every excerpt because one file was large, including
        // the small ones that would have fitted. The model is told the text was cut.
        await OpenAsync();

        var nodes = new List<GraphNode>();

        for (var i = 0; i < 3; i++)
        {
            var lines = Enumerable.Range(0, 300).Select(n => $"// line {n} " + new string('x', 180));

            await File.WriteAllLinesAsync(Path.Combine(_root, $"Big{i}.cs"), lines, Token);

            nodes.Add(FileNode($"Big{i}.cs"));
        }

        await AddAsync([.. nodes]);

        var block = await Source().BuildAsync(GraphSelection.Nodes(nodes.Select(n => n.Id)), 8_000, Token);

        Assert.NotNull(block);
        Assert.Contains("<file name=\"Big0.cs\"", block, StringComparison.Ordinal);
        Assert.Contains("truncated", block, StringComparison.Ordinal);
        Assert.InRange(TokenEstimator.EstimateMessage(block), 1, (int)(8_000 * 0.4));
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(4_000)]
    [InlineData(16_000)]
    [InlineData(64_000)]
    public async Task The_block_never_claims_more_than_its_share_of_the_request(int tokenBudget)
    {
        // The selection is a hint about what matters, not the subject of the conversation. A block
        // that took the whole window would answer about the graph with no memory of what was asked.
        await OpenAsync();
        await AddAsync(ProjectOf(60));

        var selection = GraphSelection.Nodes(_graph.Current.Nodes.Select(node => node.Id));
        var block = await Source().BuildAsync(selection, tokenBudget, Token);

        Assert.NotNull(block);
        Assert.InRange(TokenEstimator.EstimateMessage(block), 1, (int)(tokenBudget * 0.4));
    }

    [Fact]
    public async Task A_marquee_over_a_whole_project_degrades_to_a_list_and_says_what_it_left_out()
    {
        // A model told about twelve of two hundred nodes can answer about the twelve. One that
        // believes it was shown everything answers about the project. The budget here is small
        // enough that even a list of titles has to stop partway, which is the only case that can
        // produce the sentence - at a comfortable budget two hundred titles fit and none is dropped.
        await AddAsync(ProjectOf(200));

        var selection = GraphSelection.Nodes(_graph.Current.Nodes.Select(node => node.Id));
        var block = await Source().BuildAsync(selection, 1_200, Token);

        Assert.NotNull(block);
        Assert.Contains("Selected: 200 nodes", block, StringComparison.Ordinal);
        Assert.Contains("omitted to fit the context budget", block, StringComparison.Ordinal);
        Assert.InRange(TokenEstimator.EstimateMessage(block), 1, (int)(1_200 * 0.4) + 64);
    }

    [Fact]
    public async Task The_share_is_a_setting_and_lowering_it_is_obeyed()
    {
        await OpenAsync();
        await AddAsync(ProjectOf(60));

        _settings.With<CanvasSettings>(canvas => canvas.MaxContextShare = 0.05);

        var selection = GraphSelection.Nodes(_graph.Current.Nodes.Select(node => node.Id));
        var block = await Source().BuildAsync(selection, 20_000, Token);

        Assert.NotNull(block);
        Assert.InRange(TokenEstimator.EstimateMessage(block), 1, (int)(20_000 * 0.05) + 64);
    }

    [Fact]
    public async Task A_relation_is_named_from_both_ends()
    {
        // The only thing in the block a directory listing could not have said, and the reason the
        // Canvas is worth selecting on at all.
        var service = new GraphNode
        {
            Id = Guid.CreateVersion7(),
            Kind = GraphNodeKind.Service,
            Key = "node:auth",
            Title = "AuthService",
            Summary = "Signs users in.",
        };

        var database = new GraphNode
        {
            Id = Guid.CreateVersion7(),
            Kind = GraphNodeKind.Database,
            Key = "node:db",
            Title = "Identity store",
        };

        await AddAsync([service, database], [Edge(service.Id, database.Id, GraphEdgeKind.DependsOn)]);

        var fromService = await Source().BuildAsync(GraphSelection.Of(service.Id), 8_000, Token);
        var fromDatabase = await Source().BuildAsync(GraphSelection.Of(database.Id), 8_000, Token);

        Assert.NotNull(fromService);
        Assert.NotNull(fromDatabase);

        // Direction is part of the fact: a store that depends on a service is a different project.
        Assert.Contains("-> depends_on Identity store [database]", fromService, StringComparison.Ordinal);
        Assert.Contains("<- depends_on AuthService [service]", fromDatabase, StringComparison.Ordinal);

        // Depth one, so the other end is named as a thing rather than left as a word in a relation.
        Assert.Contains("Nearby in the graph:", fromService, StringComparison.Ordinal);
        Assert.Contains("Signs users in.", fromService, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_what_was_asked_for_is_pulled_in()
    {
        // Depth zero is how an action on one card asks about that card. Left unclamped on a
        // well-connected graph, two hops is most of the project and the budget goes on neighbours.
        var one = FileNode("A.cs");
        var two = FileNode("B.cs");

        await AddAsync([one, two], [Edge(one.Id, two.Id, GraphEdgeKind.References)]);

        var block = await Source().BuildAsync(
            GraphSelection.Nodes([one.Id], depth: 0),
            8_000,
            Token);

        Assert.NotNull(block);
        Assert.DoesNotContain("Nearby", block, StringComparison.Ordinal);
        Assert.Contains("Selected: 1 node.", block, StringComparison.Ordinal);
    }

    /// <summary>Shorthand for the token every test here passes to every call.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The real builder over the real graph and the real sandbox.</summary>
    private GraphContextSource Source() =>
        new(_graph, _workspace, _settings, new RecordingLogger<GraphContextSource>());

    /// <summary>Opens the scratch folder, for the tests where an excerpt is meant to be readable.</summary>
    private async Task OpenAsync()
    {
        var opened = await _workspace.OpenAsync(_root, Token);

        Assert.True(opened.Success, opened.Error);
    }

    /// <summary>A file node as the indexer would have made it: keyed and sourced by relative path.</summary>
    private static GraphNode FileNode(string key) => new()
    {
        Id = Guid.CreateVersion7(),
        Kind = GraphNodeKind.File,
        Key = key,
        Title = key[(key.LastIndexOf('/') + 1)..],
        Source = WorkspacePath.Parse(key),
        Origin = GraphOrigin.Indexer,
    };

    private static GraphEdge Edge(Guid from, Guid to, GraphEdgeKind kind) => new()
    {
        Id = Guid.CreateVersion7(),
        FromId = from,
        ToId = to,
        Kind = kind,
    };

    /// <summary>Enough nodes to make a budget matter, each with a plausible name and a summary.</summary>
    private static GraphNode[] ProjectOf(int count) =>
    [
        .. Enumerable.Range(0, count).Select(i => FileNode($"src/Feature{i:000}/Service{i:000}.cs") with
        {
            Summary = $"Handles feature {i} and everything the specification says about it.",
        }),
    ];

    private Task AddAsync(params GraphNode[] nodes) => AddAsync(nodes, []);

    private async Task AddAsync(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        List<GraphMutation> mutations =
        [
            .. nodes.Select(node => new GraphMutation.AddNode(node)),
            .. edges.Select(edge => new GraphMutation.AddEdge(edge)),
        ];

        var applied = await _graph.ApplyAsync(
            GraphChangeSet.Create("Set up the test graph", GraphOrigin.User, mutations),
            Token);

        Assert.True(applied.Success, applied.Error);
    }
}
