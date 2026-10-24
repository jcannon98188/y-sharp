using System;
using System.Collections.Generic;
using YSharp.StoreSpike.Content;

namespace YSharp.StoreSpike.Arenas;

/// <summary>One deleted clock range of one client.</summary>
internal readonly record struct DeleteRange(int Clock, int Length)
{
    public int End => Clock + Length;
}

/// <summary>
/// The delete set: per client index, sorted and merged clock ranges in a <see cref="ChunkedList{T}"/>. Adding keeps the list sorted and merges
/// adjacent or overlapping ranges, which is the state Yjs reaches with <c>sortAndMergeDeleteSet</c> at commit.
/// </summary>
internal sealed class ArenaDeleteSet
{
    private readonly List<ChunkedList<DeleteRange>> _clients = new();

    public int RangeCount(int client) => client < _clients.Count ? _clients[client].Count : 0;

    public void Add(int client, int clock, int length)
    {
        while (_clients.Count <= client) _clients.Add(new ChunkedList<DeleteRange>());
        var ranges = _clients[client];

        // First range that ends at or after the new clock: the new range merges with it or goes before it.
        var i = LowerBoundByEnd(ranges, clock);
        var start = clock;
        var end = clock + length;

        while (i < ranges.Count && ranges[i].Clock <= end)
        {
            start = Math.Min(start, ranges[i].Clock);
            end = Math.Max(end, ranges[i].End);
            ranges.RemoveAt(i);
        }

        ranges.Insert(i, new DeleteRange(start, end - start));
    }

    public bool IsDeleted(int client, int clock)
    {
        if (client >= _clients.Count) return false;
        var ranges = _clients[client];
        var i = LowerBoundByEnd(ranges, clock + 1);
        return i < ranges.Count && ranges[i].Clock <= clock;
    }

    public long MaxChunkBytes
    {
        get
        {
            long max = 0;
            foreach (var ranges in _clients) max = Math.Max(max, ranges.MaxBlockBytes);
            return max;
        }
    }

    /// <summary>Index of the first range whose end is at or after <paramref name="clock"/>.</summary>
    private static int LowerBoundByEnd(ChunkedList<DeleteRange> ranges, int clock)
    {
        int lo = 0, hi = ranges.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (ranges[mid].End < clock) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }
}
