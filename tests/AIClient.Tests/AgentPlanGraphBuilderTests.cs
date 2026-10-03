using AIClient.Application.DTOs;
using AIClient.Application.Graph;
using AIClient.Domain.Graph;

namespace AIClient.Tests;

/// <summary>
/// The geometry of a drawn plan: what nodes and edges a plan becomes, and where they land relative to
/// whatever is already on the canvas.
/// </summary>
/// <remarks>
/// This is the one part of the AI-to-canvas pipeline worth testing on its own. The sink around it only
/// applies the set and persists it, and the mutator it hands the set to is tested elsewhere; the shape
/// of the picture - a plan node, a node per part, containment and dependency edges, fanned into a
/// readable column beside the existing content - lives here, behind a static call that needs no graph
/// service, workspace or UI thread.
/// </remarks>
public sealed class AgentPlanGraphBuilderTests
{
    [Fact]
    public void A_plan_becomes_a_plan_node_a_node_per_part_and_the_edges_between_them()
    {
        var set = AgentPlanGraphBuilder.Build(Sample, GraphSnapshot.Empty);

        var nodes = Nodes(set);
        var edges = Edges(set);

        // One plan node, plus one node for each of the two parts.
        Assert.Equal(3, nodes.Count);
        var plan = Assert.Single(nodes, n => n.Kind == GraphNodeKind.Plan);
        Assert.Equal("Add authentication", plan.Title);
        Assert.Equal("2 parts", plan.Subtitle);
        Assert.Equal("Let a returning user stay signed in.", plan.Detail);

        var auth = Assert.Single(nodes, n => n.Title == "AuthService");
        var store = Assert.Single(nodes, n => n.Title == "TokenStore");

        // A part carries its path and purpose so the node is self-describing on the canvas.
        Assert.Equal("src/Auth/AuthService.cs", auth.Path);
        Assert.Equal("src/Auth/AuthService.cs", auth.Subtitle);
        Assert.Equal("Issues and validates tokens", auth.Detail);

        // A "Plans" edge runs from the plan to every part; a "Depends" edge runs between the parts that
        // named each other. AuthService depends on TokenStore.
        Assert.Equal(2, edges.Count(e => e.Kind == GraphEdgeKind.Plans && e.SourceId == plan.Id));

        var depends = Assert.Single(edges, e => e.Kind == GraphEdgeKind.Depends);
        Assert.Equal(auth.Id, depends.SourceId);
        Assert.Equal(store.Id, depends.TargetId);
    }

    [Fact]
    public void The_change_set_is_marked_as_the_agents_doing()
    {
        var set = AgentPlanGraphBuilder.Build(Sample, GraphSnapshot.Empty);

        Assert.Equal(GraphChangeOrigin.Agent, set.Origin);
        Assert.Equal("Agent plan: Add authentication", set.Title);
        Assert.Equal("Let a returning user stay signed in.", set.Description);
    }

    [Fact]
    public void A_plan_with_no_parts_draws_a_lone_plan_node()
    {
        var set = AgentPlanGraphBuilder.Build(new AgentPlan { Title = "Just a heading" }, GraphSnapshot.Empty);

        var node = Assert.Single(Nodes(set));
        Assert.Equal(GraphNodeKind.Plan, node.Kind);
        Assert.Null(node.Subtitle); // no "0 parts"
        Assert.Empty(Edges(set));
    }

    [Fact]
    public void Part_kinds_map_to_node_kinds_and_the_unrecognised_becomes_a_note()
    {
        var set = AgentPlanGraphBuilder.Build(
            new AgentPlan
            {
                Title = "Kinds",
                Parts =
                [
                    new AgentPlanPart { Name = "Svc", Kind = AgentPlanPartKind.Service },
                    new AgentPlanPart { Name = "Ui", Kind = AgentPlanPartKind.View },
                    new AgentPlanPart { Name = "Huh", Kind = AgentPlanPartKind.Other },
                ],
            },
            GraphSnapshot.Empty);

        var nodes = Nodes(set);

        Assert.Equal(GraphNodeKind.Service, Assert.Single(nodes, n => n.Title == "Svc").Kind);
        Assert.Equal(GraphNodeKind.View, Assert.Single(nodes, n => n.Title == "Ui").Kind);
        // Other has no matching node kind, so it draws as a plain note rather than an invented shape.
        Assert.Equal(GraphNodeKind.Note, Assert.Single(nodes, n => n.Title == "Huh").Kind);
    }

    [Fact]
    public void The_subgraph_lands_to_the_right_of_whatever_is_already_drawn()
    {
        // A node 200 wide centred at x=100 has its right edge at 200; the plan anchors 360 past that.
        var current = new GraphSnapshot
        {
            Nodes = [new GraphNode { Id = "existing", Title = "Existing", X = 100, Y = 50, Width = 200, Height = 64 }],
        };

        var set = AgentPlanGraphBuilder.Build(
            new AgentPlan { Title = "Beside it", Parts = [new AgentPlanPart { Name = "Only" }] },
            current);

        var nodes = Nodes(set);
        var plan = Assert.Single(nodes, n => n.Kind == GraphNodeKind.Plan);
        var part = Assert.Single(nodes, n => n.Title == "Only");

        // Anchored right of the existing content (200 + 360) and level with its vertical middle.
        Assert.Equal(560d, plan.X);
        Assert.Equal(50d, plan.Y);

        // The part sits in a column 320 to the right of the plan, centred on it (one part -> same Y).
        Assert.Equal(880d, part.X);
        Assert.Equal(50d, part.Y);
    }

    [Fact]
    public void Parts_are_fanned_into_a_column_centred_on_the_plan()
    {
        var set = AgentPlanGraphBuilder.Build(
            new AgentPlan
            {
                Title = "Column",
                Parts = [new AgentPlanPart { Name = "Top" }, new AgentPlanPart { Name = "Bottom" }],
            },
            GraphSnapshot.Empty);

        var nodes = Nodes(set);
        var plan = Assert.Single(nodes, n => n.Kind == GraphNodeKind.Plan);
        var top = Assert.Single(nodes, n => n.Title == "Top");
        var bottom = Assert.Single(nodes, n => n.Title == "Bottom");

        // Two parts straddle the plan's Y by half the row spacing (92) each: -46 and +46 around y=0.
        Assert.Equal(plan.X + 320d, top.X);
        Assert.Equal(top.X, bottom.X);
        Assert.Equal(-46d, top.Y);
        Assert.Equal(46d, bottom.Y);
    }

    [Fact]
    public void A_dependency_that_names_nothing_draws_no_edge()
    {
        var set = AgentPlanGraphBuilder.Build(
            new AgentPlan
            {
                Title = "Dangling",
                Parts = [new AgentPlanPart { Name = "Real", DependsOn = ["Ghost"] }],
            },
            GraphSnapshot.Empty);

        // The plan keeps the name on the part, but a dependency naming a part that is not here draws no
        // dangling edge - only the one "Plans" edge from the plan to the part remains.
        Assert.DoesNotContain(Edges(set), e => e.Kind == GraphEdgeKind.Depends);
        Assert.Single(Edges(set), e => e.Kind == GraphEdgeKind.Plans);
    }

    [Fact]
    public void A_part_that_depends_on_itself_draws_no_edge()
    {
        var set = AgentPlanGraphBuilder.Build(
            new AgentPlan
            {
                Title = "Loop",
                Parts = [new AgentPlanPart { Name = "Solo", DependsOn = ["solo"] }],
            },
            GraphSnapshot.Empty);

        // Matched without regard to case, and a self-loop the mutator would reject is never emitted.
        Assert.DoesNotContain(Edges(set), e => e.Kind == GraphEdgeKind.Depends);
    }

    #region Harness

    private static IReadOnlyList<GraphNode> Nodes(GraphChangeSet set) =>
        set.Changes.OfType<AddNode>().Select(c => c.Node).ToList();

    private static IReadOnlyList<GraphEdge> Edges(GraphChangeSet set) =>
        set.Changes.OfType<AddEdge>().Select(c => c.Edge).ToList();

    /// <summary>The same shape the tool tests use, built as objects rather than parsed from JSON.</summary>
    private static AgentPlan Sample => new()
    {
        Title = "Add authentication",
        Goal = "Let a returning user stay signed in.",
        Parts =
        [
            new AgentPlanPart
            {
                Name = "AuthService",
                Kind = AgentPlanPartKind.Service,
                Path = "src/Auth/AuthService.cs",
                Purpose = "Issues and validates tokens",
                DependsOn = ["TokenStore"],
            },
            new AgentPlanPart { Name = "TokenStore", Kind = AgentPlanPartKind.Data },
        ],
    };

    #endregion
}
