using YSharp.StoreSpike.Content;

namespace YSharp.StoreSpike.ObjectPerItem;

/// <summary>One deleted clock range of one client (Yjs <c>DeleteItem</c>, plus the client slot).</summary>
public readonly record struct DeleteRange(int Client, uint Clock, uint Length)
{
    internal uint End => Clock + Length;
}

/// <summary>
/// A delete set kept sorted by (client slot, clock) and merged as it grows, so the commit needs no separate <c>sortAndMergeDeleteSet</c> pass.
/// Ranges are 12-byte structs in chunks of at most 64 KB. The store owns one and clears it after every commit, so a steady stream of
/// edits reuses its blocks instead of allocating new ones.
/// </summary>
public sealed class DeleteSet
{
    private readonly SmallStartList<DeleteRange> _ranges = new();

    public int Count => _ranges.Count;

    public DeleteRange this[int index] => _ranges[index];

    internal long MaxChunkBytes => _ranges.MaxBlockBytes;

    public void Add(int client, uint clock, uint length)
    {
        if (length == 0) return;

        // first range that starts after (client, clock)
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
                // extend the previous range
                at--;
                clock = prev.Clock;
                if (prev.End > end) end = prev.End;
                _ranges[at] = new DeleteRange(client, clock, end - clock);
                AbsorbFollowing(at);
                return;
            }
        }

        _ranges.Insert(at, new DeleteRange(client, clock, length));
        AbsorbFollowing(at);
    }

    /// <summary>Yjs <c>isDeleted</c>.</summary>
    public bool IsDeleted(int client, uint clock)
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
        for (var i = _ranges.Count - 1; i >= 0; i--) _ranges.RemoveAt(i);
    }

    private void AbsorbFollowing(int at)
    {
        var current = _ranges[at];
        while (at + 1 < _ranges.Count)
        {
            var next = _ranges[at + 1];
            if (next.Client != current.Client || next.Clock > current.End) break;
            var end = next.End > current.End ? next.End : current.End;
            current = new DeleteRange(current.Client, current.Clock, end - current.Clock);
            _ranges[at] = current;
            _ranges.RemoveAt(at + 1);
        }
    }
}
