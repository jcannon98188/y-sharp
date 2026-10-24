using System;

namespace YSharp.StoreSpike.ObjectPerItem;

/// <summary>A shared type's list or map root: the first item of its sequence, its visible length, and its search markers or key table.</summary>
public sealed class Branch
{
    internal Item? Start;
    internal int Length;
    internal readonly SearchMarkers? Markers;
    internal readonly MapKeyTable? Map;

    private Branch(SearchMarkers? markers, MapKeyTable? map)
    {
        Markers = markers;
        Map = map;
    }

    internal static Branch Sequence(SearchMarkers markers) => new(markers, null);

    internal static Branch Keyed(MapKeyTable map) => new(null, map);
}

/// <summary>
/// Yjs search markers (<c>AbstractType.js</c>): at most 80 remembered (item, index) pairs per sequence, so an index lookup walks only from the
/// nearest marker. Fixed arrays, allocated once per branch, so marker upkeep allocates nothing. The timestamp clock belongs to the document,
/// not to a static, so no state outlives the document (ADR 0018).
/// </summary>
public sealed class SearchMarkers
{
    public const int Max = 80;

    private readonly Item?[] _items = new Item?[Max];
    private readonly int[] _indexes = new int[Max];
    private readonly long[] _stamps = new long[Max];
    private readonly MarkerClock _clock;
    private int _count;

    internal SearchMarkers(MarkerClock clock) => _clock = clock;

    public int Count => _count;

    /// <summary>A marker found by <see cref="Find"/>: the marked item and its index.</summary>
    internal readonly record struct Hit(Item Item, int Index);

    /// <summary>Yjs <c>findMarker</c>: returns a refreshed marker at or near <paramref name="index"/>, or null when the walk should start at the front.</summary>
    internal Hit? Find(Branch parent, int index)
    {
        if (parent.Start is null || index == 0) return null;

        var best = -1;
        for (var i = 0; i < _count; i++)
        {
            if (best < 0 || Math.Abs(index - _indexes[i]) < Math.Abs(index - _indexes[best])) best = i;
        }

        var p = parent.Start;
        var pindex = 0;
        if (best >= 0)
        {
            p = _items[best]!;
            pindex = _indexes[best];
            _stamps[best] = _clock.Next();
        }

        // iterate to the right if possible
        while (p.Right is not null && pindex < index)
        {
            if (!p.Deleted && p.Countable)
            {
                if (index < pindex + p.Length) break;
                pindex += p.Length;
            }

            p = p.Right;
        }

        // iterate to the left if necessary
        while (p.Left is not null && pindex > index)
        {
            p = p.Left;
            if (!p.Deleted && p.Countable) pindex -= p.Length;
        }

        // move left until p cannot be merged with its left neighbour
        while (p.Left is not null && p.Left.Client == p.Client && p.Left.Clock + (uint)p.Left.Length == p.Clock)
        {
            p = p.Left;
            if (!p.Deleted && p.Countable) pindex -= p.Length;
        }

        if (best >= 0 && Math.Abs(_indexes[best] - pindex) < parent.Length / (double)Max)
        {
            Overwrite(best, p, pindex);
            return new Hit(p, pindex);
        }

        Mark(p, pindex);
        return new Hit(p, pindex);
    }

    /// <summary>Yjs <c>updateMarkerChanges</c>: call before a deletion (negative <paramref name="len"/>) or an insertion.</summary>
    internal void Update(int index, int len)
    {
        for (var i = _count - 1; i >= 0; i--)
        {
            if (len > 0)
            {
                var p = _items[i];
                p!.Marker = false;
                while (p is not null && (p.Deleted || !p.Countable))
                {
                    p = p.Left;
                    if (p is not null && !p.Deleted && p.Countable) _indexes[i] -= p.Length;
                }

                if (p is null || p.Marker)
                {
                    RemoveAt(i);
                    continue;
                }

                _items[i] = p;
                p.Marker = true;
            }

            if (index < _indexes[i] || (len > 0 && index == _indexes[i])) _indexes[i] = Math.Max(index, _indexes[i] + len);
        }
    }

    /// <summary>The merge rule of <c>Item.mergeWith</c>: a marker on <paramref name="right"/> moves to <paramref name="left"/>.</summary>
    internal void Repoint(Item right, Item left)
    {
        for (var i = 0; i < _count; i++)
        {
            if (!ReferenceEquals(_items[i], right)) continue;
            _items[i] = left;
            if (!left.Deleted && left.Countable) _indexes[i] -= left.Length;
        }
    }

    private void Mark(Item p, int index)
    {
        if (_count >= Max)
        {
            var oldest = 0;
            for (var i = 1; i < _count; i++) if (_stamps[i] < _stamps[oldest]) oldest = i;
            Overwrite(oldest, p, index);
            return;
        }

        p.Marker = true;
        _items[_count] = p;
        _indexes[_count] = index;
        _stamps[_count] = _clock.Next();
        _count++;
    }

    private void Overwrite(int i, Item p, int index)
    {
        _items[i]!.Marker = false;
        _items[i] = p;
        p.Marker = true;
        _indexes[i] = index;
        _stamps[i] = _clock.Next();
    }

    private void RemoveAt(int i)
    {
        _count--;
        Array.Copy(_items, i + 1, _items, i, _count - i);
        Array.Copy(_indexes, i + 1, _indexes, i, _count - i);
        Array.Copy(_stamps, i + 1, _stamps, i, _count - i);
        _items[_count] = null;
    }
}

/// <summary>The document's marker timestamp counter (Yjs keeps a module-level one; a per-document one roots nothing).</summary>
internal sealed class MarkerClock
{
    private long _now;

    public long Next() => _now++;
}
