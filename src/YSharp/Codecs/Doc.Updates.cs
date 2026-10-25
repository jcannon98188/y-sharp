#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using YSharp.Codecs;
using YSharp.Codecs.V1;
using YSharp.Codecs.V2;
using YSharp.Integration;
using YSharp.Lib0;
using YSharp.Store;

namespace YSharp;

// Applying and encoding updates (TASK-4.14, ADR 0005): the whole update is decoded and validated before anything is integrated, then the integrator
// (spec 13) takes the decoded structs. Encoding writes the store through the codec seam; with pending state it merges the state with the pending
// structs and delete set, as Yjs encodeStateAsUpdate does.
public sealed partial class Doc
{
    // ---- internal test seams (ADR 0015), reached through InternalsVisibleTo; the TestKit and interop projects cannot see them.

    /// The ADR 0016 `structs` dump as JSON: an array of [client, [struct, ...]], clients descending, structs in clock order (Spec 6).
    internal string DumpStructs()
    {
        var h = EnterRead();
        try
        {
            return StructDump.Write(this);
        }
        finally
        {
            Leave(h);
        }
    }

    /// True while pending structs or a pending delete set are held (JS store.pendingStructs !== null || store.pendingDs !== null).
    internal bool HasPending
    {
        get
        {
            var h = EnterRead();
            try
            {
                return Pending is not null || PendingDeletes is not null;
            }
            finally
            {
                Leave(h);
            }
        }
    }

    // ---- updates (ADR 0005 pair: Try* never throws on bad bytes)

    /// <summary>
    /// Applies an update. A malformed update or one over a limit (the pending state it would leave included) changes nothing and returns <see cref="YError.Malformed"/> or
    /// <see cref="YError.LimitExceeded"/>; structs that cannot be integrated are rejected with their dependents and counted, the rest applies, and
    /// the result is <see cref="YError.Rejected"/>. A faulted document returns <see cref="YError.Rejected"/> and changes nothing.
    /// </summary>
    public ApplyResult TryApplyUpdate(ReadOnlySpan<byte> update, object? origin = null, UpdateFormat format = UpdateFormat.V1)
    {
        if (IsFaulted) return new ApplyResult(YError.Rejected, 0, 0);
        var limits = Limits;
        if (update.Length > limits.MaxUpdateBytes) return new ApplyResult(YError.LimitExceeded, 0, 0);
        var error = Decode(update, format, limits, out var refs);
        if (error != YError.None) return new ApplyResult(error, 0, 0);
        if (!TryIntegrateRemote(refs, origin, limits.MaxPendingBytes, out var rejected)) return new ApplyResult(YError.LimitExceeded, 0, 0);
        return new ApplyResult(rejected > 0 ? YError.Rejected : YError.None, rejected, 0);
    }

    /// <summary>The throwing form of <see cref="TryApplyUpdate"/>: <see cref="YDecodeException"/> with the same code, <see cref="YInternalException"/> on a faulted document.</summary>
    public void ApplyUpdate(ReadOnlySpan<byte> update, object? origin = null, UpdateFormat format = UpdateFormat.V1)
    {
        ThrowIfFaulted();
        var result = TryApplyUpdate(update, origin, format);
        if (!result.Ok) throw new YDecodeException(result.Error);
    }

    /// <summary>Decodes and validates a whole update (ADR 0005, atomic decode).</summary>
    internal static YError Decode(ReadOnlySpan<byte> update, UpdateFormat format, YLimits limits, out UpdateRefs refs)
    {
        if (format == UpdateFormat.V2) return UpdateCodecV2.Decode(update, limits, out refs);
        var decoder = new UpdateDecoderV1(update, limits);
        return UpdateReader.Read(ref decoder, limits, out refs);
    }

    /// <summary>
    /// Writes what this document holds beyond <paramref name="targetStateVector"/> (a V1 state vector; empty means everything), followed by the
    /// whole delete set. Throws <see cref="YDecodeException"/> when the state vector is malformed.
    /// </summary>
    public void EncodeStateAsUpdate(IBufferWriter<byte> writer, ReadOnlySpan<byte> targetStateVector = default, UpdateFormat format = UpdateFormat.V1)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var target = targetStateVector.IsEmpty ? null : StateVector.Decode(targetStateVector);
        var h = EnterRead();
        try
        {
            if (format == UpdateFormat.V2)
            {
                UpdateCodecV2.EncodeState(this, target, writer);
                return;
            }

            using var w = Lib0Writer.Rent(256);
            var encoder = new UpdateEncoderV1(w);
            if (Pending is null && PendingDeletes is null)
            {
                UpdateWriter.WriteClientsStructs(ref encoder, this, target);
                UpdateWriter.WriteStoreDeleteSet(ref encoder, Store);
            }
            else
            {
                var (structs, deletes) = MergeWithPending(target);
                UpdateWriter.WriteBlocks(ref encoder, structs);
                UpdateWriter.WriteDeleteSet(ref encoder, deletes);
            }

            w.CopyTo(writer);
        }
        finally
        {
            Leave(h);
        }
    }

    public byte[] EncodeStateAsUpdate(ReadOnlySpan<byte> targetStateVector = default, UpdateFormat format = UpdateFormat.V1)
    {
        var writer = new ArrayBufferWriter<byte>();
        EncodeStateAsUpdate(writer, targetStateVector, format);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The state merged with the pending structs and the pending delete set (Yjs <c>encodeStateAsUpdateV2</c>: the state update, the pending delete
    /// set and the pending structs diffed against the target, merged with <c>mergeUpdates</c>). The caller holds the lock.
    /// </summary>
    internal (List<StructRef> Structs, DeleteRanges Deletes) MergeWithPending(StateVector? target)
    {
        var sources = new List<List<StructRef>> { UpdateMerge.StateStructs(this, target) };
        if (Pending is { } pending)
        {
            // the pending update lists its clients in descending order, as Yjs writeClientsStructs writes it
            var clients = new List<List<StructRef>>();
            foreach (var (_, refs) in pending.Clients) clients.Add(refs);
            var flat = new List<StructRef>();
            for (var i = clients.Count - 1; i >= 0; i--) flat.AddRange(clients[i]);
            sources.Add(UpdateMerge.Diff(flat, target ?? StateVector.Create(default)));
        }

        var deletes = new DeleteRanges();
        AddStoreDeletes(deletes);
        if (PendingDeletes is { } pendingDs)
        {
            for (var i = 0; i < pendingDs.Count; i++) deletes.Add(pendingDs[i].Client, pendingDs[i].Clock, pendingDs[i].Length);
        }

        return (UpdateMerge.Merge(sources), deletes);
    }

    /// <summary>Yjs <c>createDeleteSetFromStructStore</c>: every deleted struct, GC structs included.</summary>
    internal void AddStoreDeletes(DeleteRanges deletes)
    {
        var store = Store;
        for (var rank = 0; rank < store.ClientCount; rank++)
        {
            var client = store.ClientIdAtRank(rank);
            var count = store.StructCountAtRank(rank);
            for (var i = 0; i < count; i++)
            {
                var s = store.StructAtRank(rank, i);
                if (s.IsDeleted) deletes.Add(client, s.Clock, s.Length);
            }
        }
    }

    /// <summary>Writes the state vector (V1; the same bytes serve V2): the count, then each client by descending id with its next clock.</summary>
    public void EncodeStateVector(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var h = EnterRead();
        try
        {
            var store = Store;
            using var w = Lib0Writer.Rent(64);
            var count = 0;
            for (var rank = 0; rank < store.ClientCount; rank++)
                if (store.StructCountAtRank(rank) > 0) count++;
            w.WriteVarUint((ulong)count);
            for (var rank = store.ClientCount - 1; rank >= 0; rank--)
            {
                if (store.StructCountAtRank(rank) == 0) continue;
                w.WriteVarUint(store.ClientIdAtRank(rank));
                w.WriteVarUint((ulong)store.StateAtRank(rank));
            }

            w.CopyTo(writer);
        }
        finally
        {
            Leave(h);
        }
    }

    public StateVector GetStateVector()
    {
        var h = EnterRead();
        try
        {
            var store = Store;
            var entries = new List<KeyValuePair<ulong, long>>();
            for (var rank = 0; rank < store.ClientCount; rank++)
                if (store.StructCountAtRank(rank) > 0) entries.Add(new KeyValuePair<ulong, long>(store.ClientIdAtRank(rank), store.StateAtRank(rank)));
            return StateVector.Create(entries.ToArray());
        }
        finally
        {
            Leave(h);
        }
    }
}
