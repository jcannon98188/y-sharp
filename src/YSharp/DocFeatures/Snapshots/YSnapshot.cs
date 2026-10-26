#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using YSharp.Codecs;
using YSharp.Codecs.V1;
using YSharp.Codecs.V2;
using YSharp.Lib0;
using YSharp.Store;
using YSharp.Transactions;

namespace YSharp;

// ---------------------------------------------------------------- snapshots

/// <summary>
/// A document's state at one moment (Yjs <c>Snapshot</c>, spec 30): its state vector and its delete set. Immutable. A decoded snapshot keeps its
/// delete ranges exactly as they were read, unsorted and unmerged, since Yjs compares, encodes and checks containment against them that way
/// (SNAP-03); <see cref="DeleteSet"/> and the visibility checks use them sorted and merged.
/// </summary>
public sealed class YSnapshot : IEquatable<YSnapshot>
{
    private readonly StateVector _sv;
    // every client's ranges in read order, the clients in the order Yjs's map holds them (first appearance)
    private readonly ulong[] _clients;
    private readonly DeleteRange[][] _ranges;
    private readonly DeleteSet _ds;

    private YSnapshot(StateVector sv, ulong[] clients, DeleteRange[][] ranges)
    {
        _sv = sv;
        _clients = clients;
        _ranges = ranges;
        var merged = new DeleteRanges();
        foreach (var list in ranges)
            foreach (var r in list)
                if (r.Length > 0) merged.Add(r.Client, r.Clock, r.Length);
        _ds = new DeleteSet(merged);
    }

    /// <summary>Yjs <c>snapshot(doc)</c>: the document's delete set (every deleted struct, collected ones included) and state vector.</summary>
    public static YSnapshot Create(Doc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var h = doc.EnterRead();
        try
        {
            var ds = new DeleteRanges();
            doc.AddStoreDeletes(ds);
            return FromMerged(ds, StateVector.Of(doc));
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>Yjs <c>emptySnapshot</c>: no deletes and an empty state vector. Immutable, unlike the JS module singleton (SNAP-04).</summary>
    public static YSnapshot Empty { get; } = new(StateVector.Create(ReadOnlySpan<KeyValuePair<ulong, long>>.Empty), [], []);

    public StateVector StateVector => _sv;

    public DeleteSet DeleteSet => _ds;

    /// <summary>
    /// Yjs <c>encodeSnapshot</c> (V1) and <c>encodeSnapshotV2</c>: the delete set as <c>writeDeleteSet</c> writes it (clients descending, each
    /// client's ranges as held), then the state vector (clients descending), both through the delete-set encoder of the format (SNAP-02).
    /// </summary>
    public void Encode(IBufferWriter<byte> writer, UpdateFormat format = UpdateFormat.V1)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var ordered = InWriteOrder();
        if (format == UpdateFormat.V2)
        {
            using var encoder = new UpdateEncoderV2();
            var e = encoder;
            Write(ref e, ordered);
            encoder.CopyRestTo(writer);
            return;
        }

        using var w = new Lib0Writer(16);
        var v1 = new UpdateEncoderV1(w);
        Write(ref v1, ordered);
        w.CopyTo(writer);
    }

    private void Write<TEncoder>(ref TEncoder encoder, List<DeleteRange> ordered)
        where TEncoder : IUpdateEncoder, allows ref struct
    {
        UpdateWriter.WriteDeleteRuns(ref encoder, ordered);
        encoder.WriteVarUint((ulong)_sv.Count);
        foreach (var (client, clock) in _sv.Clients)
        {
            encoder.WriteVarUint(client);
            encoder.WriteVarUint((ulong)clock);
        }
    }

    /// <summary>
    /// Yjs <c>decodeSnapshot</c> and <c>decodeSnapshotV2</c>: a delete set read with the format's delete-set decoder, then a state vector.
    /// Running out of bytes and values above 2^53-1 are Malformed; trailing bytes are ignored, as in JS. A client listed twice in the delete set
    /// gets the later ranges appended, and a client with no ranges is left out; a client listed twice in the state vector keeps its last clock.
    /// </summary>
    public static YStatus TryDecode(ReadOnlySpan<byte> bytes, UpdateFormat format, out YSnapshot snapshot)
    {
        snapshot = null!;
        var reader = new Lib0Reader(bytes);
        var v2 = format == UpdateFormat.V2;
        if (!reader.TryReadVarUint(out var numClients)) return new YStatus(reader.Error);
        if (numClients > (ulong)reader.Remaining / 2) return new YStatus(YError.Malformed);   // a client entry is at least two bytes
        var clients = new List<ulong>();
        var ranges = new Dictionary<ulong, List<DeleteRange>>();
        for (ulong c = 0; c < numClients; c++)
        {
            if (!reader.TryReadVarUint(out var client) || !reader.TryReadVarUint(out var count)) return new YStatus(reader.Error);
            if (count > (ulong)reader.Remaining / 2) return new YStatus(YError.Malformed);       // so is a range
            if (count == 0) continue;
            if (!ranges.TryGetValue(client, out var list))
            {
                ranges[client] = list = new List<DeleteRange>((int)count);
                clients.Add(client);
            }

            ulong current = 0;   // DSDecoderV2's dsCurrVal, reset for every client
            for (ulong i = 0; i < count; i++)
            {
                if (!reader.TryReadVarUint(out var clock) || !reader.TryReadVarUint(out var length)) return new YStatus(reader.Error);
                if (clock > UpdateReader.MaxClock || length > UpdateReader.MaxClock) return new YStatus(YError.Malformed);
                if (v2)
                {
                    clock += current;
                    length += 1;
                    current = clock + length;
                }

                if (clock > UpdateReader.MaxClock || length > UpdateReader.MaxClock - clock) return new YStatus(YError.Malformed);
                list.Add(new DeleteRange(client, (long)clock, (long)length));
            }
        }

        if (!reader.TryReadVarUint(out var svCount)) return new YStatus(reader.Error);
        if (svCount > (ulong)reader.Remaining / 2) return new YStatus(YError.Malformed);
        var sv = new KeyValuePair<ulong, long>[(int)svCount];
        for (var i = 0; i < sv.Length; i++)
        {
            if (!reader.TryReadVarUint(out var client) || !reader.TryReadVarUint(out var clock)) return new YStatus(reader.Error);
            if (clock > UpdateReader.MaxClock) return new YStatus(YError.Malformed);
            sv[i] = new KeyValuePair<ulong, long>(client, (long)clock);
        }

        var lists = new DeleteRange[clients.Count][];
        for (var i = 0; i < lists.Length; i++) lists[i] = ranges[clients[i]].ToArray();
        snapshot = new YSnapshot(StateVector.Create(sv), clients.ToArray(), lists);
        return default;
    }

    /// <summary>
    /// Yjs <c>equalSnapshots</c>: the same state vector entries, and for every client the same delete ranges in the same order. Nothing is
    /// normalised, so two runs that touch describe a different snapshot from the one run they merge into (SNAP-03).
    /// </summary>
    public bool Equals(YSnapshot? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (_sv.Count != other._sv.Count || _clients.Length != other._clients.Length) return false;
        foreach (var (client, clock) in _sv.Clients)
            if (!other._sv.TryGetClock(client, out var c) || c != clock) return false;
        for (var i = 0; i < _clients.Length; i++)
            if (!SameRanges(_ranges[i], other.RangesOf(_clients[i]))) return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as YSnapshot);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (client, clock) in _sv.Clients)
        {
            hash.Add(client);
            hash.Add(clock);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Yjs <c>snapshotContainsUpdate</c> and <c>snapshotContainsUpdateV2</c>: every struct of the update ends within the state vector, and
    /// merging the update's delete set into the snapshot's changes none of the snapshot's ranges (SNAP-10). A malformed update throws
    /// <see cref="YDecodeException"/>.
    /// </summary>
    public bool ContainsUpdate(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1)
    {
        var error = YUpdate.DecodeForFunctions(update, format, YLimits.Default, out var refs, out var structs);
        if (error != YError.None) throw new YDecodeException(error);
        foreach (var s in structs)
            if (_sv[s.Client] < s.End) return false;

        // mergeDeleteSets([snapshot.ds, updateDs]) then equalDeleteSets(snapshot.ds, merged): the update may add no client and change no run
        var added = new Dictionary<ulong, List<DeleteRange>>();
        foreach (var r in refs.Deletes)
        {
            if (RangesOf(r.Client).Length == 0) return false;
            if (!added.TryGetValue(r.Client, out var list)) added[r.Client] = list = new List<DeleteRange>();
            list.Add(r);
        }

        foreach (var (client, list) in added)
        {
            var own = RangesOf(client);
            var all = new List<DeleteRange>(own.Length + list.Count);
            all.AddRange(own);
            all.AddRange(list);
            if (!SameRanges(own, SortAndMerge(all))) return false;
        }

        return true;
    }

    /// <summary>
    /// Yjs <c>createDocFromSnapshot</c>: writes, inside a transaction on <paramref name="origin"/>, every struct of each client the state vector
    /// lists with a clock above 0 up to that clock (splitting the struct the clock falls in) and the snapshot's delete set, as one V2 update, and
    /// applies it to <paramref name="target"/> (a new document by default) with the origin <c>"snapshot"</c> (SNAP-08, SNAP-09). The origin must
    /// have garbage collection off, since a collected struct's content cannot be restored.
    /// </summary>
    public static Doc CreateDoc(Doc origin, YSnapshot snapshot, Doc? target = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (origin.Gc) throw new InvalidOperationException("Garbage-collection must be disabled in the origin document.");
        target ??= new Doc();
        byte[] update;
        var h = origin.EnterWrite(null, local: true);
        try
        {
            var store = origin.Store;
            using var encoder = new UpdateEncoderV2();
            var e = encoder;
            var written = 0;
            foreach (var (_, clock) in snapshot._sv.Clients)
                if (clock > 0) written++;
            e.WriteVarUint((ulong)written);
            foreach (var (client, clock) in snapshot._sv.Clients)
            {
                if (clock == 0) continue;
                var state = store.GetState(client);
                // JS fails here too: findIndexSS finds no struct at clock - 1
                if (clock > state) throw new InvalidOperationException($"The snapshot reaches clock {clock} of client {client}, past the origin document's state {state}.");
                if (clock < state) store.GetItemCleanStart(client, clock);
                var last = store.FindIndex(client, clock - 1);
                e.WriteVarUint((ulong)(last + 1));
                e.WriteClient(client);
                e.WriteVarUint(0);
                for (var i = 0; i <= last; i++) UpdateWriter.WriteStruct(ref e, origin, store.StructAt(client, i), 0);
            }

            UpdateWriter.WriteDeleteRuns(ref e, snapshot.InWriteOrder());
            var buffer = new ArrayBufferWriter<byte>();
            encoder.CopyTo(buffer);
            update = buffer.WrittenSpan.ToArray();
        }
        finally
        {
            origin.Leave(h);
        }

        target.ApplyUpdate(update, "snapshot", UpdateFormat.V2);
        return target;
    }

    // ------------------------------------------------------------------ reads

    /// <summary>Yjs <c>isVisible</c>: the item existed when the snapshot was taken and was not deleted by then.</summary>
    internal bool IsVisible(ulong client, long clock) => _sv.TryGetClock(client, out var c) && c > clock && !_ds.Contains(new ItemId(client, clock));

    /// <summary>Whether the state vector lists <paramref name="client"/> with a clock above <paramref name="clock"/> (the walk-back test of Yjs <c>typeMapGetSnapshot</c>).</summary>
    internal bool Covers(ulong client, long clock) => _sv.TryGetClock(client, out var c) && clock < c;

    /// <summary>
    /// Yjs <c>splitSnapshotAffectedStructs</c>: once per snapshot per write transaction, splits the structs at every state vector clock below the
    /// document's state and at the bounds of every delete range, so each item is wholly visible or not. The caller holds the write transaction.
    /// </summary>
    internal void SplitAffectedStructs(TxState tx)
    {
        var done = tx.SplitSnapshots ??= new HashSet<YSnapshot>(ReferenceEqualityComparer.Instance);
        if (!done.Add(this)) return;
        var store = tx.Doc.Store;
        foreach (var (client, clock) in _sv.Clients)
            if (clock < store.GetState(client)) store.GetItemCleanStart(client, clock);

        // iterateDeletedStructs: each client's ranges in order, stopping at the first that starts past the client's state
        for (var c = 0; c < _clients.Length; c++)
        {
            var client = _clients[c];
            if (store.StructCount(client) == 0) continue;
            var state = store.GetState(client);
            foreach (var r in _ranges[c])
            {
                if (r.Clock >= state) break;
                if (r.Length == 0) continue;
                // iterateStructs: a clean start at the range's first clock and a clean end at its last
                var index = store.FindIndexCleanStart(client, r.Clock);
                ItemRef s;
                do
                {
                    s = store.StructAt(client, index++);
                    if (r.End < s.Clock + s.Length) store.FindIndexCleanStart(client, r.End);
                } while (index < store.StructCount(client) && store.StructAt(client, index).Clock < r.End);
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static YSnapshot FromMerged(DeleteRanges ds, StateVector sv)
    {
        var clients = new List<ulong>();
        var lists = new List<DeleteRange[]>();
        var start = 0;
        while (start < ds.Count)
        {
            var end = start + 1;
            while (end < ds.Count && ds[end].Client == ds[start].Client) end++;
            var list = new DeleteRange[end - start];
            for (var i = start; i < end; i++) list[i - start] = ds[i];
            clients.Add(ds[start].Client);
            lists.Add(list);
            start = end;
        }

        return new YSnapshot(sv, clients.ToArray(), lists.ToArray());
    }

    private DeleteRange[] RangesOf(ulong client)
    {
        for (var i = 0; i < _clients.Length; i++)
            if (_clients[i] == client) return _ranges[i];
        return [];
    }

    /// <summary>Every range, clients descending (Yjs <c>writeDeleteSet</c>), each client's ranges as held.</summary>
    private List<DeleteRange> InWriteOrder()
    {
        var order = new int[_clients.Length];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => _clients[b].CompareTo(_clients[a]));
        var result = new List<DeleteRange>();
        foreach (var i in order) result.AddRange(_ranges[i]);
        return result;
    }

    private static bool SameRanges(DeleteRange[] a, IReadOnlyList<DeleteRange> b)
    {
        if (a.Length != b.Count) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i].Clock != b[i].Clock || a[i].Length != b[i].Length) return false;
        return true;
    }

    /// <summary>Yjs <c>sortAndMergeDeleteSet</c> for one client: a stable sort by clock, then each range that reaches the next absorbs it.</summary>
    private static List<DeleteRange> SortAndMerge(List<DeleteRange> dels)
    {
        var result = new List<DeleteRange>(dels.Count);
        foreach (var right in System.Linq.Enumerable.OrderBy(dels, d => d.Clock))
        {
            if (result.Count > 0 && result[^1].Clock + result[^1].Length >= right.Clock)
            {
                var left = result[^1];
                result[^1] = left with { Length = Math.Max(left.Length, right.Clock + right.Length - left.Clock) };
            }
            else
            {
                result.Add(right);
            }
        }

        return result;
    }
}
