using System.Collections.Generic;

namespace DoclingDotNet.Algorithms.Spatial;

public sealed class SpatialIndex<T>
{
    private readonly IntervalTree _xTree = new();
    private readonly IntervalTree _yTree = new();
    private readonly Dictionary<int, (BoundingBox Bounds, T Item)> _items = new();

    public void Insert(int id, BoundingBox bounds, T item)
    {
        _items[id] = (bounds, item);
        _xTree.Insert(bounds.L, bounds.R, id);
        _yTree.Insert(bounds.B, bounds.T, id);
    }

    /// <remarks>
    /// Items are removed from the lookup dictionary immediately.
    /// Their interval-tree entries are lazily skipped during <see cref="Intersection"/> queries.
    /// </remarks>
    public void Remove(int id)
    {
        _items.Remove(id);
    }

    public IEnumerable<(int Id, T Item)> Intersection(BoundingBox query)
    {
        var candidates = _xTree.FindOverlapping(query.L, query.R);
        candidates.IntersectWith(_yTree.FindOverlapping(query.B, query.T));

        foreach (var id in candidates)
        {
            if (_items.TryGetValue(id, out var entry))
            {
                yield return (id, entry.Item);
            }
        }
    }
}