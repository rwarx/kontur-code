using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Services;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// The arithmetic that turns containment into coordinates.
/// </summary>
/// <remarks>
/// Two properties matter more than any particular position. The layout has to be the same on every
/// run, or a user cannot build a spatial memory of their own project; and it has to leave organised
/// surfaces alone, or the cost of indexing again is losing an afternoon of arranging. Everything else
/// here is a check that the picture matches the relation it claims to draw.
/// </remarks>
public sealed class CanvasLayoutTests
{
    /// <summary>The horizontal distance between one level of nesting and the next.</summary>
    private const double Column = CanvasMetrics.NodeWidth + CanvasMetrics.LevelGap;

    [Fact]
    public void The_same_graph_lays_out_the_same_way_twice()
    {
        // Deterministic by construction, and asserted because the alternative is a project that
        // opens differently every morning. Built from two independently shuffled snapshots so the
        // dictionary iteration order the graph happens to have cannot leak into the answer.
        var project = GraphSample.Node(".", GraphNodeKind.Project, title: "Project");
        var src = GraphSample.Node("src", GraphNodeKind.Folder, title: "src");
        var a = GraphSample.Node("src/a.cs", title: "a.cs");
        var b = GraphSample.Node("src/b.cs", title: "b.cs");

        GraphEdge[] edges =
        [
            GraphSample.Edge(project, src),
            GraphSample.Edge(src, a, order: 0),
            GraphSample.Edge(src, b, order: 1),
        ];

        var first = CanvasLayout.Arrange(GraphSample.Snapshot([project, src, a, b], edges));
        var second = CanvasLayout.Arrange(GraphSample.Snapshot([b, a, src, project], [.. edges.Reverse()]));

        Assert.Equal(
            first.OrderBy(p => p.NodeId).Select(p => (p.NodeId, p.X, p.Y)),
            second.OrderBy(p => p.NodeId).Select(p => (p.NodeId, p.X, p.Y)));
    }

    [Fact]
    public void Every_node_is_given_exactly_one_place()
    {
        // A node without a placement is a node the user can neither see nor select, so it may as
        // well not be in the graph. This includes the ones containment never reached.
        var project = GraphSample.Node(".", GraphNodeKind.Project, title: "Project");
        var file = GraphSample.Node("src/a.cs", title: "a.cs");
        var loose = GraphSample.Node("node:decision", GraphNodeKind.Decision, title: "Use SQLite");

        var graph = GraphSample.Snapshot(
            [project, file, loose],
            [GraphSample.Edge(project, file)]);

        var placements = CanvasLayout.Arrange(graph);

        Assert.Equal(3, placements.Count);
        Assert.Equal(3, placements.Select(p => p.NodeId).Distinct().Count());
        Assert.Equal(graph.Nodes.Select(n => n.Id).OrderBy(id => id), placements.Select(p => p.NodeId).OrderBy(id => id));
    }

    [Fact]
    public void Nesting_reads_left_to_right()
    {
        var project = GraphSample.Node(".", GraphNodeKind.Project, title: "Project");
        var src = GraphSample.Node("src", GraphNodeKind.Folder, title: "src");
        var file = GraphSample.Node("src/a.cs", title: "a.cs");

        var placements = CanvasLayout.Arrange(GraphSample.Snapshot(
            [project, src, file],
            [GraphSample.Edge(project, src), GraphSample.Edge(src, file)]));

        var byId = placements.ToDictionary(p => p.NodeId);

        Assert.Equal(0, byId[project.Id].X);
        Assert.Equal(Column, byId[src.Id].X);
        Assert.Equal(2 * Column, byId[file.Id].X);
    }

    [Fact]
    public void A_parent_sits_level_with_the_middle_of_its_children()
    {
        // What makes the picture read as a tree rather than as a list with indentation.
        var src = GraphSample.Node("src", GraphNodeKind.Folder, title: "src");
        var a = GraphSample.Node("src/a.cs", title: "a.cs");
        var b = GraphSample.Node("src/b.cs", title: "b.cs");
        var c = GraphSample.Node("src/c.cs", title: "c.cs");

        var placements = CanvasLayout.Arrange(GraphSample.Snapshot(
            [src, a, b, c],
            [
                GraphSample.Edge(src, a, order: 0),
                GraphSample.Edge(src, b, order: 1),
                GraphSample.Edge(src, c, order: 2),
            ]));

        var byId = placements.ToDictionary(p => p.NodeId);

        Assert.Equal(byId[b.Id].Y, byId[src.Id].Y, 1e-9);
        Assert.Equal((byId[a.Id].Y + byId[c.Id].Y) / 2, byId[src.Id].Y, 1e-9);
    }

    [Fact]
    public void No_two_cards_are_drawn_on_top_of_each_other()
    {
        // Stated as rectangles rather than as rows on purpose. A run of siblings wraps into several
        // columns, so "no two share a row" is no longer true and no longer the property that matters;
        // what matters is that every card is separately visible and separately clickable.
        var src = GraphSample.Node("src", GraphNodeKind.Folder, title: "src");
        var files = Enumerable.Range(0, 12)
            .Select(i => GraphSample.Node($"src/f{i}.cs", title: $"f{i}.cs"))
            .ToArray();

        var placements = CanvasLayout.Arrange(GraphSample.Snapshot(
            [src, .. files],
            [.. files.Select((f, i) => GraphSample.Edge(src, f, order: i))]));

        var pairs =
            from first in placements
            from second in placements
            where first.NodeId.CompareTo(second.NodeId) < 0
            select (First: first, Second: second);

        Assert.All(pairs, pair => Assert.False(
            pair.First.Bounds.Intersects(pair.Second.Bounds),
            "Two cards are drawn on top of each other."));
    }

    [Fact]
    public void A_long_run_of_siblings_wraps_instead_of_becoming_a_strip()
    {
        // The property "Fit to content" depends on. Forty files in a single column is a strip several
        // times taller than any screen, and a project of such folders is a ribbon that clamps at the
        // minimum zoom with content still running off both edges - which is not a fit at all.
        var src = GraphSample.Node("src", GraphNodeKind.Folder, title: "src");
        var files = Enumerable.Range(0, 40)
            .Select(i => GraphSample.Node($"src/f{i}.cs", title: $"f{i}.cs"))
            .ToArray();

        var placements = CanvasLayout.Arrange(GraphSample.Snapshot(
            [src, .. files],
            [.. files.Select((f, i) => GraphSample.Edge(src, f, order: i))]));

        var leaves = placements.Where(p => p.NodeId != src.Id).ToList();

        Assert.True(leaves.Select(p => p.X).Distinct().Count() > 1, "The run never wrapped.");
        Assert.True(
            leaves.Select(p => p.Y).Distinct().Count() <= CanvasMetrics.WrapColumnAt,
            $"Wrapped, but still stacked {leaves.Select(p => p.Y).Distinct().Count()} rows.");
    }

    [Fact]
    public void Auto_layout_leaves_the_nodes_the_user_pinned_where_they_are()
    {
        // "Auto Layout" is allowed to move things - that is the gesture - but not the ones somebody
        // placed deliberately. Otherwise tidying the rest of the graph costs them their own work.
        var src = GraphSample.Node("src", GraphNodeKind.Folder, title: "src");
        var a = GraphSample.Node("src/a.cs", title: "a.cs");
        var b = GraphSample.Node("src/b.cs", title: "b.cs");

        var graph = GraphSample.Snapshot(
            [src, a, b],
            [GraphSample.Edge(src, a, order: 0), GraphSample.Edge(src, b, order: 1)]);

        var pinned = CanvasPlacement.At(a.Id, -4200, 900) with { IsPinned = true };
        var dragged = CanvasPlacement.At(b.Id, 5000, 5000);

        var placements = CanvasLayout.Arrange(graph, [pinned, dragged]);
        var byId = placements.ToDictionary(p => p.NodeId);

        Assert.Equal(pinned, byId[a.Id]);
        Assert.NotEqual(5000, byId[b.Id].X);
        Assert.Equal(3, placements.Count);
    }

    [Fact]
    public void Indexing_again_only_places_what_is_new()
    {
        // The pass that runs after every re-index. Returning a position for an already-placed node
        // would move it, which is the one thing indexing must never do to a surface.
        var src = GraphSample.Node("src", GraphNodeKind.Folder, title: "src");
        var known = GraphSample.Node("src/a.cs", title: "a.cs");
        var added = GraphSample.Node("src/b.cs", title: "b.cs");

        var graph = GraphSample.Snapshot(
            [src, known, added],
            [GraphSample.Edge(src, known, order: 0), GraphSample.Edge(src, added, order: 1)]);

        var existing = new[] { CanvasPlacement.At(src.Id, 10, 10), CanvasPlacement.At(known.Id, 20, 20) };

        var placed = CanvasLayout.PlaceMissing(graph, existing);

        Assert.Equal([added.Id], placed.Select(p => p.NodeId));
    }

    [Fact]
    public void A_first_index_places_everything()
    {
        var src = GraphSample.Node("src", GraphNodeKind.Folder, title: "src");
        var file = GraphSample.Node("src/a.cs", title: "a.cs");
        var graph = GraphSample.Snapshot([src, file], [GraphSample.Edge(src, file)]);

        Assert.Equal(2, CanvasLayout.PlaceMissing(graph, []).Count);
    }

    [Fact]
    public void Nodes_no_hierarchy_reaches_are_gridded_below_the_trees()
    {
        // A cycle drawn by hand, or a decision that relates to a file without being inside anything.
        // A block underneath is honest about there being no hierarchy to show, and keeps them
        // reachable, which dropping them would not.
        var src = GraphSample.Node("src", GraphNodeKind.Folder, title: "src");
        var file = GraphSample.Node("src/a.cs", title: "a.cs");
        var left = GraphSample.Node("node:left", GraphNodeKind.Decision, title: "Left");
        var right = GraphSample.Node("node:right", GraphNodeKind.Decision, title: "Right");

        // Contained by each other, so neither is a root and the walk never reaches either.
        var graph = GraphSample.Snapshot(
            [src, file, left, right],
            [
                GraphSample.Edge(src, file),
                GraphSample.Edge(left, right),
                GraphSample.Edge(right, left),
            ]);

        var placements = CanvasLayout.Arrange(graph).ToDictionary(p => p.NodeId);
        var tree = CanvasLayout.BoundsOf([placements[src.Id], placements[file.Id]]);

        Assert.Equal(4, placements.Count);
        Assert.True(placements[left.Id].Y > tree.Bottom);
        Assert.True(placements[right.Id].Y > tree.Bottom);
    }

    [Fact]
    public void The_bounds_of_a_layout_cover_every_card_in_it()
    {
        // What "Fit to content" is handed. A rectangle that clipped one card would open the canvas
        // with a node just off screen, which reads as a missing node.
        var src = GraphSample.Node("src", GraphNodeKind.Folder, title: "src");
        var files = Enumerable.Range(0, 7)
            .Select(i => GraphSample.Node($"src/f{i}.cs", title: $"f{i}.cs"))
            .ToArray();

        var placements = CanvasLayout.Arrange(GraphSample.Snapshot(
            [src, .. files],
            [.. files.Select((f, i) => GraphSample.Edge(src, f, order: i))]));

        var bounds = CanvasLayout.BoundsOf(placements);

        Assert.All(placements, p => Assert.Equal(bounds, bounds.Union(p.Bounds)));
    }

    [Fact]
    public void An_empty_graph_produces_no_placements_and_no_rectangle()
    {
        // The state the canvas is in before a folder is indexed, and the reason the empty state is
        // drawn from the graph rather than from a flag.
        Assert.Empty(CanvasLayout.Arrange(GraphSnapshot.Empty));
        Assert.True(CanvasLayout.BoundsOf([]).IsEmpty);
    }

    [Fact]
    public void An_installation_that_has_never_recorded_a_revision_counts_as_behind()
    {
        // The comparison the canvas opens with. Stored positions outlive the arithmetic that made
        // them, and indexing deliberately never moves a card that already has a place - so if this
        // default ever caught up to the constant, every project arranged by older code would keep
        // that shape for good and nothing anywhere would say so.
        Assert.True(new CanvasSettings().LayoutRevision < CanvasLayout.Revision);
    }

    [Fact]
    public void Catching_up_an_older_surface_wraps_the_run_and_still_spares_the_pinned_cards()
    {
        // The one-time pass on a project stored as a single tall column by an older revision. Both
        // halves matter: the run has to stop being a ribbon, which is what made the canvas open at
        // six percent zoom, and the one card somebody placed by hand has to come through it
        // untouched - otherwise catching up costs the user their own arrangement.
        var src = GraphSample.Node("src", GraphNodeKind.Folder, title: "src");
        var files = Enumerable.Range(0, 20)
            .Select(i => GraphSample.Node($"src/f{i}.cs", title: $"f{i}.cs"))
            .ToArray();

        var graph = GraphSample.Snapshot(
            [src, .. files],
            [.. files.Select((f, i) => GraphSample.Edge(src, f, order: i))]);

        var pinned = CanvasPlacement.At(files[0].Id, -3000, 700) with { IsPinned = true };

        // Every other file directly under the last, in one column - the shape the old arithmetic left.
        var stale = files
            .Skip(1)
            .Select((f, i) => CanvasPlacement.At(f.Id, Column, i * (CanvasMetrics.NodeHeight + CanvasMetrics.SiblingGap)))
            .ToList();

        var placements = CanvasLayout.Arrange(graph, [pinned, .. stale]);
        var byId = placements.ToDictionary(p => p.NodeId);

        Assert.Equal(pinned, byId[files[0].Id]);

        var rearranged = files.Skip(1).Select(f => byId[f.Id]).ToList();

        Assert.True(
            rearranged.Select(p => p.X).Distinct().Count() > 1,
            "Catching up left the run as a strip.");
        Assert.Equal(files.Length + 1, placements.Count);
    }
}
