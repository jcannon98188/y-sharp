#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using YSharp.Codecs;
using YSharp.Integration;
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
/// <para>Every member is safe to call from any thread. The relay has its own lock and never takes a document lock.</para>
/// </summary>
public sealed class YRelay
{
    private readonly object _gate = new();   // a plain monitor, as ADR 0006 CC-5 requires of every lock in the library
    private readonly DeleteRanges _deletes = new();
    private List<StructRef> _structs = new();
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
    /// accepted. An invalid or over-limit update returns its error and changes nothing.
    /// </summary>
    public YStatus TryApplyUpdate(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1)
    {
        var error = YUpdate.DecodeForFunctions(update, format, Limits, out var refs, out var structs);
        if (error != YError.None) return new YStatus(error);
        lock (_gate)
        {
            if (structs.Count > 0)
            {
                _structs = UpdateMerge.Merge(new[] { _structs, structs });
                _stateVector = StateVectorOf(_structs);
            }

            foreach (var d in refs.Deletes) _deletes.Add(d.Client, d.Clock, d.Length);
        }

        return default;
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

    private static StateVector StateVectorOf(List<StructRef> structs)
    {
        var entries = YUpdate.StateVectorEntries(structs);
        var pairs = new KeyValuePair<ulong, long>[entries.Count];
        for (var i = 0; i < pairs.Length; i++) pairs[i] = new KeyValuePair<ulong, long>(entries[i].Client, entries[i].Clock);
        return StateVector.Create(pairs);
    }
}
