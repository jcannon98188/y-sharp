#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using YSharp.Codecs;
using YSharp.Codecs.V1;
using YSharp.Integration;
using YSharp.Lib0;
using YSharp.Store;

namespace YSharp;

/// <summary>
/// A document held as its merged update, with no <see cref="Doc"/> and no item graph: the relay tier of a server (ADR 0024 SRV-1). It folds
/// updates in as Yjs <c>mergeUpdates</c> would merge them and answers what a peer is missing as Yjs <c>diffUpdate</c> would, so a server can
/// receive, store and fan out updates without integrating them.
/// <para>
/// Every update is decoded and validated in full against <see cref="Limits"/> before anything changes (ADR 0005), with the strict decoding of
/// the update functions (<see cref="YUpdate"/>); a rejected update leaves the relay as it was. The merged state keeps every struct it was given,
/// including the content of deleted items and structs that wait on a gap, which a <see cref="Doc"/> would collect or hold back: a document
/// that applies <see cref="EncodeStateAsUpdate(IBufferWriter{byte}, ReadOnlySpan{byte}, UpdateFormat)"/> ends where one that applied every
/// input ends. The merged state grows with the history; compaction and storage belong to the server tier (ADR 0024 SRV-10).
/// </para>
/// <para>
/// What the relay holds but cannot yet pass on as part of the document is bounded by <see cref="YLimits.MaxPendingBytes"/>, as a
/// <see cref="Doc"/>'s pending state is (ADR 0005): the structs past each client's first gap and the deleted ranges past each client's state,
/// measured as their V1 encoding. An update that would take that above the limit is rejected with LimitExceeded and changes nothing (ADR 0024
/// RLY-7).
/// </para>
/// <para>Every member is safe to call from any thread. The relay has its own lock and never takes a document lock.</para>
/// </summary>
public sealed class YRelay
{
    private readonly object _gate = new();   // a plain monitor, as ADR 0006 CC-5 requires of every lock in the library
    private readonly DeleteRanges _deletes = new();
    private List<StructRef> _structs = new();
    private long _version;
    private StateVector _stateVector = StateVector.Create(ReadOnlySpan<KeyValuePair<ulong, long>>.Empty);

    /// <param name="limits">Limits every incoming update is checked against; <see cref="YLimits.Default"/> when null.</param>
    public YRelay(YLimits? limits = null) => Limits = limits ?? YLimits.Default;

    public YLimits Limits { get; }

    /// <summary>True until an update with a struct or a deleted range has been folded in.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_gate) return _structs.Count == 0 && _deletes.Count == 0;
        }
    }

    /// <summary>
    /// Validates <paramref name="update"/> and folds it into the merged state (Yjs <c>mergeUpdates([state, update])</c>): structs the relay
    /// already holds are dropped, overlaps are sliced, gaps become Skips and the delete sets are merged. A duplicate or out-of-order update is
    /// accepted. An invalid or over-limit update returns its error and changes nothing. The same as <see cref="TryPrepareUpdate"/> then
    /// <see cref="Commit"/>, in one step.
    /// </summary>
    public YStatus TryApplyUpdate(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1)
    {
        var error = YUpdate.DecodeForFunctions(update, format, Limits, out var refs, out var structs);
        if (error != YError.None) return new YStatus(error);
        lock (_gate)
        {
            var prepared = Prepare(refs, structs);
            if (prepared is null) return new YStatus(YError.LimitExceeded);
            Commit(prepared);
        }

        return default;
    }

    /// <summary>
    /// The first half of <see cref="TryApplyUpdate"/>: validates <paramref name="update"/> and computes the merged state it would leave without
    /// changing the relay, so a server can persist the update before the relay serves it (ADR 0024 SRV-8). The update's bytes are not kept.
    /// An invalid or over-limit update returns its error and a null <paramref name="prepared"/>.
    /// </summary>
    public YStatus TryPrepareUpdate(ReadOnlySpan<byte> update, UpdateFormat format, out PreparedUpdate? prepared)
    {
        prepared = null;
        var error = YUpdate.DecodeForFunctions(update, format, Limits, out var refs, out var structs);
        if (error != YError.None) return new YStatus(error);
        lock (_gate) prepared = Prepare(refs, structs);
        return prepared is null ? new YStatus(YError.LimitExceeded) : default;
    }

    /// <summary>
    /// Folds in an update <see cref="TryPrepareUpdate"/> prepared. Throws <see cref="InvalidOperationException"/> when it was prepared by another
    /// relay, was already committed, or was prepared before another update changed this relay: prepare and commit run one update at a time, as
    /// a server's per-document mailbox runs them.
    /// </summary>
    public void Commit(PreparedUpdate prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        lock (_gate)
        {
            if (!ReferenceEquals(prepared.Relay, this)) throw new InvalidOperationException("The update was prepared by another relay.");
            if (prepared.Version != _version) throw new InvalidOperationException("The relay changed after the update was prepared, or the update was already committed.");
            _structs = prepared.Structs;
            _stateVector = prepared.StateVector;
            foreach (var d in prepared.Deletes) _deletes.Add(d.Client, d.Clock, d.Length);
            _version++;
        }
    }

    /// <summary>An update <see cref="TryPrepareUpdate"/> validated and merged, waiting for <see cref="Commit"/>.</summary>
    public sealed class PreparedUpdate
    {
        internal PreparedUpdate(YRelay relay, long version, List<StructRef> structs, StateVector stateVector, List<DeleteRange> deletes)
        {
            Relay = relay;
            Version = version;
            Structs = structs;
            StateVector = stateVector;
            Deletes = deletes;
        }

        /// <summary>The relay's state vector once the update is committed.</summary>
        public StateVector StateVector { get; }

        internal YRelay Relay { get; }

        internal long Version { get; }

        internal List<StructRef> Structs { get; }

        internal List<DeleteRange> Deletes { get; }
    }

    /// <summary>The merged state a decoded update leaves, or null when its pending part would be over the limit (RLY-7). The caller holds the gate.</summary>
    private PreparedUpdate? Prepare(UpdateRefs refs, List<StructRef> structs)
    {
        var merged = structs.Count > 0 ? UpdateMerge.Merge(new[] { _structs, structs }) : _structs;
        var stateVector = structs.Count > 0 ? StateVectorOf(merged) : _stateVector;
        if (PendingV1Bytes(merged, stateVector, _deletes, refs.Deletes) > Limits.MaxPendingBytes) return null;
        return new PreparedUpdate(this, _version, merged, stateVector, refs.Deletes);
    }

    /// <summary>
    /// Writes what the relay holds beyond <paramref name="targetStateVector"/> (a V1 state vector; empty means everything), followed by the
    /// whole delete set, as Yjs <c>diffUpdate</c> writes it for the merged state. Everything is written as merged, Skips included; with a target,
    /// a client's first missing struct is sliced at the target's clock. Returns Malformed, and writes nothing, when the state vector is malformed.
    /// </summary>
    public YStatus TryEncodeStateAsUpdate(IBufferWriter<byte> writer, ReadOnlySpan<byte> targetStateVector = default, UpdateFormat format = UpdateFormat.V1)
    {
        ArgumentNullException.ThrowIfNull(writer);
        StateVector? target = null;
        if (!targetStateVector.IsEmpty)
        {
            var status = StateVector.TryDecode(targetStateVector, out target);
            if (!status.Ok) return status;
        }

        lock (_gate)
        {
            var structs = target is null ? _structs : UpdateMerge.Diff(_structs, target);
            YUpdate.Write(format, structs, YUpdate.InWriteOrder(_deletes), writer);
        }

        return default;
    }

    /// <summary>The throwing <see cref="TryEncodeStateAsUpdate"/>: throws <see cref="YDecodeException"/> when the state vector is malformed.</summary>
    public void EncodeStateAsUpdate(IBufferWriter<byte> writer, ReadOnlySpan<byte> targetStateVector = default, UpdateFormat format = UpdateFormat.V1)
    {
        var status = TryEncodeStateAsUpdate(writer, targetStateVector, format);
        if (!status.Ok) throw new YDecodeException(status.Error);
    }

    public byte[] EncodeStateAsUpdate(ReadOnlySpan<byte> targetStateVector = default, UpdateFormat format = UpdateFormat.V1)
    {
        var writer = new ArrayBufferWriter<byte>();
        EncodeStateAsUpdate(writer, targetStateVector, format);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The state vector of the merged state (Yjs <c>encodeStateVectorFromUpdate</c> of it): per client, the end of the run of structs from
    /// clock 0 up to the first gap. A client whose structs do not start at 0 is not listed. Unlike a <see cref="Doc"/>, the relay counts items
    /// whose origin or parent is still missing, because it holds them and will pass them on.
    /// </summary>
    public StateVector GetStateVector()
    {
        lock (_gate) return _stateVector;
    }

    /// <summary>Writes <see cref="GetStateVector"/> in the V1 encoding (the sync protocol's step 1 payload).</summary>
    public void EncodeStateVector(IBufferWriter<byte> writer) => GetStateVector().Encode(writer);

    /// <summary>
    /// The V1 size of what a merged state holds past each client's state (RLY-7): per client, the structs after its first gap with the Skip
    /// that opens the gap dropped, then the deleted ranges, the held ones and the update's, clipped to their part past the client's state.
    /// </summary>
    private static long PendingV1Bytes(List<StructRef> structs, StateVector stateVector, DeleteRanges held, List<DeleteRange> incoming)
    {
        List<StructRef>? pending = null;
        ulong? client = null;
        foreach (var s in structs)
        {
            if (s.Clock < stateVector[s.Client]) continue;
            if (client != s.Client)
            {
                client = s.Client;
                if (s.Kind == RefKind.Skip) continue;
            }

            (pending ??= new List<StructRef>()).Add(s);
        }

        DeleteRanges? pendingDeletes = null;
        for (var i = 0; i < held.Count; i++) ClipTo(stateVector, held[i], ref pendingDeletes);
        foreach (var r in incoming) ClipTo(stateVector, r, ref pendingDeletes);
        if (pending is null && pendingDeletes is null) return 0;

        using var w = new Lib0Writer(256);
        var encoder = new UpdateEncoderV1(w);
        UpdateWriter.WriteBlocks(ref encoder, pending ?? new List<StructRef>());
        UpdateWriter.WriteDeleteRuns(ref encoder, pendingDeletes is null ? new List<DeleteRange>() : YUpdate.InWriteOrder(pendingDeletes));
        return w.Length;
    }

    private static void ClipTo(StateVector stateVector, DeleteRange range, ref DeleteRanges? clipped)
    {
        var state = stateVector[range.Client];
        if (range.End <= state) return;
        var from = Math.Max(range.Clock, state);
        (clipped ??= new DeleteRanges()).Add(range.Client, from, range.End - from);
    }

    private static StateVector StateVectorOf(List<StructRef> structs)
    {
        var entries = YUpdate.StateVectorEntries(structs);
        var pairs = new KeyValuePair<ulong, long>[entries.Count];
        for (var i = 0; i < pairs.Length; i++) pairs[i] = new KeyValuePair<ulong, long>(entries[i].Client, entries[i].Clock);
        return StateVector.Create(pairs);
    }
}
