using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// The read side: what a projection is allowed to ask the graph, and what it gets back.
/// </summary>
/// <remarks>
/// These are the queries a canvas runs on every hover, every drill-in and every selection, so they
/// are also where a wrong answer is least visible - a missing edge just looks like a project with one
/// fewer relationship in it.
/// </remarks>
public sealed class GraphSnapshotTests
{
    [Fact]
    public void An_edge_with_an_end_that_is_not_there_is_dropped()
    {
        // Rows outlive the nodes they point at when storage is repaired by hand or a migration goes
        // sideways. Keeping the edge would make every consumer re-check both endpoints.
        var kept = GraphSample.Node("src");
        var gone = GraphSample.Node("deleted");

        var snapshot = GraphSample.Snapshot([kept], [GraphSample.Edge(kept, gone)]);

        Assert.Equal(1, snapshot.NodeCount);
        Assert.Equal(0, snapshot.EdgeCount);
        Assert.Empty(snapshot.Outgoing(kept.Id));
    }

    [Fact]
    public void Two_nodes_claiming_one_key_leave_the_later_one_holding_it()
    {
        // A defect in storage, and not a reason to refuse to open the project. The next write
        // corrects it; until then the graph is usable.
        var first = GraphSample.Node("src/Program.cs", title: "first");
        var second = GraphSample.Node("src/Program.cs", title: "second");

        var snapshot = GraphSample.Snapshot([first, second]);

        Assert.Equal(2, snapshot.NodeCount);
        Assert.Equal("second", snapshot.FindByKey(GraphNodeKind.File, "src/Program.cs")?.Title);
    }

    [Fact]
    public void A_node_is_found_by_the_kind_and_key_an_indexing_pass_would_upsert_on()
    {
        var file = GraphSample.Node("src/Auth/AuthService.cs");
        var type = GraphSample.Node("AIClient.Auth.AuthService", GraphNodeKind.Class);

        var snapshot = GraphSample.Snapshot([file, type]);

        Assert.Equal(file.Id, snapshot.FindByKey(GraphNodeKind.File, "src/Auth/AuthService.cs")?.Id);
        Assert.Equal(type.Id, snapshot.FindByKey(GraphNodeKind.Class, "AIClient.Auth.AuthService")?.Id);

        // Same key, wrong kind: two different things may share a name, so the pair is the identity.
        Assert.Null(snapshot.FindByKey(GraphNodeKind.Class, "src/Auth/AuthService.cs"));
    }

    [Fact]
    public void Adjacency_is_indexed_in_both_directions()
    {
        var service = GraphSample.Node("AuthService", GraphNodeKind.Service);
        var repository = GraphSample.Node("UserRepository", GraphNodeKind.Service);
        var edge = GraphSample.Edge(service, repository, GraphEdgeKind.DependsOn);

        var snapshot = GraphSample.Snapshot([service, repository], [edge]);

        Assert.Equal([edge.Id], snapshot.Outgoing(service.Id).Select(e => e.Id));
        Assert.Empty(snapshot.Incoming(service.Id));
        Assert.Equal([edge.Id], snapshot.Incoming(repository.Id).Select(e => e.Id));
        Assert.Single(snapshot.EdgesOf(repository.Id));
    }

    [Fact]
    public void A_neighbourhood_walks_edges_in_both_directions()
    {
        // Asked about AuthService, a person means what it uses and what uses it. A directed walk
        // would answer half the question and look like a complete answer.
        var api = GraphSample.Node("Api", GraphNodeKind.Api);
        var service = GraphSample.Node("AuthService", GraphNodeKind.Service);
        var repository = GraphSample.Node("UserRepository", GraphNodeKind.Service);
        var database = GraphSample.Node("Postgres", GraphNodeKind.Database);

        var snapshot = GraphSample.Snapshot(
            [api, service, repository, database],
            [
                GraphSample.Edge(api, service, GraphEdgeKind.DependsOn),
                GraphSample.Edge(service, repository, GraphEdgeKind.DependsOn),
                GraphSample.Edge(repository, database, GraphEdgeKind.DependsOn),
            ]);

        Assert.Equal([service.Id], snapshot.Neighbourhood([service.Id], depth: 0));

        var oneHop = snapshot.Neighbourhood([service.Id], depth: 1);

        Assert.Equal(3, oneHop.Count);
        Assert.Contains(api.Id, oneHop);
        Assert.Contains(repository.Id, oneHop);
        Assert.DoesNotContain(database.Id, oneHop);

        Assert.Equal(4, snapshot.Neighbourhood([service.Id], depth: 2).Count);
    }

    [Fact]
    public void A_cycle_terminates()
    {
        // Any real dependency graph has one, and a walk without a visited set recurses until the
        // stack gives out - on a user's project, not here.
        var a = GraphSample.Node("A");
        var b = GraphSample.Node("B");
        var c = GraphSample.Node("C");

        var snapshot = GraphSample.Snapshot(
            [a, b, c],
            [
                GraphSample.Edge(a, b, GraphEdgeKind.Calls),
                GraphSample.Edge(b, c, GraphEdgeKind.Calls),
                GraphSample.Edge(c, a, GraphEdgeKind.Calls),
            ]);

        Assert.Equal(3, snapshot.Neighbourhood([a.Id], depth: 50).Count);
    }

    [Fact]
    public void A_seed_that_is_not_in_the_graph_is_ignored_rather_than_invented()
    {
        var node = GraphSample.Node("src");
        var snapshot = GraphSample.Snapshot([node]);

        Assert.Equal([node.Id], snapshot.Neighbourhood([node.Id, Guid.CreateVersion7()], depth: 3));
    }

    [Fact]
    public void A_subgraph_keeps_only_the_edges_whose_both_ends_survived()
    {
        // This is what an AI step is handed. An edge to a node that was left out would read as a
        // relationship with nothing on the other end, and the model would ask about the nothing.
        var api = GraphSample.Node("Api", GraphNodeKind.Api);
        var service = GraphSample.Node("AuthService", GraphNodeKind.Service);
        var outside = GraphSample.Node("Postgres", GraphNodeKind.Database);

        var snapshot = GraphSample.Snapshot(
            [api, service, outside],
            [
                GraphSample.Edge(api, service, GraphEdgeKind.DependsOn),
                GraphSample.Edge(service, outside, GraphEdgeKind.DependsOn),
            ]);

        var selected = snapshot.Subgraph([api.Id, service.Id]);

        Assert.Equal(2, selected.NodeCount);
        Assert.Equal(1, selected.EdgeCount);
        Assert.Null(selected.Node(outside.Id));

        // The same version: a restriction is a view of one instant, not a new one.
        Assert.Equal(snapshot.Version, selected.Version);
    }

    [Fact]
    public void Children_put_folders_first_and_then_read_alphabetically()
    {
        // What the tree and a drill-in both show. Stable between runs, because a list that reorders
        // itself on every index pass is unusable however correct it is.
        var root = GraphSample.Node("src", GraphNodeKind.Folder);
        var auth = GraphSample.Node("src/Auth", GraphNodeKind.Folder, title: "Auth");
        var second = GraphSample.Node("src/b.cs", title: "b.cs");
        var first = GraphSample.Node("src/a.cs", title: "a.cs");

        var snapshot = GraphSample.Snapshot(
            [root, auth, second, first],
            [
                GraphSample.Edge(root, second),
                GraphSample.Edge(root, first),
                GraphSample.Edge(root, auth),
            ]);

        Assert.Equal(["Auth", "a.cs", "b.cs"], snapshot.Children(root.Id).Select(n => n.Title));
    }

    [Fact]
    public void Children_include_what_a_component_groups_as_well_as_what_a_folder_holds()
    {
        // The two mean different things - one is disk, the other is architecture - but a reader
        // drilling into a node is asking the same question of both.
        var component = GraphSample.Node("Authentication", GraphNodeKind.Component);
        var service = GraphSample.Node("AuthService", GraphNodeKind.Service, title: "AuthService");
        var unrelated = GraphSample.Node("Logging", GraphNodeKind.Component, title: "Logging");

        var snapshot = GraphSample.Snapshot(
            [component, service, unrelated],
            [
                GraphSample.Edge(component, service, GraphEdgeKind.Groups),
                GraphSample.Edge(component, unrelated, GraphEdgeKind.RelatesTo),
            ]);

        Assert.Equal(["AuthService"], snapshot.Children(component.Id).Select(n => n.Title));
    }

    [Fact]
    public void Roots_are_what_nothing_contains_and_exclude_what_was_archived()
    {
        var root = GraphSample.Node("src", GraphNodeKind.Folder, title: "src");
        var child = GraphSample.Node("src/a.cs", title: "a.cs");
        var grouped = GraphSample.Node("AuthService", GraphNodeKind.Service, title: "AuthService");
        var component = GraphSample.Node("Authentication", GraphNodeKind.Component, title: "Authentication");
        var archived = GraphSample.Node("old.cs", status: GraphNodeStatus.Archived, title: "old.cs");

        var snapshot = GraphSample.Snapshot(
            [root, child, grouped, component, archived],
            [
                GraphSample.Edge(root, child),

                // Grouping is not containment: a service a component gathers still stands on its own,
                // because it lives somewhere else entirely.
                GraphSample.Edge(component, grouped, GraphEdgeKind.Groups),
            ]);

        Assert.Equal(
            ["Authentication", "AuthService", "src"],
            snapshot.Roots().Select(n => n.Title));
    }

    [Fact]
    public void A_parent_is_what_contains_a_node_and_not_what_merely_groups_it()
    {
        var folder = GraphSample.Node("src", GraphNodeKind.Folder);
        var component = GraphSample.Node("Authentication", GraphNodeKind.Component);
        var file = GraphSample.Node("src/AuthService.cs");
        var loose = GraphSample.Node("AuthService", GraphNodeKind.Service);

        var snapshot = GraphSample.Snapshot(
            [folder, component, file, loose],
            [
                GraphSample.Edge(folder, file),
                GraphSample.Edge(component, loose, GraphEdgeKind.Groups),
            ]);

        Assert.Equal(folder.Id, snapshot.Parent(file.Id)?.Id);
        Assert.Null(snapshot.Parent(loose.Id));
        Assert.Null(snapshot.Parent(folder.Id));
    }

    [Fact]
    public void Nodes_can_be_taken_by_kind()
    {
        var snapshot = GraphSample.Snapshot(
        [
            GraphSample.Node("src", GraphNodeKind.Folder),
            GraphSample.Node("src/a.cs"),
            GraphSample.Node("src/b.cs"),
        ]);

        Assert.Equal(2, snapshot.OfKind(GraphNodeKind.File).Count());
        Assert.Single(snapshot.OfKind(GraphNodeKind.Folder));
        Assert.Empty(snapshot.OfKind(GraphNodeKind.Class));
    }

    [Fact]
    public void An_empty_graph_answers_every_question_without_being_asked_twice()
    {
        // What the application looks at before a folder is opened, which is a state the canvas has to
        // render rather than crash on.
        Assert.Equal(0, GraphSnapshot.Empty.NodeCount);
        Assert.Empty(GraphSnapshot.Empty.Roots());
        Assert.Empty(GraphSnapshot.Empty.Outgoing(Guid.CreateVersion7()));
        Assert.Null(GraphSnapshot.Empty.FindByKey(GraphNodeKind.File, "src/a.cs"));
        Assert.False(GraphSnapshot.Empty.TryGetNode(Guid.CreateVersion7(), out _));
    }
}
