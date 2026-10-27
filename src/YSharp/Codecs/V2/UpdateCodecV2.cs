#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using YSharp.Integration;
using YSharp.Store;

namespace YSharp.Codecs.V2;

/// <summary>
/// The V2 entry points the format-independent code calls (ADR 0021): the V2 adapters behind the same reader and writer as V1.
/// </summary>
internal static class UpdateCodecV2
{
    /// <summary>Decodes and validates a whole V2 update.</summary>
    public static YError Decode(ReadOnlySpan<byte> update, YLimits limits, out UpdateRefs refs)
    {
        refs = null!;
        var decoder = new UpdateDecoderV2(update, limits);
        if (decoder.Error != YError.None) return decoder.Error;
        return UpdateReader.Read(ref decoder, limits, out refs);
    }

    /// <summary>Writes the document's state beyond <paramref name="target"/> as V2, merging pending state as the V1 path does. The caller holds the lock.</summary>
    public static void EncodeState(Doc doc, StateVector? target, IBufferWriter<byte> writer)
    {
        using var encoder = new UpdateEncoderV2();
        var e = encoder;
        if (doc.Pending is null && doc.PendingDeletes is null)
        {
            UpdateWriter.WriteClientsStructs(ref e, doc, target);
            UpdateWriter.WriteStoreDeleteSet(ref e, doc.Store);
        }
        else
        {
            var (structs, deletes) = doc.MergeWithPending(target);
            UpdateWriter.WriteBlocks(ref e, structs);
            UpdateWriter.WriteDeleteSet(ref e, deletes);
        }

        encoder.CopyTo(writer);
    }

    /// <summary>
    /// Writes a delete set as V2 (Yjs <c>writeDeleteSet</c> on a <c>DSEncoderV2</c>, then its <c>toUint8Array</c>): the rest buffer alone, with
    /// no flag or columns, as V1 writes the delete set alone.
    /// </summary>
    public static void EncodeDeleteSet(DeleteSet deleteSet, IBufferWriter<byte> writer)
    {
        using var encoder = new UpdateEncoderV2();
        var e = encoder;
        UpdateWriter.WriteDeleteSet(ref e, deleteSet.Ranges);
        encoder.CopyRestTo(writer);
    }

    /// <summary>Decodes a whole V2 update the way the update functions read it (exact Skips, structs in read order).</summary>
    public static YError DecodeForFunctions(ReadOnlySpan<byte> update, YLimits limits, out UpdateRefs refs, List<StructRef> inReadOrder,
        List<(ulong Client, int Start)>? blocks = null)
    {
        refs = null!;
        var decoder = new UpdateDecoderV2(update, limits);
        if (decoder.Error != YError.None) return decoder.Error;
        return UpdateReader.Read(ref decoder, limits, out refs, exactSkip: true, inReadOrder: inReadOrder, blocks: blocks);
    }

    /// <summary>Writes an update function's result as V2: the structs in client blocks, then the delete ranges (already in write order).</summary>
    public static void EncodeFunctionOutput(List<StructRef> structs, List<DeleteRange> deletes, IBufferWriter<byte> output)
    {
        using var encoder = new UpdateEncoderV2();
        var e = encoder;
        UpdateWriter.WriteBlocks(ref e, structs);
        UpdateWriter.WriteDeleteRuns(ref e, deletes);
        encoder.CopyTo(output);
    }
}
