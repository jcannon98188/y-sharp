#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed partial class Doc
{
    // ---- internal test seams (ADR 0015), reached through InternalsVisibleTo; the TestKit and interop projects cannot see them.
    /// The ADR 0016 `structs` dump as JSON: an array of [client, [struct, ...]], clients descending, structs in clock order (Spec 6).
    internal string DumpStructs() => throw Sketch.NI;
    /// True while pending structs or a pending delete set are held (JS store.pendingStructs !== null || store.pendingDs !== null).
    internal bool HasPending => throw Sketch.NI;

    // ---- updates (ADR 0005 pair: Try* never throws on bad bytes)
    public ApplyResult TryApplyUpdate(ReadOnlySpan<byte> update, object? origin = null, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
    public void ApplyUpdate(ReadOnlySpan<byte> update, object? origin = null, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;   // throws YDecodeException
    public void EncodeStateAsUpdate(IBufferWriter<byte> writer, ReadOnlySpan<byte> targetStateVector = default, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
    public byte[] EncodeStateAsUpdate(ReadOnlySpan<byte> targetStateVector = default, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
    public void EncodeStateVector(IBufferWriter<byte> writer) => throw Sketch.NI;
    public StateVector GetStateVector() => throw Sketch.NI;

}
