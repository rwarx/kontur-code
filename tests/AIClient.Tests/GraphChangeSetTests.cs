using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// The write side: the rules a change set obeys, and how it is undone.
/// </summary>
/// <remarks>
/// Every rule here is enforced in one pure function, which is why these tests need no database and no
/// window. The one they exist for above all others is the last group: an indexing pass owns only what
/// it created. Without that, the second walk of a workspace erases every link a person drew and the
/// graph is no more durable than a cache.
/// </remarks>
public sealed class GraphChangeSetTests
{
    [Fact]
    public void A_change_set_starts_as_a_proposal_that_cannot_yet_be_undone()
    {
        var change = GraphChangeSet.Create(
            "Add a caching service",
            GraphOrigin.Chat,
            GraphSample.Adds(GraphSample.Node("CacheService", GraphNodeKind.Service)));

        Assert.Equal(GraphChangeState.Proposed, change.State);
        Assert.Empty(change.Inverse);
        Assert.Null(change.AppliedAt);
        Assert.False(change.CanRevert);
        Assert.NotEqual(Guid.Empty, change.Id);
    }

    [Fact]
    public void Only_an_applied_change_that_recorded_an_inverse_can_be_undone()
    {
        var node = GraphSample.Node("src/a.cs");
        var inverse = GraphSample.Adds(node);

        var applied = GraphChangeSet.Create("x", GraphOrigin.User, GraphSample.Adds(node)) with
        {
            State = GraphChangeState.Applied,
            Inverse = inverse,
        };

        Assert.True(applied.CanRevert);
        Assert.False((applied with { Inverse = [] }).CanRevert);
        Assert.False((applied with { State = GraphChangeState.Reverted }).CanRevert);
    }

    [Fact]
    public void A_change_that_does_nothing_hands_back_the_snapshot_it_was_given()
    {
        // Readers watch the version to decide whether to rebuild. A new snapshot over an unchanged
        // graph makes every one of them work for nothing.
        var snapshot = GraphSample.Snapshot([GraphSample.Node("src")]);

        var result = GraphMutator.Apply(snapshot, []);

        Assert.Same(snapshot, result.Snapshot);
        Assert.False(result.Changed);
        Assert.Equal(snapshot.Version, result.Snapshot.Version);
    }

    [Fact]
    public void An_applied_change_moves_the_version_on()
    {
        var snapshot = GraphSample.Snapshot([]);

        var result = GraphMutator.Apply(snapshot, GraphSample.Adds(GraphSample.Node("src/a.cs")));

        Assert.True(result.Changed);
        Assert.Equal(snapshot.Version + 1, result.Snapshot.Version);
        Assert.Equal(1, result.Snapshot.NodeCount);
    }

    [Fact]
    public void A_node_added_and_then_undone_leaves_the_graph_as_it_was()
    {
        var snapshot = GraphSample.Snapshot([]);
        var node = GraphSample.Node("src/a.cs");

        var forward = GraphMutator.Apply(snapshot, GraphSample.Adds(node));

        Assert.IsType<GraphMutation.AddNode>(Assert.Single(forward.Applied));
        Assert.IsType<GraphMutation.RemoveNode>(Assert.Single(forward.Inverse));

        var back = GraphMutator.Apply(forward.Snapshot, forward.Inverse);

        Assert.Equal(0, back.Snapshot.NodeCount);
    }

    [Fact]
    public void Adding_a_node_that_is_already_there_is_recorded_as_the_update_it_really_is()
    {
        // Add and update are one upsert. An indexing pass re-walking a workspace emits adds for
        // everything it finds, and it must not matter whether the node was there already - but the
        // log has to say what happened, and the inverse has to restore the old node rather than
        // delete it.
        var original = GraphSample.Node("src/a.cs", title: "a.cs");
        var snapshot = GraphSample.Snapshot([original]);

        var renamed = original with { Title = "renamed.cs" };
        var result = GraphMutator.Apply(snapshot, GraphSample.Adds(renamed));

        Assert.IsType<GraphMutation.UpdateNode>(Assert.Single(result.Applied));
        Assert.Equal("renamed.cs", result.Snapshot.Node(original.Id)?.Title);

        var restored = Assert.IsType<GraphMutation.UpdateNode>(Assert.Single(result.Inverse));

        Assert.Equal("a.cs", restored.Node.Title);
    }

    [Fact]
    public void An_update_of_a_node_that_is_not_there_is_refused()
    {
        var result = GraphMutator.Apply(
            GraphSnapshot.Empty,
            [new GraphMutation.UpdateNode(GraphSample.Node("src/a.cs"))]);

        Assert.False(result.Changed);
        Assert.Contains("no node", Assert.Single(result.Refused));
    }

    [Fact]
    public void A_key_that_belongs_to_another_node_is_refused_while_the_rest_of_the_batch_applies()
    {
        // The uniqueness rule storage enforces, mirrored here so that a violation is a sentence
        // rather than an exception out of SaveChanges - by which time the snapshot has already moved.
        var existing = GraphSample.Node("src/a.cs");
        var snapshot = GraphSample.Snapshot([existing]);

        var impostor = GraphSample.Node("src/a.cs");
        var innocent = GraphSample.Node("src/b.cs");

        var result = GraphMutator.Apply(snapshot, GraphSample.Adds(impostor, innocent));

        Assert.Single(result.Applied);
        Assert.Contains("already belongs", Assert.Single(result.Refused));
        Assert.Equal(2, result.Snapshot.NodeCount);
        Assert.Null(result.Snapshot.Node(impostor.Id));
        Assert.NotNull(result.Snapshot.Node(innocent.Id));
    }

    [Fact]
    public void A_node_that_moved_gives_up_its_old_key_in_the_same_batch()
    {
        // Renaming a file and adding a new one in its place is one indexing pass. If the old key were
        // still held, nothing could ever take it again.
        var moved = GraphSample.Node("src/a.cs");
        var snapshot = GraphSample.Snapshot([moved]);

        var replacement = GraphSample.Node("src/a.cs");

        var result = GraphMutator.Apply(
            snapshot,
            [
                new GraphMutation.UpdateNode(moved with { Key = "src/moved/a.cs" }),
                new GraphMutation.AddNode(replacement),
            ]);

        Assert.Empty(result.Refused);
        Assert.Equal(moved.Id, result.Snapshot.FindByKey(GraphNodeKind.File, "src/moved/a.cs")?.Id);
        Assert.Equal(replacement.Id, result.Snapshot.FindByKey(GraphNodeKind.File, "src/a.cs")?.Id);
    }

    [Fact]
    public void An_edge_needs_both_of_its_nodes_to_exist()
    {
        var present = GraphSample.Node("src/a.cs");
        var absent = GraphSample.Node("src/b.cs");
        var snapshot = GraphSample.Snapshot([present]);

        var result = GraphMutator.Apply(
            snapshot,
            GraphSample.Adds(GraphSample.Edge(present, absent, GraphEdgeKind.DependsOn)));

        Assert.False(result.Changed);
        Assert.Contains("both of its nodes", Assert.Single(result.Refused));
    }

    [Fact]
    public void An_edge_may_be_added_in_the_same_batch_as_the_nodes_it_joins()
    {
        // Order inside a change set is load-bearing: this is how an indexing pass emits a folder and
        // the file it contains without two round trips.
        var folder = GraphSample.Node("src", GraphNodeKind.Folder);
        var file = GraphSample.Node("src/a.cs");

        var result = GraphMutator.Apply(
            GraphSnapshot.Empty,
            [
                new GraphMutation.AddNode(folder),
                new GraphMutation.AddNode(file),
                new GraphMutation.AddEdge(GraphSample.Edge(folder, file)),
            ]);

        Assert.Empty(result.Refused);
        Assert.Equal(1, result.Snapshot.EdgeCount);
        Assert.Equal([file.Id], result.Snapshot.Children(folder.Id).Select(n => n.Id));
    }

    [Fact]
    public void An_edge_from_a_node_to_itself_is_refused()
    {
        var node = GraphSample.Node("AuthService", GraphNodeKind.Service);
        var snapshot = GraphSample.Snapshot([node]);

        var result = GraphMutator.Apply(
            snapshot,
            GraphSample.Adds(GraphSample.Edge(node, node, GraphEdgeKind.DependsOn)));

        Assert.False(result.Changed);
        Assert.Contains("says nothing", Assert.Single(result.Refused));
    }

    [Fact]
    public void One_pair_of_nodes_may_hold_two_relationships_but_not_the_same_one_twice()
    {
        // "A depends on B" and "A calls B" are different facts about the same pair and both are
        // allowed; the same fact twice is a duplicate row and a doubled line on the canvas.
        var from = GraphSample.Node("AuthService", GraphNodeKind.Service);
        var to = GraphSample.Node("UserRepository", GraphNodeKind.Service);
        var snapshot = GraphSample.Snapshot([from, to]);

        var result = GraphMutator.Apply(
            snapshot,
            GraphSample.Adds(
                GraphSample.Edge(from, to, GraphEdgeKind.DependsOn),
                GraphSample.Edge(from, to, GraphEdgeKind.Calls),
                GraphSample.Edge(from, to, GraphEdgeKind.DependsOn)));

        Assert.Equal(2, result.Applied.Count);
        Assert.Contains("already joined", Assert.Single(result.Refused));
    }

    [Fact]
    public void Removing_a_node_takes_its_edges_with_it_and_the_undo_brings_them_back()
    {
        // The reason the inverse is computed at the moment of applying rather than when the change was
        // written: inverting "remove this node" needs the node and every edge that touched it, and only
        // the graph as it stood then knows them.
        var folder = GraphSample.Node("src", GraphNodeKind.Folder);
        var file = GraphSample.Node("src/a.cs");
        var edge = GraphSample.Edge(folder, file);
        var snapshot = GraphSample.Snapshot([folder, file], [edge]);

        var forward = GraphMutator.Apply(snapshot, [new GraphMutation.RemoveNode(file.Id)]);

        Assert.Equal(1, forward.Snapshot.NodeCount);
        Assert.Equal(0, forward.Snapshot.EdgeCount);

        // Recorded forwards, undone backwards: the edge goes first and comes back last, because the
        // node it points at has to exist again before it can.
        Assert.IsType<GraphMutation.RemoveEdge>(forward.Applied[0]);
        Assert.IsType<GraphMutation.RemoveNode>(forward.Applied[1]);
        Assert.IsType<GraphMutation.AddNode>(forward.Inverse[0]);
        Assert.IsType<GraphMutation.AddEdge>(forward.Inverse[1]);

        var back = GraphMutator.Apply(forward.Snapshot, forward.Inverse);

        Assert.Empty(back.Refused);
        Assert.Equal(2, back.Snapshot.NodeCount);
        Assert.Equal([file.Id], back.Snapshot.Children(folder.Id).Select(n => n.Id));
        Assert.Equal(edge.Id, Assert.Single(back.Snapshot.Edges).Id);
    }

    [Fact]
    public void The_inverse_of_a_batch_runs_backwards()
    {
        var first = GraphSample.Node("src", GraphNodeKind.Folder);
        var second = GraphSample.Node("src/a.cs");

        var forward = GraphMutator.Apply(
            GraphSnapshot.Empty,
            [
                new GraphMutation.AddNode(first),
                new GraphMutation.AddNode(second),
                new GraphMutation.AddEdge(GraphSample.Edge(first, second)),
            ]);

        Assert.IsType<GraphMutation.RemoveEdge>(forward.Inverse[0]);
        Assert.Equal(second.Id, Assert.IsType<GraphMutation.RemoveNode>(forward.Inverse[1]).NodeId);
        Assert.Equal(first.Id, Assert.IsType<GraphMutation.RemoveNode>(forward.Inverse[2]).NodeId);

        Assert.Equal(0, GraphMutator.Apply(forward.Snapshot, forward.Inverse).Snapshot.NodeCount);
    }

    [Fact]
    public void An_indexing_pass_may_not_alter_or_remove_what_a_person_made()
    {
        // The invariant this file exists for. Every re-walk of a workspace emits the whole tree again,
        // so without this rule the second pass would flatten the component a person drew, the note
        // they attached and the dependency they corrected by hand.
        var mine = GraphSample.Node("Authentication", GraphNodeKind.Component, origin: GraphOrigin.User);
        var indexed = GraphSample.Node("src/a.cs", origin: GraphOrigin.Indexer);
        var drawn = GraphSample.Edge(mine, indexed, GraphEdgeKind.Groups, origin: GraphOrigin.User);
        var snapshot = GraphSample.Snapshot([mine, indexed], [drawn]);

        var result = GraphMutator.Apply(
            snapshot,
            [
                new GraphMutation.UpdateNode(mine with { Title = "Auth" }),
                new GraphMutation.RemoveNode(mine.Id),
                new GraphMutation.RemoveEdge(drawn.Id),
            ],
            GraphOrigin.Indexer);

        Assert.False(result.Changed);
        Assert.Equal(3, result.Refused.Count);
        Assert.Contains("may not alter the User-owned node Authentication", result.Refused[0]);
        Assert.Contains("may not remove the User-owned node Authentication", result.Refused[1]);
        Assert.Contains("may not remove a User-owned edge", result.Refused[2]);
    }

    [Fact]
    public void An_indexing_pass_may_freely_overwrite_what_it_wrote_itself()
    {
        var indexed = GraphSample.Node("src/a.cs", origin: GraphOrigin.Indexer, title: "a.cs");
        var snapshot = GraphSample.Snapshot([indexed]);

        var result = GraphMutator.Apply(
            snapshot,
            GraphSample.Adds(indexed with { Title = "renamed.cs" }),
            GraphOrigin.Indexer);

        Assert.Empty(result.Refused);
        Assert.Equal("renamed.cs", result.Snapshot.Node(indexed.Id)?.Title);
    }

    [Fact]
    public void A_person_may_change_what_the_indexing_pass_wrote()
    {
        // Ownership is a restriction on the indexer alone, not a lock on the node. Correcting a title
        // or deleting a file node by hand has to work, and the next pass will not undo it because an
        // upsert only writes the fields it knows.
        var indexed = GraphSample.Node("src/a.cs", origin: GraphOrigin.Indexer);
        var snapshot = GraphSample.Snapshot([indexed]);

        var result = GraphMutator.Apply(
            snapshot,
            [new GraphMutation.UpdateNode(indexed with { Status = GraphNodeStatus.Archived })],
            GraphOrigin.User);

        Assert.Empty(result.Refused);
        Assert.Equal(GraphNodeStatus.Archived, result.Snapshot.Node(indexed.Id)?.Status);
    }

    [Fact]
    public void The_cascade_takes_a_hand_drawn_edge_when_the_node_it_hung_on_goes()
    {
        // The one place ownership is not consulted. A user's edge is worth keeping, but not at the
        // price of a row pointing at a node that no longer exists - and the inverse restores it.
        var component = GraphSample.Node("Authentication", GraphNodeKind.Component, origin: GraphOrigin.User);
        var deleted = GraphSample.Node("src/a.cs", origin: GraphOrigin.Indexer);
        var drawn = GraphSample.Edge(component, deleted, GraphEdgeKind.Groups, origin: GraphOrigin.User);
        var snapshot = GraphSample.Snapshot([component, deleted], [drawn]);

        var forward = GraphMutator.Apply(
            snapshot,
            [new GraphMutation.RemoveNode(deleted.Id)],
            GraphOrigin.Indexer);

        Assert.Empty(forward.Refused);
        Assert.Equal(0, forward.Snapshot.EdgeCount);

        var back = GraphMutator.Apply(forward.Snapshot, forward.Inverse);

        Assert.Equal(drawn.Id, Assert.Single(back.Snapshot.Edges).Id);
        Assert.Equal(GraphOrigin.User, Assert.Single(back.Snapshot.Edges).Origin);
    }
}
