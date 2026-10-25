#nullable enable
using System;
using System.Buffers;
using YSharp.Integration;

namespace YSharp.Codecs.V2;

/// <summary>
/// The V2 entry points the format-independent code calls (ADR 0021): the V2 adapters land in spec 18 behind these members, so V1's files need no
/// edit when they do.
/// </summary>
internal static class UpdateCodecV2
{
    /// <summary>Decodes and validates a whole V2 update.</summary>
    public static YError Decode(ReadOnlySpan<byte> update, YLimits limits, out UpdateRefs refs) => throw Sketch.NI;

    /// <summary>Writes the document's state beyond <paramref name="target"/> as V2, merging pending state as the V1 path does. The caller holds the lock.</summary>
    public static void EncodeState(Doc doc, StateVector? target, IBufferWriter<byte> writer) => throw Sketch.NI;

    /// <summary>Writes a delete set as a V2 delete set.</summary>
    public static void EncodeDeleteSet(DeleteSet deleteSet, IBufferWriter<byte> writer) => throw Sketch.NI;

    /// <summary>Decodes a whole V2 update the way the update functions read it (exact Skips, structs in read order).</summary>
    public static YError DecodeForFunctions(ReadOnlySpan<byte> update, YLimits limits, out UpdateRefs refs, System.Collections.Generic.List<StructRef> inReadOrder) =>
        throw Sketch.NI;

    /// <summary>Writes an update function's result as V2: the structs in client blocks, then the delete ranges (already in write order).</summary>
    public static void EncodeFunctionOutput(System.Collections.Generic.List<StructRef> structs, System.Collections.Generic.List<YSharp.Store.DeleteRange> deletes,
        IBufferWriter<byte> output) => throw Sketch.NI;
}
