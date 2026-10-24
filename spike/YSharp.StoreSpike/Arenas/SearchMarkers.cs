using System;

namespace YSharp.StoreSpike.Arenas;

/// <summary>
/// Yjs search markers (<c>AbstractType.js</c> 27-200), at most <see cref="MaxMarkers"/> per sequence. A marker is an item slot index and that item's
/// start index. The marked item carries <see cref="ItemFlags.Marker"/>, so a commit merge can repoint the marker before freeing the slot.
/// </summary>
internal sealed class SearchMarkers
{
    public const int MaxMarkers = 80;

    private struct Marker
    {
        public int Item;
        public int Index;
        public long Timestamp;
    }

    private readonly Marker[] _markers = new Marker[MaxMarkers];
    private int _count;
    private long _timestamp;

    public int Count => _count;

    /// <summary>
    /// <c>findMarker</c>: the item at or before <paramref name="index"/> to start a walk from, and its start index. Returns false when the walk
    /// should start at the beginning of the sequence.
    /// </summary>
    public bool Find(StructArena arena, ArenaSequence sequence, int index, out int item, out int itemIndex)
    {
        item = -1;
        itemIndex = 0;
        if (sequence.Start < 0 || index == 0) return false;

        var nearest = -1;
        for (var i = 0; i < _count; i++)
        {
            if (nearest < 0 || Math.Abs(index - _markers[i].Index) < Math.Abs(index - _markers[nearest].Index)) nearest = i;
        }

        var p = sequence.Start;
        var pindex = 0;
        if (nearest >= 0)
        {
            p = _markers[nearest].Item;
            pindex = _markers[nearest].Index;
            _markers[nearest].Timestamp = _timestamp++;
        }

        // Walk right.
        while (arena[p].Right >= 0 && pindex < index)
        {
            ref var slot = ref arena[p];
            if (!slot.IsDeleted)
            {
                if (index < pindex + slot.Length) break;
                pindex += slot.Length;
            }

            p = slot.Right;
        }

        // Walk left.
        while (arena[p].Left >= 0 && pindex > index)
        {
            p = arena[p].Left;
            pindex -= arena[p].CountableLength;
        }

        // Make sure p cannot be merged with its left (Yjs keeps markers off items a commit merge would remove).
        while (arena[p].Left >= 0)
        {
            ref var left = ref arena[arena[p].Left];
            ref var self = ref arena[p];
            if (left.Client != self.Client || left.Clock + left.Length != self.Clock) break;
            p = self.Left;
            pindex -= arena[p].CountableLength;
        }

        if (nearest >= 0 && Math.Abs(_markers[nearest].Index - pindex) < sequence.Length / MaxMarkers) Overwrite(arena, nearest, p, pindex);
        else Mark(arena, p, pindex);

        item = p;
        itemIndex = pindex;
        return true;
    }

    /// <summary><c>updateMarkerChanges</c>: shifts markers after an insert (positive <paramref name="length"/>) or a delete (negative) at <paramref name="index"/>.</summary>
    public void Update(StructArena arena, int index, int length)
    {
        for (var i = _count - 1; i >= 0; i--)
        {
            ref var marker = ref _markers[i];
            if (length > 0)
            {
                // A marker on a deleted item moves left to the nearest countable item.
                var p = marker.Item;
                arena[p].Flags &= ~ItemFlags.Marker;
                while (p >= 0 && arena[p].IsDeleted)
                {
                    p = arena[p].Left;
                    if (p >= 0 && !arena[p].IsDeleted) marker.Index -= arena[p].Length;
                }

                if (p < 0 || (arena[p].Flags & ItemFlags.Marker) != 0)
                {
                    RemoveAt(i);
                    continue;
                }

                marker.Item = p;
                arena[p].Flags |= ItemFlags.Marker;
            }

            if (index < marker.Index || (length > 0 && index == marker.Index)) marker.Index = Math.Max(index, marker.Index + length);
        }
    }

    /// <summary>A commit merge folds <paramref name="right"/> into <paramref name="left"/>: a marker on the right item moves to the left one.</summary>
    public void Repoint(StructArena arena, int right, int left)
    {
        for (var i = 0; i < _count; i++)
        {
            if (_markers[i].Item != right) continue;
            _markers[i].Item = left;
            if (!arena[left].IsDeleted) _markers[i].Index -= arena[left].Length;
            arena[left].Flags |= ItemFlags.Marker;
        }
    }

    private void Overwrite(StructArena arena, int marker, int item, int index)
    {
        arena[_markers[marker].Item].Flags &= ~ItemFlags.Marker;
        _markers[marker].Item = item;
        _markers[marker].Index = index;
        _markers[marker].Timestamp = _timestamp++;
        arena[item].Flags |= ItemFlags.Marker;
    }

    private void Mark(StructArena arena, int item, int index)
    {
        if (_count < MaxMarkers)
        {
            _markers[_count] = new Marker { Item = item, Index = index, Timestamp = _timestamp++ };
            _count++;
            arena[item].Flags |= ItemFlags.Marker;
            return;
        }

        var oldest = 0;
        for (var i = 1; i < _count; i++)
        {
            if (_markers[i].Timestamp < _markers[oldest].Timestamp) oldest = i;
        }

        Overwrite(arena, oldest, item, index);
    }

    private void RemoveAt(int i)
    {
        _count--;
        if (i < _count) Array.Copy(_markers, i + 1, _markers, i, _count - i);
    }
}
