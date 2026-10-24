#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- snapshots

public sealed class YSnapshot : IEquatable<YSnapshot>
{
    public static YSnapshot Create(Doc doc) => throw Sketch.NI;
    public static YSnapshot Empty => throw Sketch.NI;   // immutable, unlike the JS module singleton (SNAP-04)
    public StateVector StateVector => throw Sketch.NI;
    public DeleteSet DeleteSet => throw Sketch.NI;
    public void Encode(IBufferWriter<byte> writer, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
    public static YStatus TryDecode(ReadOnlySpan<byte> bytes, UpdateFormat format, out YSnapshot snapshot) { snapshot = null!; throw Sketch.NI; }
    public bool Equals(YSnapshot? other) => throw Sketch.NI;
    public bool ContainsUpdate(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
    public static Doc CreateDoc(Doc origin, YSnapshot snapshot, Doc? target = null) => throw Sketch.NI;   // origin.Gc must be false
}

