#nullable enable
using System;
using System.Collections.Generic;
using YSharp.Integration;
using YSharp.Lib0;
using YSharp.Store;

namespace YSharp;

// The pending delete set (Yjs store.pendingDs), changed in place as updates arrive (TASK-12). Yjs re-reads the whole set on every update, but a
// range at or past its client's state reads back as it was, and a client's state moves only when an update (or its retry) integrates that
// client's structs or the document edits as that client. So only those clients' ranges are re-applied, in the order Yjs applies them, and the
// set's V1 size is kept as it changes: neither an update nor the MaxPendingBytes check costs time proportional to what is pending.
public sealed partial class Doc
{
    private PendingDeleteSize _pendingDeleteSize;

    // client ids this document edited as before it picked a new one: its state may have passed a pending range since the last update
    private List<ulong>? _retiredClientIds;

    /// <summary>Called when the document stops using <paramref name="client"/> as its id, so its pending ranges are still re-applied.</summary>
    private void RetireClientId(ulong client)
    {
        if (_pendingDeletes is not null) (_retiredClientIds ??= new List<ulong>()).Add(client);
    }

    /// <summary>
    /// The clients with pending delete ranges whose state the update can have moved: its struct clients and the ids the document edits as.
    /// Null when nothing is pending. Taken before the structs are integrated, which consumes them.
    /// </summary>
    private List<ulong>? ReachableDeleteClients(UpdateRefs update, DeleteRanges? pendingDeletes)
    {
        if (pendingDeletes is null) return null;
        List<ulong>? clients = null;
        foreach (var c in update.ClientValues) AddIfPending(ref clients, pendingDeletes, c.Client);
        AddIfPending(ref clients, pendingDeletes, ClientId);
        if (_retiredClientIds is { } retired)
            foreach (var c in retired) AddIfPending(ref clients, pendingDeletes, c);
        return clients;
    }

    private static void AddIfPending(ref List<ulong>? clients, DeleteRanges pendingDeletes, ulong client)
    {
        if (pendingDeletes.FirstIndexOf(client) < pendingDeletes.Count) (clients ??= new List<ulong>()).Add(client);
    }

    /// <summary>
    /// Yjs <c>readUpdateV2</c>'s pending delete step, after the update's own ranges were applied: re-applies the pending ranges of
    /// <paramref name="reach"/> below their client's state and merges what stays unapplied of them and <paramref name="dsRest"/> into the set,
    /// clients descending as Yjs writes and applies them. Ranges are applied with a transaction, and checked against the dry run's state
    /// without one. <paramref name="size"/> follows the set; the set itself is changed (in place) only when <paramref name="change"/> is set.
    /// </summary>
    private void SettlePendingDeletes(Transaction? tx, PendingDryRun? dry, List<ulong>? reach, DeleteRanges? dsRest, ref DeleteRanges? set, ref PendingDeleteSize size, bool change)
    {
        if (reach is null && dsRest is null) return;
        var clients = reach ?? new List<ulong>();
        if (dsRest is not null)
            for (var i = 0; i < dsRest.Count; i++)
                if (i == 0 || dsRest[i - 1].Client != dsRest[i].Client) clients.Add(dsRest[i].Client);
        clients.Sort(static (a, b) => b.CompareTo(a));

        for (var k = 0; k < clients.Count; k++)
        {
            var client = clients[k];
            if (k > 0 && clients[k - 1] == client) continue;

            // the client's pending ranges below its state: a prefix, as the ranges are sorted and every one was at or past the state once
            int first = 0, end = 0, drop = 0;
            if (set is not null)
            {
                end = set.EndIndexOf(client);
                first = Math.Min(set.FirstIndexOf(client), end);
                var state = StateOf(client, dry);
                while (first + drop < end && set[first + drop].Clock < state) drop++;
            }

            DeleteRanges? added = null;
            if (drop > 0)
            {
                var reached = new List<DeleteRange>(drop);
                for (var i = first; i < first + drop; i++) reached.Add(set![i]);
                added = tx is null ? Unapplied(dry!, reached) : ApplyDeleteSet(tx, reached);
            }

            int from = 0, to = 0;
            if (dsRest is not null)
            {
                to = dsRest.EndIndexOf(client);
                from = Math.Min(dsRest.FirstIndexOf(client), to);
            }

            var source = dsRest;
            if (added is not null)
            {
                for (var i = from; i < to; i++) added.Add(client, dsRest![i].Clock, dsRest[i].Length);
                (source, from, to) = (added, 0, added.Count);
            }

            if (drop == 0 && from == to) continue;
            size.Change(set, client, first, end, drop, source, from, to);
            if (!change) continue;
            set ??= new DeleteRanges();
            set.RemoveRange(first, drop);
            for (var i = from; i < to; i++) set.Add(client, source![i].Clock, source[i].Length);
            if (set.Count == 0) set = null;
        }
    }
}

/// <summary>
/// The V1 size of a pending delete set written as an update's delete section, kept as the set changes: the number of clients, and the bytes
/// written for them (each client's id, range count and ranges). The client count itself is written only for a set that is not empty.
/// </summary>
internal struct PendingDeleteSize
{
    public int Clients;
    public long Body;

    public readonly long Bytes => Clients == 0 ? 0 : Lib0Writer.VarUintLength((ulong)Clients) + Body;

    /// <summary>
    /// The change of replacing the first <paramref name="drop"/> of <paramref name="client"/>'s ranges (<paramref name="first"/> to
    /// <paramref name="end"/> in <paramref name="set"/>) and adding <paramref name="added"/>[<paramref name="from"/>..<paramref name="to"/>]
    /// (sorted and merged, of the same client), as <see cref="DeleteRanges.Add"/> merges them: only the ranges they touch are read.
    /// </summary>
    public void Change(DeleteRanges? set, ulong client, int first, int end, int drop, DeleteRanges? added, int from, int to)
    {
        var oldCount = end - first;
        var body = -Header(client, oldCount);
        for (var i = first; i < first + drop; i++) body -= RangeBytes(set![i].Clock, set[i].Length);

        // merge the added ranges with the kept ranges they touch, in clock order
        var kept = first + drop;
        var newCount = oldCount - drop;
        long runStart = 0, runEnd = 0;
        var open = false;
        for (var a = from; a < to; a++)
        {
            long start = added![a].Clock, stop = added[a].End;
            if (open && runEnd >= start)
            {
                start = runStart;
                stop = Math.Max(stop, runEnd);
            }
            else if (open)
            {
                body += RangeBytes(runStart, runEnd - runStart);
                newCount++;
            }

            var j = FirstEndingFrom(set, kept, end, start);
            while (j < end && set![j].Clock <= stop)
            {
                body -= RangeBytes(set[j].Clock, set[j].Length);
                newCount--;
                start = Math.Min(start, set[j].Clock);
                stop = Math.Max(stop, set[j].End);
                j++;
            }

            kept = Math.Max(kept, j);
            (runStart, runEnd, open) = (start, stop, true);
        }

        if (open)
        {
            body += RangeBytes(runStart, runEnd - runStart);
            newCount++;
        }

        Body += body + Header(client, newCount);
        Clients += (newCount > 0 ? 1 : 0) - (oldCount > 0 ? 1 : 0);
    }

    /// <summary>The first range from <paramref name="lo"/> to <paramref name="hi"/> that ends at or after <paramref name="clock"/>.</summary>
    private static int FirstEndingFrom(DeleteRanges? set, int lo, int hi, long clock)
    {
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (set![mid].End < clock) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private static long Header(ulong client, int count) =>
        count == 0 ? 0 : Lib0Writer.VarUintLength(client) + Lib0Writer.VarUintLength((ulong)count);

    private static long RangeBytes(long clock, long length) =>
        Lib0Writer.VarUintLength((ulong)clock) + Lib0Writer.VarUintLength((ulong)length);
}
