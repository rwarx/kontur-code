using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Entities;
using AIClient.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Graph;

/// <summary>
/// The spatial state of the Canvas, in three tables of its own.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the graph, this is read on demand and never cached: a view is opened once per session and
/// its placements are handed to the surface, which then owns them. Writes are frequent and tiny -
/// one card after a drag - so they go straight to the row and nothing is published to anyone.
/// </para>
/// <para>
/// The tables carry no semantics whatsoever, which is checked by a test that deletes every row in
/// all three and asserts the graph is intact. That test is the reason this class exists separately
/// rather than as a few columns on <c>GraphNodes</c>.
/// </para>
/// </remarks>
public sealed class CanvasViewStore : ICanvasViewStore
{
    /// <summary>Name of the view a user never asked for, which is the one they always get.</summary>
    private const string DefaultViewName = "Project";

    private readonly IDbContextFactory<AIClientDbContext> _contextFactory;
    private readonly ILogger<CanvasViewStore> _logger;

    public CanvasViewStore(
        IDbContextFactory<AIClientDbContext> contextFactory,
        ILogger<CanvasViewStore> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<CanvasViewState> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // Ordered so that a database which somehow holds two default views resolves the same way
        // every time rather than alternating between them.
        var view = await db.CanvasViews
            .AsNoTracking()
            .Where(row => row.IsDefault)
            .OrderBy(row => row.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (view is null)
        {
            view = new CanvasViewRow
            {
                Id = Guid.CreateVersion7(),
                Name = DefaultViewName,
                IsDefault = true,
            };

            db.CanvasViews.Add(view);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Created the default canvas view.");

            return Map(view, [], []);
        }

        var placements = await db.CanvasPlacements
            .AsNoTracking()
            .Where(row => row.ViewId == view.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var areas = await db.CanvasAreas
            .AsNoTracking()
            .Where(row => row.ViewId == view.Id)
            .OrderBy(row => row.Order)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Map(view, placements, areas);
    }

    public async Task SavePlacementsAsync(
        Guid viewId,
        IEnumerable<CanvasPlacement> placements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placements);

        var incoming = placements as IReadOnlyList<CanvasPlacement> ?? [.. placements];

        if (incoming.Count == 0)
        {
            return;
        }

        await using var db = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var nodeIds = incoming.Select(placement => placement.NodeId).ToHashSet();

        var existing = await db.CanvasPlacements
            .Where(row => row.ViewId == viewId && nodeIds.Contains(row.NodeId))
            .ToDictionaryAsync(row => row.NodeId, cancellationToken)
            .ConfigureAwait(false);

        // A placement is geometry for a node, and the schema says so: the row cannot outlive the
        // node it positions. A change set can remove a node while a drag is still in flight, so the
        // ids are checked here rather than left to the constraint - this is one batch, and losing
        // every other card's position over one card that went is not a trade worth making.
        var live = await db.GraphNodes
            .AsNoTracking()
            .Where(row => nodeIds.Contains(row.Id))
            .Select(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var known = live.ToHashSet();
        var now = DateTimeOffset.UtcNow;
        var vanished = 0;

        foreach (var placement in incoming)
        {
            if (!known.Contains(placement.NodeId))
            {
                vanished++;
                continue;
            }

            if (existing.TryGetValue(placement.NodeId, out var row))
            {
                row.X = placement.X;
                row.Y = placement.Y;
                row.Width = placement.Width;
                row.Height = placement.Height;
                row.IsCollapsed = placement.IsCollapsed;
                row.Accent = placement.Accent;
                row.IsPinned = placement.IsPinned;
                row.UpdatedAt = now;
                continue;
            }

            db.CanvasPlacements.Add(new CanvasPlacementRow
            {
                Id = Guid.CreateVersion7(),
                ViewId = viewId,
                NodeId = placement.NodeId,
                X = placement.X,
                Y = placement.Y,
                Width = placement.Width,
                Height = placement.Height,
                IsCollapsed = placement.IsCollapsed,
                Accent = placement.Accent,
                IsPinned = placement.IsPinned,
                UpdatedAt = now,
            });
        }

        if (vanished > 0)
        {
            _logger.LogDebug(
                "Skipped {Count} placement(s) whose node is no longer in the graph.",
                vanished);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveViewportAsync(
        Guid viewId,
        CanvasViewport viewport,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var view = await db.CanvasViews
            .FirstOrDefaultAsync(row => row.Id == viewId, cancellationToken)
            .ConfigureAwait(false);

        if (view is null)
        {
            // The view was deleted while the surface was open. Losing a camera position is not
            // worth an exception on a path that runs when a window closes.
            return;
        }

        var camera = viewport.Normalized();

        view.PanX = camera.PanX;
        view.PanY = camera.PanY;
        view.Zoom = camera.Zoom;
        view.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CanvasViewState Map(
        CanvasViewRow view,
        IReadOnlyList<CanvasPlacementRow> placements,
        IReadOnlyList<CanvasAreaRow> areas) => new()
        {
            Id = view.Id,
            Name = view.Name,
            RootNodeId = view.RootNodeId,
            Depth = view.Depth,
            LayoutMode = view.LayoutMode,
            Viewport = new CanvasViewport(view.PanX, view.PanY, view.Zoom).Normalized(),
            Placements =
            [
                .. placements.Select(row => new CanvasPlacement
                {
                    NodeId = row.NodeId,
                    X = row.X,
                    Y = row.Y,
                    Width = row.Width,
                    Height = row.Height,
                    IsCollapsed = row.IsCollapsed,
                    Accent = row.Accent,
                    IsPinned = row.IsPinned,
                }),
            ],
            Areas =
            [
                .. areas.Select(row => new CanvasArea
                {
                    Id = row.Id,
                    Title = row.Title,
                    GroupNodeId = row.GroupNodeId,
                    X = row.X,
                    Y = row.Y,
                    Width = row.Width,
                    Height = row.Height,
                    Accent = row.Accent,
                    Order = row.Order,
                }),
            ],
        };
}
