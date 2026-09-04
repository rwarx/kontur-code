using AIClient.Application.DTOs;
using AIClient.Application.Services;
using AIClient.Domain.Entities;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using AIClient.Domain.Workspace;
using AIClient.Infrastructure.Graph;
using AIClient.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace AIClient.Tests;

/// <summary>
/// The principle the whole design rests on, asserted rather than declared.
/// </summary>
/// <remarks>
/// <para>
/// "Canvas is not the source of truth. Canvas is a spatial projection and interaction surface over
/// the Living Knowledge Graph." That sentence is easy to agree with and easy to violate: one column
/// for a node's colour on <c>GraphNodes</c>, one label kept only on a card, and the graph quietly
/// stops being the thing that knows the project.
/// </para>
/// <para>
/// So the check is destructive. Every row of spatial state is deleted and the application is opened
/// again over the same file: if a single fact about the project has gone with it, the design was
/// wrong, and no amount of documentation would have caught it.
/// </para>
/// </remarks>
public sealed class CanvasIsAProjectionTests : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await TestDatabase.CreateAsync();

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Deleting_every_spatial_row_loses_no_fact_about_the_project()
    {
        var graph = await LoadedAsync();

        await FillAsync(graph);
        await ArrangeAsync(graph);

        var before = graph.Current;

        Assert.Equal(3, await WipeAsync());

        // A second service over the same file, which is what a restart is.
        var reopened = _db.Graph();

        await reopened.LoadAsync(Token);

        var after = reopened.Current;

        Assert.Equal(before.NodeCount, after.NodeCount);
        Assert.Equal(before.EdgeCount, after.EdgeCount);

        foreach (var node in before.Nodes)
        {
            var survivor = after.Node(node.Id);

            Assert.NotNull(survivor);
            Assert.Equal(node.Kind, survivor.Kind);
            Assert.Equal(node.Key, survivor.Key);
            Assert.Equal(node.Title, survivor.Title);
            Assert.Equal(node.Summary, survivor.Summary);
            Assert.Equal(node.Source, survivor.Source);
            Assert.Equal(node.StartLine, survivor.StartLine);
            Assert.Equal(node.EndLine, survivor.EndLine);
            Assert.Equal(node.Status, survivor.Status);
            Assert.Equal(node.Origin, survivor.Origin);
            Assert.Equal(node.Metadata, survivor.Metadata);
        }

        foreach (var edge in before.Edges)
        {
            var survivor = after.Edges.SingleOrDefault(candidate => candidate.Id == edge.Id);

            Assert.NotNull(survivor);
            Assert.Equal(edge.Kind, survivor.Kind);
            Assert.Equal((edge.FromId, edge.ToId), (survivor.FromId, survivor.ToId));
            Assert.Equal(edge.Origin, survivor.Origin);
        }
    }

    [Fact]
    public async Task Deleting_every_spatial_row_leaves_the_history_of_the_graph_intact()
    {
        // The journal is how the user sees what an agent did, and it is a graph table for exactly
        // this reason: a wiped surface must not also wipe the record of how the project got here.
        var graph = await LoadedAsync();

        await FillAsync(graph);
        await ArrangeAsync(graph);

        var before = await graph.HistoryAsync(cancellationToken: Token);

        await WipeAsync();

        var reopened = _db.Graph();

        await reopened.LoadAsync(Token);

        var after = await reopened.HistoryAsync(cancellationToken: Token);

        Assert.NotEmpty(before);
        Assert.Equal(
            before.Select(change => (change.Id, change.Summary, change.State)),
            after.Select(change => (change.Id, change.Summary, change.State)));
    }

    [Fact]
    public async Task A_surface_wiped_of_positions_draws_itself_again()
    {
        // The other half of the claim. Losing no fact would be a hollow guarantee if the canvas came
        // back blank: what is lost is the arrangement, and the arrangement is recomputed.
        var graph = await LoadedAsync();

        await FillAsync(graph);
        await ArrangeAsync(graph);
        await WipeAsync();

        var view = await _db.Canvas().GetDefaultAsync(Token);

        Assert.Empty(view.Placements);
        Assert.Empty(view.Areas);
        Assert.Equal(CanvasViewport.Default, view.Viewport);

        var placements = CanvasLayout.PlaceMissing(graph.Current, view.Placements);

        // One card per node, all of them somewhere a camera can reach, and no two on the same spot.
        Assert.Equal(graph.Current.NodeCount, placements.Count);
        Assert.Equal(graph.Current.NodeCount, placements.Select(p => p.NodeId).Distinct().Count());
        Assert.Equal(placements.Count, placements.Select(p => (p.X, p.Y)).Distinct().Count());
        Assert.All(placements, placement => Assert.True(double.IsFinite(placement.X + placement.Y)));
    }

    [Fact]
    public async Task The_layout_a_wipe_falls_back_to_is_the_same_one_every_time()
    {
        // Because it has to be. A surface that recomputes differently on each launch cannot be
        // remembered, and "the position is lost but the shape is familiar" is the whole reason the
        // spatial tables are safe to lose.
        var graph = await LoadedAsync();

        await FillAsync(graph);

        var first = CanvasLayout.PlaceMissing(graph.Current, []);
        var second = CanvasLayout.PlaceMissing(graph.Current, []);

        Assert.Equal(
            first.Select(p => (p.NodeId, p.X, p.Y)),
            second.Select(p => (p.NodeId, p.X, p.Y)));
    }

    [Fact]
    public async Task No_graph_table_has_a_column_the_canvas_should_own()
    {
        // The structural half of the principle, checked against the database rather than the model:
        // a geometry column on a graph table is how this design would decay, and it would decay one
        // convenient column at a time.
        string[] spatial =
        [
            "X", "Y", "Width", "Height", "PanX", "PanY", "Zoom",
            "IsCollapsed", "IsPinned", "Accent", "LayoutMode", "ViewId",
        ];

        foreach (var table in (string[])["GraphNodes", "GraphEdges", "GraphChanges"])
        {
            var columns = await ColumnsAsync(table);

            Assert.NotEmpty(columns);
            Assert.All(spatial, column => Assert.DoesNotContain(column, columns, StringComparer.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task A_placement_holds_geometry_and_a_reference_and_nothing_else()
    {
        // The converse, and the one that keeps the AI honest: if a card could carry a title or a
        // kind of its own, the model would be reasoning about a project the graph does not describe.
        var columns = await ColumnsAsync("CanvasPlacements");

        Assert.Contains("NodeId", columns, StringComparer.Ordinal);
        Assert.All(
            (string[])["Kind", "Key", "Title", "Summary", "Metadata", "Status", "Origin", "SourcePath"],
            column => Assert.DoesNotContain(column, columns, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Shorthand for the token every test here passes to every call.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<GraphService> LoadedAsync()
    {
        var graph = _db.Graph();

        await graph.LoadAsync(Token);

        return graph;
    }

    /// <summary>Column names of a table, as SQLite itself reports them.</summary>
    /// <remarks>
    /// Read from the file rather than from the EF model on purpose: the model is the thing being
    /// checked, and a migration that disagreed with it would pass a test that asked the model.
    /// </remarks>
    private async Task<List<string>> ColumnsAsync(string table)
    {
        await using var db = _db.CreateDbContext();
        await using var command = db.Database.GetDbConnection().CreateCommand();

        await db.Database.OpenConnectionAsync(Token);

        // Interpolation, because a table name cannot be a parameter - and the only values reaching
        // it are the literals above.
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";

        var columns = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    /// <summary>
    /// A project worth losing the arrangement of: a tree, a hand-drawn relation across it, a summary,
    /// metadata, a line span and a node somebody archived.
    /// </summary>
    private async Task FillAsync(GraphService graph)
    {
        var project = new GraphNode
        {
            Id = Guid.CreateVersion7(),
            Kind = GraphNodeKind.Project,
            Key = ".",
            Title = "AcmeApp",
            Origin = GraphOrigin.Indexer,
        };

        var service = new GraphNode
        {
            Id = Guid.CreateVersion7(),
            Kind = GraphNodeKind.File,
            Key = "src/AuthService.cs",
            Title = "AuthService.cs",
            Summary = "Signs users in and issues the session cookie.",
            Source = WorkspacePath.Parse("src/AuthService.cs"),
            StartLine = 12,
            EndLine = 96,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["language"] = "csharp",
                ["namespace"] = "Acme.Auth",
            },
            Origin = GraphOrigin.Indexer,
        };

        var decision = new GraphNode
        {
            Id = Guid.CreateVersion7(),
            Kind = GraphNodeKind.Decision,
            Key = $"node:{Guid.CreateVersion7():n}",
            Title = "Cookies over bearer tokens",
            Summary = "Chosen in chat, so it exists nowhere in the code.",
            Status = GraphNodeStatus.Archived,
            Origin = GraphOrigin.Chat,
        };

        var applied = await graph.ApplyAsync(
            GraphChangeSet.Create(
                "Index the project and record what was decided",
                GraphOrigin.User,
                [
                    new GraphMutation.AddNode(project),
                    new GraphMutation.AddNode(service),
                    new GraphMutation.AddNode(decision),
                    new GraphMutation.AddEdge(new GraphEdge
                    {
                        Id = Guid.CreateVersion7(),
                        FromId = project.Id,
                        ToId = service.Id,
                        Kind = GraphEdgeKind.Contains,
                        Origin = GraphOrigin.Indexer,
                    }),
                    new GraphMutation.AddEdge(new GraphEdge
                    {
                        Id = Guid.CreateVersion7(),
                        FromId = decision.Id,
                        ToId = service.Id,
                        Kind = GraphEdgeKind.Decides,
                        Origin = GraphOrigin.User,
                    }),
                ]),
            Token);

        Assert.True(applied.Success, applied.Error);
    }

    /// <summary>Positions, a camera and a frame: everything a person could arrange.</summary>
    private async Task ArrangeAsync(GraphService graph)
    {
        var view = await _db.Canvas().GetDefaultAsync(Token);

        await _db.Canvas().SavePlacementsAsync(
            view.Id,
            [.. CanvasLayout.Arrange(graph.Current).Select(placement => placement with { IsPinned = true })],
            Token);

        await _db.Canvas().SaveViewportAsync(view.Id, new CanvasViewport(-320, 64, 0.75), Token);

        // Written directly because nothing in stage 1 authors an area yet, and a deletion story that
        // skipped the third spatial table would be a promise about two of them.
        await using var db = _db.CreateDbContext();

        db.Set<CanvasAreaRow>().Add(new CanvasAreaRow
        {
            ViewId = view.Id,
            Title = "Authentication",
            X = -40,
            Y = -40,
            Width = 640,
            Height = 420,
        });

        await db.SaveChangesAsync(Token);
    }

    /// <summary>
    /// Deletes every row of spatial state, and reports how many of the three tables had any.
    /// </summary>
    /// <remarks>
    /// Raw SQL rather than the store, because no part of the product can do this - which is the
    /// point. The question being asked is what the application does when it finds the tables empty,
    /// however they got that way: a corrupted file, a support instruction, a hand-edited database.
    /// </remarks>
    private async Task<int> WipeAsync()
    {
        await using var db = _db.CreateDbContext();

        // Spelled out one statement at a time rather than looped over a list of names: a table name
        // cannot be a parameter, and a literal is the only form of this that is above suspicion.
        int[] counts =
        [
            await db.Database.ExecuteSqlRawAsync("DELETE FROM CanvasPlacements", Token),
            await db.Database.ExecuteSqlRawAsync("DELETE FROM CanvasAreas", Token),
            await db.Database.ExecuteSqlRawAsync("DELETE FROM CanvasViews", Token),
        ];

        return counts.Count(deleted => deleted > 0);
    }
}
