using System;
using System.Collections.Generic;

namespace YSharp.Server;

/// <summary>
/// The clock ranges a batch of stored records covers, per client (TASK-18): the Items and GCs of each record, not its Skips. A struct of length 0
/// counts as covering its clock, so a record that only claims an id still ends a batch. Per client the ranges are sorted and disjoint, and a range
/// joins any it touches, so a log written in order holds one range per client.
/// </summary>
internal sealed class ClockRanges
{
    private readonly Dictionary<ulong, List<(long Start, long End)>> _byClient = new();

    public void Clear() => _byClient.Clear();

    /// <summary>Whether a struct of <paramref name="update"/> covers a clock already added.</summary>
    public bool Overlaps(DecodedUpdate update)
    {
        foreach (var s in update.Structs)
        {
            if (s.Kind == UpdateStructKind.Skip || !_byClient.TryGetValue(s.Id.Client, out var ranges)) continue;
            var i = FirstEndingAfter(ranges, s.Id.Clock);
            if (i < ranges.Count && ranges[i].Start < End(s)) return true;
        }

        return false;
    }

    public void Add(DecodedUpdate update)
    {
        foreach (var s in update.Structs)
        {
            if (s.Kind == UpdateStructKind.Skip) continue;
            if (!_byClient.TryGetValue(s.Id.Client, out var ranges)) _byClient[s.Id.Client] = ranges = new List<(long, long)>();
            var start = s.Id.Clock;
            var end = End(s);
            // joined with every range it overlaps or touches
            var first = FirstEndingAfter(ranges, start - 1);
            var last = first;
            while (last < ranges.Count && ranges[last].Start <= end)
            {
                start = Math.Min(start, ranges[last].Start);
                end = Math.Max(end, ranges[last].End);
                last++;
            }

            ranges.RemoveRange(first, last - first);
            ranges.Insert(first, (start, end));
        }
    }

    private static long End(in UpdateStruct s) => s.Id.Clock + Math.Max(s.Length, 1);

    private static int FirstEndingAfter(List<(long Start, long End)> ranges, long clock)
    {
        int lo = 0, hi = ranges.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (ranges[mid].End <= clock) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }
}
