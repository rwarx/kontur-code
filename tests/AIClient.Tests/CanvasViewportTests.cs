using AIClient.Application.DTOs;

namespace AIClient.Tests;

/// <summary>
/// The camera arithmetic behind pan, wheel zoom and "fit to content".
/// </summary>
/// <remarks>
/// These are the two calculations on the canvas that are easy to get subtly wrong and impossible to
/// argue about afterwards: zooming about the cursor and fitting content to a surface. They live in
/// <see cref="CanvasViewport"/> rather than in the view precisely so that this file can hold them to
/// account without a window, a dispatcher or a rendered frame.
/// </remarks>
public sealed class CanvasViewportTests
{
    /// <summary>How close two pixel positions have to be before the difference is not real.</summary>
    private const double Tolerance = 1e-9;

    [Fact]
    public void The_point_under_the_cursor_does_not_move_while_zooming()
    {
        // The whole feel of a wheel zoom. Scaling about the origin instead would send whatever the
        // user was pointing at off screen, and this is the assertion that says so.
        var camera = new CanvasViewport(-140, 60, 0.8);
        var worldX = camera.ToWorldX(500);
        var worldY = camera.ToWorldY(320);

        var zoomed = camera.ZoomedAt(1.25, 500, 320);

        Assert.Equal(500, zoomed.ToScreenX(worldX), Tolerance);
        Assert.Equal(320, zoomed.ToScreenY(worldY), Tolerance);
        Assert.Equal(1.0, zoomed.Zoom, Tolerance);
    }

    [Fact]
    public void Zoom_stops_at_both_ends_of_its_range()
    {
        var far = new CanvasViewport(0, 0, CanvasViewport.MinZoom).ZoomedAt(0.1, 400, 300);
        var near = new CanvasViewport(0, 0, CanvasViewport.MaxZoom).ZoomedAt(10, 400, 300);

        Assert.Equal(CanvasViewport.MinZoom, far.Zoom);
        Assert.Equal(CanvasViewport.MaxZoom, near.Zoom);
    }

    [Fact]
    public void A_zoom_that_would_change_nothing_returns_the_camera_untouched()
    {
        // Not an optimisation: at the limit the fixed-point arithmetic would still rewrite the pan,
        // so a user holding the wheel at maximum zoom would watch the graph drift.
        var camera = new CanvasViewport(-90, 210, CanvasViewport.MaxZoom);

        Assert.Equal(camera, camera.ZoomedAt(2, 640, 400));
        Assert.Equal(camera, camera.ZoomedAt(1, 640, 400));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-2)]
    public void Nonsense_zoom_factors_are_ignored_rather_than_refused(double factor)
    {
        // A camera is driven by a mouse wheel and by a restored database row. Throwing here would
        // turn one bad row into a surface the user cannot recover, so the input is dropped instead.
        var camera = new CanvasViewport(12, -34, 1.5);

        Assert.Equal(camera, camera.ZoomedAt(factor, 100, 100));
    }

    [Fact]
    public void A_camera_restored_from_a_damaged_row_still_makes_sense()
    {
        var camera = new CanvasViewport(double.NaN, double.PositiveInfinity, 900).Normalized();

        Assert.Equal(0, camera.PanX);
        Assert.Equal(0, camera.PanY);
        Assert.Equal(CanvasViewport.MaxZoom, camera.Zoom);
    }

    [Fact]
    public void Screen_and_world_coordinates_round_trip()
    {
        var camera = new CanvasViewport(37, -128, 0.65);

        Assert.Equal(410, camera.ToWorldX(camera.ToScreenX(410)), Tolerance);
        Assert.Equal(-72, camera.ToWorldY(camera.ToScreenY(-72)), Tolerance);
    }

    [Fact]
    public void A_camera_with_no_scale_maps_coordinates_straight_through()
    {
        // Only reachable through a corrupted row, and the answer has to be finite rather than right:
        // dividing by the zoom here would put every node at infinity and hang the layout pass.
        var broken = new CanvasViewport(50, 50, 0);

        Assert.Equal(200, broken.ToWorldX(200));
        Assert.Equal(-40, broken.ToWorldY(-40));
    }

    [Fact]
    public void Fitting_content_centres_it_on_the_surface()
    {
        var content = new CanvasBounds(1000, 500, 400, 200);

        var camera = CanvasViewport.Fit(content, 1600, 900);
        var padded = content.Inflate(CanvasMetrics.FitPadding);

        Assert.Equal(800, camera.ToScreenX(padded.CenterX), Tolerance);
        Assert.Equal(450, camera.ToScreenY(padded.CenterY), Tolerance);
    }

    [Fact]
    public void Fitting_never_magnifies_past_one_to_one()
    {
        // Three cards blown up to fill a window read as a rendering fault, not as a graph. Filling
        // the space is the layout's job; the camera only ever shrinks to fit.
        var camera = CanvasViewport.Fit(new CanvasBounds(0, 0, 40, 20), 1600, 900);

        Assert.Equal(1.0, camera.Zoom);
    }

    [Fact]
    public void Fitting_a_graph_larger_than_the_window_brings_all_of_it_into_view()
    {
        var content = new CanvasBounds(-2000, -1200, 6000, 3600);

        var camera = CanvasViewport.Fit(content, 1200, 800);
        var visible = camera.VisibleWorld(1200, 800);

        Assert.True(camera.Zoom < 1);
        Assert.True(visible.Contains(content.Left, content.Top));
        Assert.True(visible.Contains(content.Right, content.Bottom));
    }

    [Fact]
    public void Fitting_nothing_returns_the_camera_the_canvas_opens_with()
    {
        // The empty state and the un-indexed state both go through here, and neither has content to
        // aim at. A default camera is what makes the first frame identical to a fresh install.
        Assert.Equal(CanvasViewport.Default, CanvasViewport.Fit(CanvasBounds.Empty, 1200, 800));
        Assert.Equal(CanvasViewport.Default, CanvasViewport.Fit(new CanvasBounds(0, 0, 10, 10), 0, 800));
    }

    [Fact]
    public void Centering_a_selection_leaves_the_scale_alone()
    {
        // "Zoom to selection" and "bring the selection into view" are different gestures. This one
        // must not change how big the cards are, or every use of it costs the user their zoom level.
        var camera = new CanvasViewport(0, 0, 1.75);

        var centred = camera.Centered(new CanvasBounds(400, 400, 200, 100), 1000, 600);

        Assert.Equal(1.75, centred.Zoom);
        Assert.Equal(500, centred.ToScreenX(500), Tolerance);
        Assert.Equal(300, centred.ToScreenY(450), Tolerance);
    }

    [Fact]
    public void The_visible_region_shrinks_as_the_camera_moves_in()
    {
        var far = new CanvasViewport(0, 0, 0.5).VisibleWorld(1000, 800);
        var near = new CanvasViewport(0, 0, 2).VisibleWorld(1000, 800);

        Assert.Equal(2000, far.Width, Tolerance);
        Assert.Equal(500, near.Width, Tolerance);
    }

    [Fact]
    public void A_surface_with_no_size_shows_nothing()
    {
        // The first layout pass runs before WPF has measured anything. Virtualisation asks what is
        // visible on that pass, and an empty answer is the only honest one.
        Assert.True(CanvasViewport.Default.VisibleWorld(0, 0).IsEmpty);
    }
}
