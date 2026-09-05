using System.Windows;
using AIClient.Domain.Graph;

namespace AIClient.App.Canvas;

/// <summary>
/// A uniform-grid spatial index over node rectangles, in world coordinates.
/// </summary>
/// <remarks>
/// <para>
/// Every "what is under the cursor" and "what is on screen" question the canvas asks is
/// answered here rather than by walking the graph or by WPF's own visual hit-testing,
/// both of which are linear in node count and both of which get slow in the thousands.
/// A node lands in every bucket its rectangle touches, so a query is exact, not an
/// approximation followed by a filter pass.
/// </para>
/// <para>
/// The bucket size is one number tuned for this product's nodes (~200×60): at 256 world
/// units, a viewport at 100% zoom covers a handful of buckets, and a 10,000-node graph
/// spreads across enough buckets that a viewport query touches a small fraction of them.
/// There is no tree to rebalance and no rebalance cost while dragging.
/// </para>
/// </remarks>
public sealed class SpatialIndex
{
    private const double BucketSize = 256;

    // Presized for the common case; the grid dictionary itself starts empty and only
    // grows to as many buckets as the graph actually occupies.
    private readonly Dictionary<string, Rect> _rects = new(StringComparer.Ordinal);
    private readonly Dictionary<long, HashSet<string>> _buckets = [];

    public int Count => _rects.Count;

    /// <summary>Replaces the index wholesale - the snapshot came from a restore or a load.</summary>
    public void Rebuild(IEnumerable<GraphNode> nodes)
    {
        _rects.Clear();
        _buckets.Clear();

        foreach (var node in nodes)
        {
            Insert(node);
        }
    }

    public void Insert(GraphNode node)
    {
        var rect = NodeRect(node);

        _rects[node.Id] = rect;

        foreach (var cell in CellsOf(rect))
        {
            if (!_buckets.TryGetValue(cell, out var bucket))
            {
                bucket = [];
                _buckets[cell] = bucket;
            }

            bucket.Add(node.Id);
        }
    }

    public void Remove(string nodeId)
    {
        if (!_rects.Remove(nodeId, out var rect))
        {
            return;
        }

        foreach (var cell in CellsOf(rect))
        {
            if (_buckets.TryGetValue(cell, out var bucket))
            {
                bucket.Remove(nodeId);

                if (bucket.Count == 0)
                {
                    // Buckets die when empty: a graph that shrank should not pay for the
                    // space it used to occupy on every future query.
                    _buckets.Remove(cell);
                }
            }
        }
    }

    /// <summary>A node moved or resized: re-buckets it.</summary>
    public void Update(GraphNode node) => Insert(node);

    /// <summary>Node ids whose rectangles touch the query rect. Callers refine for precision.</summary>
    public IReadOnlyList<string> Query(Rect worldRect)
    {
        if (_buckets.Count == 0 || worldRect.IsEmpty)
        {
            return [];
        }

        HashSet<string>? hits = null;

        foreach (var cell in CellsOf(worldRect))
        {
            if (!_buckets.TryGetValue(cell, out var bucket))
            {
                continue;
            }

            hits ??= [];

            foreach (var id in bucket)
            {
                if (_rects.TryGetValue(id, out var rect) && rect.IntersectsWith(worldRect))
                {
                    hits.Add(id);
                }
            }
        }

        if (hits is null)
        {
            return [];
        }

        return [.. hits];
    }

    /// <summary>
    /// The node under a world point, or null. Ties go to the smaller node: when a small
    /// node sits over a large one, the eye expects the small one, and "smallest wins" is
    /// also what makes dense clusters clickable.
    /// </summary>
    public string? HitNode(Point worldPoint)
    {
        string? bestId = null;
        var bestArea = double.PositiveInfinity;

        foreach (var id in Query(new Rect(worldPoint, new Size(1, 1))))
        {
            if (!_rects.TryGetValue(id, out var rect) || !rect.Contains(worldPoint))
            {
                continue;
            }

            var area = rect.Width * rect.Height;

            if (area < bestArea)
            {
                bestArea = area;
                bestId = id;
            }
        }

        return bestId;
    }

    /// <summary>Node ids fully or partially inside a rect - the marquee's selection set.</summary>
    public IReadOnlyList<string> QueryContained(Rect worldRect)
    {
        var candidates = Query(worldRect);

        if (candidates.Count == 0)
        {
            return [];
        }

        return [.. candidates.Where(id => _rects.TryGetValue(id, out var rect) && worldRect.Contains(rect))];
    }

    public static Rect NodeRect(GraphNode node) => new(
        node.X - node.Width / 2,
        node.Y - node.Height / 2,
        node.Width,
        node.Height);

    private static IEnumerable<long> CellsOf(Rect rect)
    {
        var x0 = (long)Math.Floor(rect.Left / BucketSize);
        var y0 = (long)Math.Floor(rect.Top / BucketSize);
        var x1 = (long)Math.Floor(rect.Right / BucketSize);
        var y1 = (long)Math.Floor(rect.Bottom / BucketSize);

        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                yield return (x & 0x7FFFFFFF) | (y << 32);
            }
        }
    }
}
