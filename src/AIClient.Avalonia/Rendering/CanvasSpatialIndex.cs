using AIClient.Application.DTOs;

namespace AIClient.Avalonia.Rendering;

/// <summary>
/// A uniform-grid spatial index over node bounds, in world coordinates.
/// </summary>
/// <remarks>
/// <para>
/// The one genuinely new piece of the canvas port, and the answer to the questions the WPF
/// canvas answered by scanning every card: which cards are near the camera (per pan frame),
/// and which cards does a marquee touch. Both become queries over the cells the rectangle
/// overlaps instead of walks over the whole dictionary, which is what keeps panning a
/// ten-thousand-card graph from being a ten-thousand-card loop sixty times a second.
/// </para>
/// <para>
/// Cell size trades index granularity against rebuild cost. 256 world units is roughly one
/// card per cell at reading zoom; the rebuild is O(n) and happens on graph sync, layout and
/// drag end - never per frame.
/// </para>
/// </remarks>
public sealed class CanvasSpatialIndex
{
    /// <summary>The world-space edge of one grid cell.</summary>
    private const int CellSize = 256;

    private readonly Dictionary<long, List<Entry>> _cells = [];
    private readonly Dictionary<Guid, CanvasBounds> _bounds = [];

    private readonly struct Entry(Guid id, CanvasBounds bounds)
    {
        public Guid Id { get; } = id;
        public CanvasBounds Bounds { get; } = bounds;
    }

    /// <summary>How many cards the index knows about.</summary>
    public int Count => _bounds.Count;

    /// <summary>Rebuilds the whole index from a set of cards.</summary>
    public void Rebuild(IEnumerable<(Guid Id, CanvasBounds Bounds)> cards)
    {
        _cells.Clear();
        _bounds.Clear();

        foreach (var (id, bounds) in cards)
        {
            Insert(id, bounds);
        }
    }

    /// <summary>Replaces the bounds of the named cards in place - what a drag end needs.</summary>
    public void Update(IEnumerable<(Guid Id, CanvasBounds Bounds)> cards)
    {
        foreach (var (id, bounds) in cards)
        {
            if (!_bounds.ContainsKey(id))
            {
                continue;
            }

            Remove(id);
            Insert(id, bounds);
        }
    }

    /// <summary>Every indexed card whose bounds touch the rectangle.</summary>
    public IEnumerable<Guid> Query(CanvasBounds area)
    {
        if (area.IsEmpty || _bounds.Count == 0)
        {
            yield break;
        }

        var (minX, minY, maxX, maxY) = Cells(area);
        var seen = new HashSet<Guid>();

        for (var cy = minY; cy <= maxY; cy++)
        {
            for (var cx = minX; cx <= maxX; cx++)
            {
                if (!_cells.TryGetValue(Key(cx, cy), out var cell))
                {
                    continue;
                }

                foreach (var entry in cell)
                {
                    if (seen.Add(entry.Id) && entry.Bounds.Intersects(area))
                    {
                        yield return entry.Id;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Up to <paramref name="cap"/> cards inside the area, nearest to the camera's centre
    /// first.
    /// </summary>
    /// <remarks>
    /// The cap exists so a padded viewport over an enormous dense cluster still draws a
    /// bounded number of cards. Nearest-first is what makes the choice a reasonable one: the
    /// cards closest to what a person is looking at are the ones that survive the cut, rather
    /// than whichever happened to come first in a dictionary.
    /// </remarks>
    public IEnumerable<Guid> QueryNearest(CanvasBounds area, CanvasViewport camera, int cap)
    {
        if (cap <= 0)
        {
            yield break;
        }

        var centreX = camera.ToWorldX(0);
        var centreY = camera.ToWorldY(0);

        var candidates = new List<(Guid Id, double Distance)>();

        foreach (var id in Query(area))
        {
            candidates.Add((id, Distance(_bounds[id], centreX, centreY)));
        }

        if (candidates.Count <= cap)
        {
            foreach (var (id, _) in candidates)
            {
                yield return id;
            }

            yield break;
        }

        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        for (var i = 0; i < cap && i < candidates.Count; i++)
        {
            yield return candidates[i].Id;
        }
    }

    private void Insert(Guid id, CanvasBounds bounds)
    {
        _bounds[id] = bounds;

        var (minX, minY, maxX, maxY) = Cells(bounds);

        for (var cy = minY; cy <= maxY; cy++)
        {
            for (var cx = minX; cx <= maxX; cx++)
            {
                var key = Key(cx, cy);

                if (!_cells.TryGetValue(key, out var cell))
                {
                    cell = [];
                    _cells[key] = cell;
                }

                cell.Add(new Entry(id, bounds));
            }
        }
    }

    private void Remove(Guid id)
    {
        if (!_bounds.Remove(id, out var bounds))
        {
            return;
        }

        var (minX, minY, maxX, maxY) = Cells(bounds);

        for (var cy = minY; cy <= maxY; cy++)
        {
            for (var cx = minX; cx <= maxX; cx++)
            {
                if (_cells.TryGetValue(Key(cx, cy), out var cell))
                {
                    cell.RemoveAll(entry => entry.Id == id);
                }
            }
        }
    }

    private (int MinX, int MinY, int MaxX, int MaxY) Cells(CanvasBounds area)
    {
        var minX = (int)Math.Floor(area.X / CellSize);
        var minY = (int)Math.Floor(area.Y / CellSize);
        var maxX = (int)Math.Floor((area.X + area.Width) / CellSize);
        var maxY = (int)Math.Floor((area.Y + area.Height) / CellSize);

        return (minX, minY, maxX, maxY);
    }

    private static long Key(int cellX, int cellY) =>
        ((long)cellX << 32) | (uint)cellY;

    private static double Distance(CanvasBounds bounds, double x, double y)
    {
        var dx = Math.Max(bounds.X - x, Math.Max(0, x - (bounds.X + bounds.Width)));
        var dy = Math.Max(bounds.Y - y, Math.Max(0, y - (bounds.Y + bounds.Height)));

        return (dx * dx) + (dy * dy);
    }
}
