#nullable enable
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using YSharp.Codecs;
using YSharp.Codecs.V1;
using YSharp.Integration;
using YSharp.Lib0;
using YSharp.Store;

namespace YSharp;

// MaxPendingBytes (ADR 0005, ERR-10): what is measured is the V1 encoding of the pending state retained after the update, so an update that would
// take it above the limit is rejected whole. The pending state an update leaves is only known by integrating it, so a dry run walks the same
// loop first (IntegrateStructs with a PendingDryRun) against a virtual state, and the update is integrated only when the prediction fits.
public sealed partial class Doc
{
    /// <summary>
    /// Integrates a decoded update in a remote transaction of its own, or in the calling thread's open one, unless the pending state it would
    /// leave is over <paramref name="maxPendingBytes"/>: then nothing is integrated, no transaction is opened, and the result is false.
    /// <paramref name="rejected"/> counts the rejected structs (ADR 0005). Throws <see cref="YInternalException"/> on a faulted document.
    /// </summary>
    internal bool TryIntegrateRemote(UpdateRefs update, object? origin, long maxPendingBytes, out int rejected)
    {
        ThrowIfFaulted();
        rejected = 0;
        var entry = _lock.EnterWrite(Timeout.Infinite, default);
        long predicted;
        try
        {
            predicted = PredictPendingBytes(update);
        }
        catch
        {
            _lock.ExitWrite();
            throw;
        }

        if (predicted > maxPendingBytes)
        {
            _lock.ExitWrite();
            return false;
        }

        var h = BeginWrite(entry, origin, local: false);
        try
        {
            rejected = IntegrateUpdate(h.Tx, update);
            Debug.Assert(V1Bytes(_pending, _pendingDeletes) == predicted, "The dry run predicted a different pending state than the update left.");
        }
        finally
        {
            Leave(h);
        }

        return true;
    }

    /// <summary>
    /// The V1 size of the pending state <paramref name="update"/> would leave: <see cref="ReadUpdate"/> step by step on copies, with every struct
    /// placed in a virtual state instead of the store. Exact, rejections included: the dry walk resolves dependencies, parents and collected
    /// neighbours from the store and the structs it has placed. The caller holds the write lock.
    /// </summary>
    internal long PredictPendingBytes(UpdateRefs update)
    {
        if (NothingCanWait(update)) return 0;
        var dry = new PendingDryRun();
        var pending = _pending?.Clone();
        DeleteRanges? pendingDeletes = null;
        if (_pendingDeletes is { } current)
        {
            pendingDeletes = new DeleteRanges();
            for (var i = 0; i < current.Count; i++) pendingDeletes.Add(current[i].Client, current[i].Clock, current[i].Length);
        }

        DryReadUpdate(dry, update.Copy(), ref pending, ref pendingDeletes);
        return V1Bytes(pending, pendingDeletes);
    }

    /// <summary>
    /// The hot path, without allocating: nothing is pending, every client's structs follow on from its state with no gap, every dependency on
    /// another client is already in the store, and every delete range lies below its client's state. Then no struct can wait (a rejected one
    /// takes only its own client's later structs with it) and no range can stay unapplied, so nothing would be pending.
    /// </summary>
    private bool NothingCanWait(UpdateRefs update)
    {
        if (_pending is not null || _pendingDeletes is not null) return false;
        foreach (var range in update.Deletes)
            if (range.End > Store.GetState(range.Client)) return false;

        foreach (var client in update.ClientValues)
        {
            var refs = client.Refs;
            if (refs.Count == 0) continue;
            var expected = refs[0].Clock;
            if (expected > Store.GetState(client.Client)) return false;
            for (var i = 0; i < refs.Count; i++)
            {
                var r = refs[i];
                if (r.Kind == RefKind.Skip || r.Clock != expected) return false;
                expected = r.End;
                if (!Known(r.Origin, r.Client) || !Known(r.RightOrigin, r.Client) || !Known(r.ParentId, r.Client)) return false;
            }
        }

        return true;
    }

    private bool Known(ItemId? dep, ulong client) => dep is not { } d || d.Client == client || d.Clock < Store.GetState(d.Client);

    /// <summary><see cref="ReadUpdate"/> against a dry run's virtual state, on the caller's copies of the pending state. Keep the two in step.</summary>
    private void DryReadUpdate(PendingDryRun dry, UpdateRefs update, ref PendingStructs? pending, ref DeleteRanges? pendingDeletes)
    {
        var rejected = 0;
        var rest = IntegrateStructs(null!, update, ref rejected, dry);
        var retry = false;
        if (pending is not null)
        {
            foreach (var (client, clock) in pending.Missing)
            {
                if (clock < dry.StateOf(Store, client))
                {
                    retry = true;
                    break;
                }
            }

            if (rest is not null) pending.MergeFrom(rest);
        }
        else
        {
            pending = rest;
        }

        var dsRest = Unapplied(dry, update.Deletes);
        if (pendingDeletes is { } pendingDs)
        {
            var dsRest2 = Unapplied(dry, InWriteOrder(pendingDs));
            if (dsRest is not null && dsRest2 is not null)
            {
                for (var i = 0; i < dsRest2.Count; i++) dsRest.Add(dsRest2[i].Client, dsRest2[i].Clock, dsRest2[i].Length);
                pendingDeletes = dsRest;
            }
            else
            {
                pendingDeletes = dsRest ?? dsRest2;
            }
        }
        else
        {
            pendingDeletes = dsRest;
        }

        if (retry)
        {
            var again = pending!.ToUpdate();
            pending = null;
            DryReadUpdate(dry, again, ref pending, ref pendingDeletes);
        }
    }

    /// <summary>The part of each range <see cref="ApplyDeleteSet"/> would leave unapplied, against the dry run's state.</summary>
    private DeleteRanges? Unapplied(PendingDryRun dry, List<DeleteRange> ranges)
    {
        DeleteRanges? unapplied = null;
        foreach (var range in ranges)
        {
            var state = dry.StateOf(Store, range.Client);
            if (range.Clock >= state) (unapplied ??= new DeleteRanges()).Add(range.Client, range.Clock, range.Length);
            else if (state < range.End) (unapplied ??= new DeleteRanges()).Add(range.Client, state, range.End - state);
        }

        return unapplied;
    }

    /// <summary>
    /// The part of <see cref="Resolve"/> after the missing checks, for a dry run: whether the struct is rejected (its parent id names a struct
    /// that is not a type) and whether it would be stored as a GC. Nothing is split.
    /// </summary>
    private Resolution DryResolve(PendingDryRun dry, in StructRef r, out bool collected)
    {
        collected = false;
        if ((r.Origin is { } o && dry.KindAt(Store, o) == ContentKind.Gc) || (r.RightOrigin is { } ro && dry.KindAt(Store, ro) == ContentKind.Gc))
        {
            collected = true;
        }
        else if (r.ParentRoot is null && r.ParentId is null)
        {
            collected = r.Origin is null && r.RightOrigin is null;   // no neighbour to take the parent from
        }
        else if (r.ParentId is { } p)
        {
            switch (dry.KindAt(Store, p))
            {
                case ContentKind.Gc:
                case ContentKind.Deleted:
                    collected = true;
                    break;
                case ContentKind.Type:
                    break;
                default:
                    return Resolution.Rejected;
            }
        }

        return Resolution.Ready;
    }

    private long StateOf(ulong client, PendingDryRun? dry) => dry is null ? Store.GetState(client) : dry.StateOf(Store, client);

    /// <summary>
    /// The V1 encoding size of a pending state as one update: each client's structs (clients descending, a gap between two structs written as a
    /// Skip, as Yjs's merged pending update holds it), then the pending delete set. Nothing pending is 0.
    /// </summary>
    private static long V1Bytes(PendingStructs? pending, DeleteRanges? deletes)
    {
        if (pending is null && (deletes is null || deletes.Count == 0)) return 0;
        var structs = new List<StructRef>();
        if (pending is not null)
        {
            var clients = new List<List<StructRef>>();
            foreach (var (_, refs) in pending.Clients) clients.Add(refs);
            for (var c = clients.Count - 1; c >= 0; c--)
            {
                var refs = clients[c];
                for (var i = 0; i < refs.Count; i++)
                {
                    if (i > 0 && refs[i].Clock > refs[i - 1].End)
                        structs.Add(StructRef.Skip(refs[i].Client, refs[i - 1].End, refs[i].Clock - refs[i - 1].End));
                    structs.Add(refs[i]);
                }
            }
        }

        using var w = new Lib0Writer(256);
        var encoder = new UpdateEncoderV1(w);
        UpdateWriter.WriteBlocks(ref encoder, structs);
        UpdateWriter.WriteDeleteRuns(ref encoder, deletes is null ? new List<DeleteRange>() : InWriteOrder(deletes));
        return w.Length;
    }
}

/// <summary>
/// The virtual state of a dry run (<see cref="Doc.PredictPendingBytes"/>): the structs the walk would have integrated, per client, on top of
/// the store. Each client's placed structs continue from its state in the store, so the end of the last one is the client's new state.
/// </summary>
internal sealed class PendingDryRun
{
    private readonly Dictionary<ulong, List<(long Clock, long End, ContentKind Kind)>> _placed = new();

    public long StateOf(StructStore store, ulong client) =>
        _placed.TryGetValue(client, out var placed) ? placed[^1].End : store.GetState(client);

    /// <summary>Records the struct the walk would store from <paramref name="clock"/> to <paramref name="end"/>, as a GC or with its content kind.</summary>
    public void Place(ulong client, long clock, long end, ContentKind kind)
    {
        if (!_placed.TryGetValue(client, out var placed)) _placed[client] = placed = new List<(long, long, ContentKind)>();
        placed.Add((clock, end, kind));
    }

    /// <summary>The kind of the struct holding <paramref name="id"/>, which must be below the client's state: GC for a collected struct.</summary>
    public ContentKind KindAt(StructStore store, ItemId id)
    {
        if (id.Clock < store.GetState(id.Client))
        {
            var s = store.Find(id.Client, id.Clock);
            return s.IsGc ? ContentKind.Gc : s.Kind;
        }

        var placed = _placed[id.Client];
        int lo = 0, hi = placed.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (placed[mid].Clock <= id.Clock) lo = mid;
            else hi = mid - 1;
        }

        return placed[lo].Kind;
    }
}
