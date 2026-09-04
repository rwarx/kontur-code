using AIClient.Application.DTOs;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// The graph as the application uses it: in memory for reading, SQLite for keeping.
/// </summary>
/// <remarks>
/// Every persistence claim here is read back through a second service over the same file, because
/// "the node is in the snapshot" and "the node survives closing the application" are different
/// claims and only the second one is worth anything. Which mutations are legal is settled in
/// <see cref="GraphChangeSetTests"/>; this file is about the journal, the announcement and the lock.
/// </remarks>
public sealed class GraphServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await TestDatabase.CreateAsync();

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Nothing_may_be_written_before_the_graph_has_been_read()
    {
        // The order the shell does it in. A write that slipped through first would be applied to an
        // empty graph, and the log would then hold an inverse that deletes what storage really has.
        var service = _db.Graph();
        var change = GraphChangeSet.Create(
            "Add a.cs",
            GraphOrigin.Indexer,
            GraphSample.Adds(GraphSample.Node("src/a.cs")));

        var applied = await service.ApplyAsync(change);
        var proposed = await service.ProposeAsync(change);

        Assert.False(applied.Success);
        Assert.False(proposed.Success);
        Assert.Contains("has not been read", applied.Error);
        Assert.Contains("has not been read", proposed.Error);
        Assert.Equal(0, service.Current.NodeCount);
    }

    [Fact]
    public async Task An_unread_graph_and_an_empty_one_look_alike_from_outside()
    {
        // What the application shows before a folder is opened. The canvas renders this state rather
        // than waiting for one, which is why the empty snapshot is handed out before any read.
        var service = _db.Graph();

        Assert.False(service.IsLoaded);
        Assert.Same(GraphSnapshot.Empty, service.Current);

        await service.LoadAsync();

        Assert.True(service.IsLoaded);
        Assert.Equal(0, service.Current.NodeCount);
        Assert.Equal(1L, service.Current.Version);
    }

    [Fact]
    public async Task A_change_survives_a_restart()
    {
        // The nodes carry the origin, not only the change set that brought them: permission to touch
        // is decided per node, so an indexing pass that forgot to stamp what it emitted would be
        // refused its own rows on the next walk.
        var folder = GraphSample.Node("src", GraphNodeKind.Folder, origin: GraphOrigin.Indexer, title: "src");
        var file = GraphSample.Node("src/a.cs", origin: GraphOrigin.Indexer, title: "a.cs");

        var first = _db.Graph();
        await first.LoadAsync();

        var result = await first.ApplyAsync(GraphChangeSet.Create(
            "Index the workspace",
            GraphOrigin.Indexer,
            [
                new GraphMutation.AddNode(folder),
                new GraphMutation.AddNode(file),
                new GraphMutation.AddEdge(GraphSample.Edge(folder, file, origin: GraphOrigin.Indexer)),
            ]));

        Assert.True(result.Success);

        var second = _db.Graph();
        await second.LoadAsync();

        Assert.Equal(2, second.Current.NodeCount);
        Assert.Equal(["a.cs"], second.Current.Children(folder.Id).Select(n => n.Title));
        Assert.Equal(GraphOrigin.Indexer, second.Current.Node(file.Id)?.Origin);
        Assert.Equal(GraphNodeKind.Folder, second.Current.Node(folder.Id)?.Kind);
    }

    [Fact]
    public async Task An_applied_change_announces_the_snapshot_and_what_took_effect()
    {
        // The canvas redraws from this and nothing else, so what it carries has to be what happened -
        // not the request, and not a hint to go and look.
        var service = _db.Graph();
        await service.LoadAsync();

        var events = new List<GraphChangedEventArgs>();
        service.Changed += (_, args) => events.Add(args);

        var node = GraphSample.Node("src/a.cs");
        var change = GraphChangeSet.Create("Add a.cs", GraphOrigin.Chat, GraphSample.Adds(node));

        await service.ApplyAsync(change);

        var announced = Assert.Single(events);

        Assert.Same(service.Current, announced.Snapshot);
        Assert.Equal(GraphChangeState.Applied, announced.State);
        Assert.Equal(GraphOrigin.Chat, announced.Origin);
        Assert.Equal(change.Id, announced.ChangeId);
        Assert.False(announced.IsReload);
        Assert.Equal(
            node.Id,
            Assert.IsType<GraphMutation.AddNode>(Assert.Single(announced.Applied)).Node.Id);
    }

    [Fact]
    public async Task A_change_that_took_effect_on_nothing_is_neither_announced_nor_recorded()
    {
        // A refusal is still a successful call - the caller asked and got an answer - but the version
        // must not move, or every view rebuilds for nothing and the log fills with entries that did
        // nothing.
        var service = _db.Graph();
        await service.LoadAsync();

        var announcements = 0;
        service.Changed += (_, _) => announcements++;

        var result = await service.ApplyAsync(GraphChangeSet.Create(
            "Rename a node that is not there",
            GraphOrigin.Agent,
            [new GraphMutation.UpdateNode(GraphSample.Node("src/ghost.cs"))]));

        Assert.True(result.Success);
        Assert.False(result.Value!.Changed);
        Assert.Contains("no node", Assert.Single(result.Value.Refused));
        Assert.Equal(0, announcements);
        Assert.Equal(1L, service.Current.Version);
        Assert.Empty(await service.HistoryAsync());
    }

    [Fact]
    public async Task A_proposal_is_written_down_and_drawn_but_moves_nothing()
    {
        // Section 8 in one test: "add a caching service between the API and the database" becomes a
        // ghost card with Apply and Discard on it, not a silent edit of the project's model.
        var service = _db.Graph();
        await service.LoadAsync();

        GraphChangedEventArgs? announced = null;
        service.Changed += (_, args) => announced = args;

        var cache = GraphSample.Node("CacheService", GraphNodeKind.Service, origin: GraphOrigin.Chat);
        var proposal = await service.ProposeAsync(GraphChangeSet.Create(
            "Add a caching service",
            GraphOrigin.Chat,
            GraphSample.Adds(cache)));

        Assert.True(proposal.Success);
        Assert.Equal(GraphChangeState.Proposed, proposal.Value!.State);
        Assert.Equal(0, service.Current.NodeCount);
        Assert.Equal(1L, service.Current.Version);

        // Announced all the same: the mutations are what the canvas draws as ghosts, and the state is
        // what stops it drawing them as fact.
        Assert.Equal(GraphChangeState.Proposed, announced?.State);
        Assert.Single(announced!.Applied);
        Assert.Same(service.Current, announced.Snapshot);

        var logged = Assert.Single(await service.HistoryAsync());

        Assert.Equal(GraphChangeState.Proposed, logged.State);
        Assert.False(logged.CanRevert);
        Assert.Null(logged.AppliedAt);
    }

    [Fact]
    public async Task A_proposal_that_suggests_nothing_or_could_not_apply_is_turned_down_at_once()
    {
        // Refused while the model is still in a position to try something else, rather than at the
        // moment a person presses Apply on a ghost that was never going to work.
        var node = GraphSample.Node("AuthService", GraphNodeKind.Service);

        var service = _db.Graph();
        await service.LoadAsync();
        await service.ApplyAsync(GraphChangeSet.Create(
            "Add the service",
            GraphOrigin.User,
            GraphSample.Adds(node)));

        var empty = await service.ProposeAsync(GraphChangeSet.Create("Do something", GraphOrigin.Agent));
        var impossible = await service.ProposeAsync(GraphChangeSet.Create(
            "Make it depend on itself",
            GraphOrigin.Agent,
            GraphSample.Adds(GraphSample.Edge(node, node, GraphEdgeKind.DependsOn))));

        Assert.False(empty.Success);
        Assert.Equal("A proposal has to suggest something.", empty.Error);
        Assert.False(impossible.Success);
        Assert.Contains("says nothing", impossible.Error);

        // Neither reached the log: an entry a person cannot act on is noise in the timeline.
        Assert.Single(await service.HistoryAsync());
    }

    [Fact]
    public async Task Accepting_a_proposal_applies_it_and_rewrites_the_entry_it_already_had()
    {
        var service = _db.Graph();
        await service.LoadAsync();

        var cache = GraphSample.Node("CacheService", GraphNodeKind.Service, origin: GraphOrigin.Chat);
        var proposal = await service.ProposeAsync(GraphChangeSet.Create(
            "Add a caching service",
            GraphOrigin.Chat,
            GraphSample.Adds(cache)));

        var accepted = await service.AcceptAsync(proposal.Value!.Id);

        Assert.True(accepted.Success);
        Assert.NotNull(service.Current.Node(cache.Id));
        Assert.Equal(2L, service.Current.Version);

        var logged = Assert.Single(await service.HistoryAsync());

        Assert.Equal(GraphChangeState.Applied, logged.State);
        Assert.NotNull(logged.AppliedAt);
        Assert.True(logged.CanRevert);

        // A stale Apply button on a card that is no longer a ghost says so in words.
        var again = await service.AcceptAsync(proposal.Value.Id);

        Assert.False(again.Success);
        Assert.Equal("Change \"Add a caching service\" is applied, not a proposal.", again.Error);
    }

    [Fact]
    public async Task A_discarded_proposal_stays_in_the_log_and_leaves_the_graph_alone()
    {
        var service = _db.Graph();
        await service.LoadAsync();

        var proposal = await service.ProposeAsync(GraphChangeSet.Create(
            "Add a caching service",
            GraphOrigin.Chat,
            GraphSample.Adds(GraphSample.Node("CacheService", GraphNodeKind.Service))));

        var discarded = await service.DiscardAsync(proposal.Value!.Id);

        Assert.True(discarded.Success);
        Assert.Equal(GraphChangeState.Discarded, discarded.Value!.State);
        Assert.Equal(0, service.Current.NodeCount);
        Assert.Equal(1L, service.Current.Version);

        // Kept rather than deleted: what a model suggested and a person turned down is a fact about
        // the project, and one worth having when the same suggestion arrives again.
        var logged = Assert.Single(await service.HistoryAsync(GraphChangeState.Discarded));

        Assert.Equal(proposal.Value.Id, logged.Id);
        Assert.False((await service.AcceptAsync(proposal.Value.Id)).Success);
    }

    [Fact]
    public async Task An_applied_change_can_be_undone_and_the_log_says_so_without_growing()
    {
        var folder = GraphSample.Node("src", GraphNodeKind.Folder, origin: GraphOrigin.Indexer);
        var file = GraphSample.Node("src/a.cs", origin: GraphOrigin.Indexer);

        var service = _db.Graph();
        await service.LoadAsync();

        var change = GraphChangeSet.Create(
            "Index the workspace",
            GraphOrigin.Indexer,
            [
                new GraphMutation.AddNode(folder),
                new GraphMutation.AddNode(file),
                new GraphMutation.AddEdge(GraphSample.Edge(folder, file, origin: GraphOrigin.Indexer)),
            ]);

        await service.ApplyAsync(change);

        var undone = await service.RevertAsync(change.Id);

        Assert.True(undone.Success);
        Assert.Equal(0, service.Current.NodeCount);
        Assert.Equal(0, service.Current.EdgeCount);

        // One entry, now marked undone. This log records how the project got to where it is; a pair
        // saying "went back" and "came forward" would make it a record of which buttons were pressed.
        var logged = Assert.Single(await service.HistoryAsync());

        Assert.Equal(GraphChangeState.Reverted, logged.State);
        Assert.False((await service.RevertAsync(change.Id)).Success);

        // And it reached the file, not only the snapshot.
        var reopened = _db.Graph();
        await reopened.LoadAsync();

        Assert.Equal(0, reopened.Current.NodeCount);
        Assert.Equal(0, reopened.Current.EdgeCount);
    }

    [Fact]
    public async Task A_change_the_log_never_heard_of_is_reported_rather_than_thrown()
    {
        // Every one of these is reachable from a button on a card the canvas has not refreshed.
        var service = _db.Graph();
        await service.LoadAsync();

        var id = Guid.CreateVersion7();
        var expected = $"There is no change {id} in the log.";

        Assert.Equal(expected, (await service.AcceptAsync(id)).Error);
        Assert.Equal(expected, (await service.DiscardAsync(id)).Error);
        Assert.Equal(expected, (await service.RevertAsync(id)).Error);
    }

    [Fact]
    public async Task A_change_with_no_recorded_inverse_refuses_to_be_undone()
    {
        // A proposal has no inverse until it is accepted, so Undo on one is a state error rather than
        // a silent no-op.
        var service = _db.Graph();
        await service.LoadAsync();

        var proposal = await service.ProposeAsync(GraphChangeSet.Create(
            "Add a caching service",
            GraphOrigin.Chat,
            GraphSample.Adds(GraphSample.Node("CacheService", GraphNodeKind.Service))));

        var undone = await service.RevertAsync(proposal.Value!.Id);

        Assert.False(undone.Success);
        Assert.Equal(
            "Change \"Add a caching service\" is a proposal, not applied.",
            undone.Error);
    }

    [Fact]
    public async Task The_history_reads_newest_first_and_can_be_asked_for_one_state()
    {
        var service = _db.Graph();
        await service.LoadAsync();

        // Pinned rather than left to the clock: three writes inside one millisecond would tie, and a
        // timeline that reorders itself between runs is unusable however correct each row is.
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);

        for (var i = 0; i < 3; i++)
        {
            await service.ApplyAsync(GraphChangeSet.Create(
                $"Change {i}",
                GraphOrigin.User,
                GraphSample.Adds(GraphSample.Node($"src/{i}.cs"))) with { CreatedAt = start.AddMinutes(i) });
        }

        await service.ProposeAsync(GraphChangeSet.Create(
            "A suggestion",
            GraphOrigin.Chat,
            GraphSample.Adds(GraphSample.Node("CacheService", GraphNodeKind.Service))) with
            {
                CreatedAt = start.AddMinutes(3),
            });

        Assert.Equal(
            ["A suggestion", "Change 2", "Change 1", "Change 0"],
            (await service.HistoryAsync()).Select(change => change.Summary));

        Assert.Equal(
            ["A suggestion"],
            (await service.HistoryAsync(GraphChangeState.Proposed)).Select(change => change.Summary));

        Assert.Equal(
            ["A suggestion", "Change 2"],
            (await service.HistoryAsync(limit: 2)).Select(change => change.Summary));

        Assert.Empty(await service.HistoryAsync(limit: 0));
    }

    [Fact]
    public async Task A_second_indexing_pass_does_not_erase_what_a_person_drew()
    {
        // The invariant the whole design leans on, end to end and through storage. Two walks of the
        // same workspace with a hand-made component in between: the second walk re-emits everything
        // it found and takes nothing of the person's with it.
        var folder = GraphSample.Node("src", GraphNodeKind.Folder, origin: GraphOrigin.Indexer);
        var file = GraphSample.Node("src/a.cs", origin: GraphOrigin.Indexer, title: "a.cs");
        var walk = GraphChangeSet.Create(
            "Index the workspace",
            GraphOrigin.Indexer,
            [
                new GraphMutation.AddNode(folder),
                new GraphMutation.AddNode(file),
                new GraphMutation.AddEdge(GraphSample.Edge(folder, file, origin: GraphOrigin.Indexer)),
            ]);

        var service = _db.Graph();
        await service.LoadAsync();
        await service.ApplyAsync(walk);

        var component = GraphSample.Node("Authentication", GraphNodeKind.Component, title: "Authentication");
        var drawn = GraphSample.Edge(component, file, GraphEdgeKind.Groups);

        await service.ApplyAsync(GraphChangeSet.Create(
            "Group a.cs under Authentication",
            GraphOrigin.User,
            [new GraphMutation.AddNode(component), new GraphMutation.AddEdge(drawn)]));

        // The second pass: the same tree again, plus the tidying an indexer would do for a component
        // it does not know about.
        var again = await service.ApplyAsync(walk with
        {
            Id = Guid.CreateVersion7(),
            Mutations = [.. walk.Mutations, new GraphMutation.RemoveNode(component.Id)],
        });

        Assert.True(again.Success);
        Assert.Contains("may not remove the User-owned node Authentication", Assert.Single(again.Value!.Refused));

        var reopened = _db.Graph();
        await reopened.LoadAsync();

        Assert.Equal(3, reopened.Current.NodeCount);
        Assert.Equal("a.cs", reopened.Current.Node(file.Id)?.Title);
        Assert.Equal([file.Id], reopened.Current.Children(component.Id).Select(node => node.Id));
        Assert.Equal(GraphOrigin.User, reopened.Current.Edges.Single(e => e.Id == drawn.Id).Origin);
    }

    [Fact]
    public async Task A_reload_announces_itself_and_hands_out_a_version_nobody_has_seen()
    {
        // What happens after something outside this instance writes - an indexing pass in another
        // scope, or a repair by hand. Readers compare the version and rebuild rather than diffing.
        var first = _db.Graph();
        await first.LoadAsync();

        var second = _db.Graph();
        await second.LoadAsync();

        await second.ApplyAsync(GraphChangeSet.Create(
            "Add a.cs",
            GraphOrigin.Indexer,
            GraphSample.Adds(GraphSample.Node("src/a.cs", origin: GraphOrigin.Indexer))));

        GraphChangedEventArgs? announced = null;
        first.Changed += (_, args) => announced = args;

        Assert.Equal(0, first.Current.NodeCount);

        await first.ReloadAsync();

        Assert.Equal(1, first.Current.NodeCount);
        Assert.True(announced?.IsReload);
        Assert.Empty(announced!.Applied);
        Assert.Same(first.Current, announced.Snapshot);
        Assert.Equal(2L, first.Current.Version);
    }

    [Fact]
    public async Task Writers_that_arrive_together_are_serialised_and_none_is_lost()
    {
        // An indexing pass writes from a background task while the canvas reads on the UI thread.
        // A version that skipped or a node that never landed would show up as a flake; this is what
        // turns that flake into a failure.
        var service = _db.Graph();
        await service.LoadAsync();

        var writes = Enumerable.Range(0, 24).Select(i => service.ApplyAsync(GraphChangeSet.Create(
            $"Add {i}",
            GraphOrigin.Indexer,
            GraphSample.Adds(GraphSample.Node($"src/{i}.cs", origin: GraphOrigin.Indexer)))));

        var results = await Task.WhenAll(writes);

        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(24, service.Current.NodeCount);
        Assert.Equal(25L, service.Current.Version);

        var reopened = _db.Graph();
        await reopened.LoadAsync();

        Assert.Equal(24, reopened.Current.NodeCount);
        Assert.Equal(24, (await reopened.HistoryAsync()).Count);
    }
}
