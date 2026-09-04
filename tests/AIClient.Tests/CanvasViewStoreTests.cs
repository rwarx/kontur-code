using AIClient.Application.DTOs;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using AIClient.Infrastructure.Graph;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// Where the canvas keeps positions and the camera, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Every claim about persistence is read back through a second store over the same file. "The
/// placement is in the list I just saved" is worthless; "the card is where I left it after a restart"
/// is the whole feature, and only the second one is being asserted here.
/// </para>
/// <para>
/// Positions are saved for nodes that really are in the graph, because that is the only kind of
/// placement the application can produce - the surface places what the snapshot gave it - and
/// because the schema enforces it. A test that positioned a card on an invented id would be
/// asserting against a database the product never has.
/// </para>
/// </remarks>
public sealed class CanvasViewStoreTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private GraphService _graph = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _graph = _db.Graph();

        await _graph.LoadAsync();
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task A_first_launch_gets_a_view_rather_than_nothing()
    {
        // Returning null on an empty database would push "no view yet" into every caller before it
        // could draw a single card, and the canvas has to render something on the first frame.
        var view = await _db.Canvas().GetDefaultAsync(Token);

        Assert.NotEqual(Guid.Empty, view.Id);
        Assert.Empty(view.Placements);
        Assert.Empty(view.Areas);
        Assert.Equal(CanvasViewport.Default, view.Viewport);
        Assert.Equal(CanvasLayoutMode.Tree, view.LayoutMode);
    }

    [Fact]
    public async Task The_default_view_is_created_once_and_then_found_again()
    {
        // A new view id on every launch would orphan every position saved before it, so this is the
        // difference between remembering a surface and quietly forgetting it each restart.
        var first = await _db.Canvas().GetDefaultAsync(Token);
        var second = await _db.Canvas().GetDefaultAsync(Token);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task A_dragged_card_is_where_it_was_left_after_a_restart()
    {
        // Section 15 of the brief, end to end and without a window: move a node, restart, the
        // position remains.
        var node = await NodeAsync("src/Program.cs");
        var view = await _db.Canvas().GetDefaultAsync(Token);

        await _db.Canvas().SavePlacementsAsync(
            view.Id,
            [CanvasPlacement.At(node, 412.5, -180.25) with { IsPinned = true, Accent = "amber" }],
            Token);

        var reopened = await _db.Canvas().GetDefaultAsync(Token);
        var placement = Assert.Single(reopened.Placements);

        Assert.Equal(node, placement.NodeId);
        Assert.Equal(412.5, placement.X);
        Assert.Equal(-180.25, placement.Y);
        Assert.True(placement.IsPinned);
        Assert.Equal("amber", placement.Accent);
    }

    [Fact]
    public async Task Saving_the_same_node_twice_moves_it_rather_than_duplicating_it()
    {
        // Identity is the pair, not a row id. Two placements for one node would draw the card twice
        // and let a click select a ghost.
        var node = await NodeAsync("src/Program.cs");
        var view = await _db.Canvas().GetDefaultAsync(Token);

        await _db.Canvas().SavePlacementsAsync(view.Id, [CanvasPlacement.At(node, 10, 10)], Token);
        await _db.Canvas().SavePlacementsAsync(view.Id, [CanvasPlacement.At(node, 640, 480)], Token);

        var reopened = await _db.Canvas().GetDefaultAsync(Token);
        var placement = Assert.Single(reopened.Placements);

        Assert.Equal(640, placement.X);
        Assert.Equal(480, placement.Y);
    }

    [Fact]
    public async Task Saving_one_card_leaves_the_rest_of_the_surface_alone()
    {
        // A drag saves the node that moved. Rewriting the whole view on every drag would make the
        // cost of moving a card scale with the size of the project.
        var moved = await NodeAsync("src/Moved.cs");
        var untouched = await NodeAsync("src/Untouched.cs");
        var view = await _db.Canvas().GetDefaultAsync(Token);

        await _db.Canvas().SavePlacementsAsync(
            view.Id,
            [CanvasPlacement.At(moved, 0, 0), CanvasPlacement.At(untouched, 300, 300)],
            Token);

        await _db.Canvas().SavePlacementsAsync(view.Id, [CanvasPlacement.At(moved, 55, 66)], Token);

        var reopened = await _db.Canvas().GetDefaultAsync(Token);
        var byNode = reopened.Placements.ToDictionary(p => p.NodeId);

        Assert.Equal(2, byNode.Count);
        Assert.Equal((55, 66), (byNode[moved].X, byNode[moved].Y));
        Assert.Equal((300, 300), (byNode[untouched].X, byNode[untouched].Y));
    }

    [Fact]
    public async Task The_camera_is_where_it_was_left_after_a_restart()
    {
        var view = await _db.Canvas().GetDefaultAsync(Token);

        await _db.Canvas().SaveViewportAsync(view.Id, new CanvasViewport(-1240.5, 96.25, 0.625), Token);

        var reopened = await _db.Canvas().GetDefaultAsync(Token);

        Assert.Equal(new CanvasViewport(-1240.5, 96.25, 0.625), reopened.Viewport);
    }

    [Fact]
    public async Task A_camera_saved_out_of_range_comes_back_usable()
    {
        // Nothing in the application writes this, but a hand-edited or half-written row would, and a
        // zoom of zero is a surface with no way out of it.
        var view = await _db.Canvas().GetDefaultAsync(Token);

        await _db.Canvas().SaveViewportAsync(view.Id, new CanvasViewport(0, 0, 5000), Token);

        var reopened = await _db.Canvas().GetDefaultAsync(Token);

        Assert.InRange(reopened.Viewport.Zoom, CanvasViewport.MinZoom, CanvasViewport.MaxZoom);
    }

    [Fact]
    public async Task Saving_no_placements_at_all_is_not_an_error()
    {
        // The canvas calls this whenever a drag ends, including the drag that changed nothing.
        var view = await _db.Canvas().GetDefaultAsync(Token);

        await _db.Canvas().SavePlacementsAsync(view.Id, [], Token);

        Assert.Empty((await _db.Canvas().GetDefaultAsync(Token)).Placements);
    }

    [Fact]
    public async Task Deleting_a_node_takes_its_position_with_it()
    {
        // The dependency runs one way, and the schema is where that is enforced. Geometry for a node
        // nobody can name again is not a fact worth keeping: it cannot be drawn, cannot be selected,
        // and would accumulate for the lifetime of the project.
        var node = await NodeAsync("src/Doomed.cs");
        var view = await _db.Canvas().GetDefaultAsync(Token);

        await _db.Canvas().SavePlacementsAsync(view.Id, [CanvasPlacement.At(node, 24, 48)], Token);

        Assert.Single((await _db.Canvas().GetDefaultAsync(Token)).Placements);

        var removed = await _graph.ApplyAsync(
            GraphChangeSet.Create("Forget the file", GraphOrigin.User, [new GraphMutation.RemoveNode(node)]),
            Token);

        Assert.True(removed.Success, removed.Error);
        Assert.Empty((await _db.Canvas().GetDefaultAsync(Token)).Placements);
    }

    [Fact]
    public async Task A_card_whose_node_has_gone_is_dropped_and_the_others_are_still_saved()
    {
        // The drag that lands just as a change set removes the node under it. One batch, so letting
        // the constraint throw would take every other card's position with it - and this runs at the
        // end of a gesture, where an exception reaches the user as a crash rather than a message.
        var live = await NodeAsync("src/Live.cs");
        var view = await _db.Canvas().GetDefaultAsync(Token);

        await _db.Canvas().SavePlacementsAsync(
            view.Id,
            [CanvasPlacement.At(live, 12, 34), CanvasPlacement.At(Guid.CreateVersion7(), 99, 99)],
            Token);

        var placement = Assert.Single((await _db.Canvas().GetDefaultAsync(Token)).Placements);

        Assert.Equal(live, placement.NodeId);
        Assert.Equal((12, 34), (placement.X, placement.Y));
    }

    /// <summary>Shorthand for the token every test here passes to every call.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// One real node in the graph, and its id.
    /// </summary>
    /// <remarks>
    /// Through the graph's own write path rather than by inserting a row, so that the placements
    /// below are attached to something the application could actually have drawn.
    /// </remarks>
    private async Task<Guid> NodeAsync(string key)
    {
        var id = Guid.CreateVersion7();

        var applied = await _graph.ApplyAsync(
            GraphChangeSet.Create(
                $"Add {key}",
                GraphOrigin.User,
                [
                    new GraphMutation.AddNode(new GraphNode
                    {
                        Id = id,
                        Kind = GraphNodeKind.File,
                        Key = key,
                        Title = Path.GetFileName(key),
                    }),
                ]),
            Token);

        Assert.True(applied.Success, applied.Error);

        return id;
    }
}
