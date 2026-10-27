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

/// <summary>
/// Update functions that need no document (Yjs <c>mergeUpdates</c>, <c>diffUpdate</c>, <c>encodeStateVectorFromUpdate</c>), with the strict decoding
/// of ADR 0005: every input is decoded and validated in full, checked against <see cref="YLimits"/> (default <see cref="YLimits.Default"/>), and
/// nothing is written to <c>output</c> unless the whole operation succeeds.
/// </summary>
public static partial class YUpdate
{
    /// <summary>
    /// Merges updates into one (Yjs <c>mergeUpdates</c>): structs of all inputs, clients descending, gaps filled with Skips and overlaps sliced;
    /// the delete sets merged. A single update is written back unchanged, as Yjs returns it.
    /// </summary>
    public static YStatus TryMerge(IReadOnlyList<ReadOnlyMemory<byte>> updates, IBufferWriter<byte> output, UpdateFormat format = UpdateFormat.V1, YLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(output);
        limits ??= YLimits.Default;
        var sources = new List<List<StructRef>>(updates.Count);
        var deletes = new DeleteRanges();
        foreach (var update in updates)
        {
            var error = DecodeForFunctions(update.Span, format, limits, out var refs, out var structs);
            if (error != YError.None) return new YStatus(error);
            sources.Add(structs);
            foreach (var d in refs.Deletes) deletes.Add(d.Client, d.Clock, d.Length);
        }

        if (updates.Count == 1)
        {
            output.Write(updates[0].Span);
            return default;
        }

        Write(format, UpdateMerge.Merge(sources), InWriteOrder(deletes), output);
        return default;
    }

    /// <summary>
    /// What <paramref name="update"/> holds beyond <paramref name="stateVector"/> (Yjs <c>diffUpdate</c>): a struct the state vector's clock lands
    /// inside is written from that clock; the delete set is kept whole.
    /// </summary>
    public static YStatus TryDiff(ReadOnlySpan<byte> update, ReadOnlySpan<byte> stateVector, IBufferWriter<byte> output, UpdateFormat format = UpdateFormat.V1, YLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        limits ??= YLimits.Default;
        var svStatus = StateVector.TryDecode(stateVector, out var target);
        if (!svStatus.Ok) return svStatus;
        var error = DecodeForFunctions(update, format, limits, out var refs, out var structs);
        if (error != YError.None) return new YStatus(error);
        Write(format, UpdateMerge.Diff(structs, target!), InWriteOrder(refs.Deletes), output);
        return default;
    }

    /// <summary>
    /// The state vector an update brings a document to when applied to an empty one (Yjs <c>encodeStateVectorFromUpdate</c>): per client, the end
    /// of the run of structs that starts at clock 0, up to the first Skip; a client that does not start at 0 is not listed.
    /// </summary>
    public static YStatus TryStateVectorFromUpdate(ReadOnlySpan<byte> update, IBufferWriter<byte> output, UpdateFormat format = UpdateFormat.V1, YLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        limits ??= YLimits.Default;
        var error = DecodeForFunctions(update, format, limits, out _, out var structs);
        if (error != YError.None) return new YStatus(error);

        var entries = StateVectorEntries(structs);
        using var w = new Lib0Writer(16 + entries.Count * 8);
        w.WriteVarUint((ulong)entries.Count);
        foreach (var (client, clock) in entries)
        {
            w.WriteVarUint(client);
            w.WriteVarUint((ulong)clock);
        }

        w.CopyTo(output);
        return default;
    }

    /// <summary>
    /// The state vector entries of decoded structs in update order (clients descending): per client, the end of the run that starts at clock 0,
    /// up to the first Skip. Shared by <see cref="TryStateVectorFromUpdate"/> and <see cref="YRelay"/>.
    /// </summary>
    internal static List<(ulong Client, long Clock)> StateVectorEntries(List<StructRef> structs)
    {
        var entries = new List<(ulong Client, long Clock)>();
        if (structs.Count > 0)
        {
            var currClient = structs[0].Client;
            var stopCounting = structs[0].Clock != 0;   // a client must start at 0
            long currClock = 0;
            foreach (var curr in structs)
            {
                if (curr.Client != currClient)
                {
                    if (currClock != 0) entries.Add((currClient, currClock));
                    currClient = curr.Client;
                    currClock = 0;
                    stopCounting = curr.Clock != 0;
                }

                if (curr.Kind == RefKind.Skip) stopCounting = true;
                if (!stopCounting) currClock = curr.End;
            }

            if (currClock != 0) entries.Add((currClient, currClock));
        }

        return entries;
    }

    /// <summary>Decodes an update the way the update functions read it: its size checked, Skips only with an exact info byte, structs in read order.</summary>
    internal static YError DecodeForFunctions(ReadOnlySpan<byte> update, UpdateFormat format, YLimits limits, out UpdateRefs refs, out List<StructRef> structs,
        List<(ulong Client, int Start)>? blocks = null)
    {
        structs = new List<StructRef>();
        refs = null!;
        if (update.Length > limits.MaxUpdateBytes) return YError.LimitExceeded;
        if (format == UpdateFormat.V2) return UpdateCodecV2.DecodeForFunctions(update, limits, out refs, structs, blocks);
        var decoder = new UpdateDecoderV1(update, limits);
        return UpdateReader.Read(ref decoder, limits, out refs, exactSkip: true, inReadOrder: structs, blocks: blocks);
    }

    /// <summary>Merged ranges in Yjs <c>writeDeleteSet</c> order: clients descending, each client's ranges by clock.</summary>
    internal static List<DeleteRange> InWriteOrder(DeleteRanges merged)
    {
        var read = new List<DeleteRange>(merged.Count);
        for (var i = 0; i < merged.Count; i++) read.Add(merged[i]);
        return InWriteOrder(read);
    }

    /// <summary>
    /// Ranges as Yjs <c>readDeleteSet</c> then <c>writeDeleteSet</c> pass them through: grouped by client (a repeated client's ranges joined, in
    /// read order, not merged), clients descending.
    /// </summary>
    internal static List<DeleteRange> InWriteOrder(List<DeleteRange> read)
    {
        var byClient = new Dictionary<ulong, List<DeleteRange>>();
        foreach (var r in read)
        {
            if (!byClient.TryGetValue(r.Client, out var list)) byClient[r.Client] = list = new List<DeleteRange>();
            list.Add(r);
        }

        var clients = new List<ulong>(byClient.Keys);
        clients.Sort((a, b) => b.CompareTo(a));
        var ordered = new List<DeleteRange>(read.Count);
        foreach (var client in clients) ordered.AddRange(byClient[client]);
        return ordered;
    }

    internal static void Write(UpdateFormat format, List<StructRef> structs, List<DeleteRange> deletes, IBufferWriter<byte> output)
    {
        if (format == UpdateFormat.V2)
        {
            UpdateCodecV2.EncodeFunctionOutput(structs, deletes, output);
            return;
        }

        using var w = new Lib0Writer(256);
        var encoder = new UpdateEncoderV1(w);
        UpdateWriter.WriteBlocks(ref encoder, structs);
        UpdateWriter.WriteDeleteRuns(ref encoder, deletes);
        w.CopyTo(output);
    }
}
