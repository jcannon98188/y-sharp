using System;
using System.Collections.Generic;

namespace YSharp.Store;

/// <summary>One deleted clock range of one client (Yjs <c>DeleteItem</c> plus its client).</summary>
internal readonly record struct DeleteRange(ulong Client, long Clock, long Length)
{
    internal long End => Clock + Length;
}

/// <summary>
/// The internal delete set (Yjs <c>DeleteSet</c>): clock ranges kept sorted by (client, clock) and merged as they are added, so it never needs a
/// separate <c>sortAndMergeDeleteSet</c> pass. Ranges are held in chunks of at most 64 KB. A transaction's set is cleared after its commit and
/// keeps its blocks, so a steady stream of edits reuses them instead of allocating new ones.
/// </summary>
internal sealed class DeleteRanges
{
    private readonly ChunkedList<DeleteRange> _ranges = new();
    private readonly List<ulong>? _clientOrder;

    internal DeleteRanges(bool trackClientOrder = false)
    {
        if (trackClientOrder) _clientOrder = new List<ulong>();
    }

    /// <summary>
    /// The clients in the order each got its first range, which is the iteration order of the Yjs <c>DeleteSet.clients</c> Map (sorting
    /// never reorders its keys); null unless the set was made to track it. A transaction's set tracks it for the formatting cleanup.
    /// </summary>
    internal IReadOnlyList<ulong>? ClientOrder => _clientOrder;

    public int Count => _ranges.Count;

    public DeleteRange this[int index] => _ranges[index];

    internal long MaxChunkBytes => _ranges.MaxBlockBytes;

    /// <summary>Yjs <c>addToDeleteSet</c>, merging with any range it touches or overlaps.</summary>
    public void Add(ulong client, long clock, long length)
    {
        if (length <= 0) return;

        // the first range that starts after (client, clock)
        int lo = 0, hi = _ranges.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            var r = _ranges[mid];
            if (r.Client < client || (r.Client == client && r.Clock <= clock)) lo = mid + 1;
            else hi = mid;
        }

        var end = clock + length;
        var at = lo;
        if (at > 0)
        {
            var prev = _ranges[at - 1];
            if (prev.Client == client && prev.End >= clock)
            {
                at--;
                clock = prev.Clock;
                if (prev.End > end) end = prev.End;
                _ranges[at] = new DeleteRange(client, clock, end - clock);
                AbsorbFollowing(at);
                return;
            }
        }

        if (_clientOrder != null && (at == 0 || _ranges[at - 1].Client != client) && (at == _ranges.Count || _ranges[at].Client != client))
            _clientOrder.Add(client);
        _ranges.Insert(at, new DeleteRange(client, clock, length));
        AbsorbFollowing(at);
    }

    /// <summary>Yjs <c>isDeleted</c>.</summary>
    public bool Contains(ulong client, long clock)
    {
        int lo = 0, hi = _ranges.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var r = _ranges[mid];
            if (r.Client < client || (r.Client == client && r.End <= clock)) lo = mid + 1;
            else if (r.Client > client || r.Clock > clock) hi = mid - 1;
            else return true;
        }

        return false;
    }

    /// <summary>Empties the set but keeps its blocks for the next transaction.</summary>
    public void Clear()
    {
        _ranges.Clear();
        _clientOrder?.Clear();
    }

    /// <summary>The index of the first range of <paramref name="client"/>, or <see cref="Count"/> when it has none.</summary>
    public int FirstIndexOf(ulong client)
    {
        int lo = 0, hi = _ranges.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_ranges[mid].Client < client) lo = mid + 1;
            else hi = mid;
        }

        return lo < _ranges.Count && _ranges[lo].Client == client ? lo : _ranges.Count;
    }

    private void AbsorbFollowing(int at)
    {
        var current = _ranges[at];
        var remove = 0;
        while (at + 1 + remove < _ranges.Count)
        {
            var next = _ranges[at + 1 + remove];
            if (next.Client != current.Client || next.Clock > current.End) break;
            var end = Math.Max(next.End, current.End);
            current = new DeleteRange(current.Client, current.Clock, end - current.Clock);
            remove++;
        }

        if (remove == 0) return;
        _ranges[at] = current;
        _ranges.RemoveRange(at + 1, remove);
    }
}
