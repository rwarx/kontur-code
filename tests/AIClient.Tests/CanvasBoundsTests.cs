using AIClient.Application.DTOs;

namespace AIClient.Tests;

/// <summary>
/// The rectangle the canvas measures everything with.
/// </summary>
/// <remarks>
/// Two distinctions are load-bearing and neither is obvious from the type: an empty rectangle is not
/// a zero-sized one at the origin, and a rectangle drawn by a pointer arrives with its corners in
/// whatever order the drag happened to take.
/// </remarks>
public sealed class CanvasBoundsTests
{
    [Fact]
    public void No_content_is_not_the_same_as_a_point_at_the_origin()
    {
        // "Fit to content" answers these two differently, and a graph whose only node sits at (0,0)
        // is a real graph. Collapsing them would make that case open on the empty-state camera.
        Assert.True(CanvasBounds.Empty.IsEmpty);
        Assert.False(new CanvasBounds(0, 0, 0, 0).IsEmpty);
    }

    [Fact]
    public void Spanning_a_set_of_rectangles_ignores_the_empty_ones()
    {
        var around = CanvasBounds.Around(
        [
            CanvasBounds.Empty,
            new CanvasBounds(100, 100, 50, 50),
            CanvasBounds.Empty,
            new CanvasBounds(300, 0, 20, 20),
        ]);

        Assert.Equal(new CanvasBounds(100, 0, 220, 150), around);
        Assert.True(CanvasBounds.Around([]).IsEmpty);
    }

    [Fact]
    public void A_rectangle_dragged_upwards_and_to_the_left_is_still_a_rectangle()
    {
        // The selection lasso, whose corners arrive in the order the user dragged them.
        var forwards = CanvasBounds.Between(10, 20, 110, 220);
        var backwards = CanvasBounds.Between(110, 220, 10, 20);

        Assert.Equal(forwards, backwards);
        Assert.Equal(new CanvasBounds(10, 20, 100, 200), forwards);
    }

    [Fact]
    public void Rectangles_that_only_touch_count_as_overlapping()
    {
        // Virtualisation culls by this test. Treating a shared edge as a miss would blank the row of
        // cards exactly at the edge of the viewport as the user pans.
        var left = new CanvasBounds(0, 0, 100, 100);

        Assert.True(left.Intersects(new CanvasBounds(100, 0, 100, 100)));
        Assert.False(left.Intersects(new CanvasBounds(100.5, 0, 100, 100)));
        Assert.False(left.Intersects(CanvasBounds.Empty));
    }

    [Fact]
    public void Nothing_is_inside_a_rectangle_that_has_no_content()
    {
        Assert.False(CanvasBounds.Empty.Contains(0, 0));
        Assert.True(new CanvasBounds(0, 0, 10, 10).Contains(10, 10));
    }
}
