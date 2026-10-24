#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- relative positions

public readonly record struct RelativePosition(ItemId? Item, string? TypeName, ItemId? Type, int Assoc)
{
    public void Encode(IBufferWriter<byte> writer) => throw Sketch.NI;
    public static YStatus TryDecode(ReadOnlySpan<byte> bytes, out RelativePosition position) { position = default; throw Sketch.NI; }
    public YValue ToJson() => throw Sketch.NI;
    public static YStatus TryFromJson(in YValue json, out RelativePosition position) { position = default; throw Sketch.NI; }
    /// null when the anchor is unknown or collected. REVIEW: tname anchors create a placeholder root type in JS (RP-06); here they do not.
    public AbsolutePosition? Resolve(Doc doc, bool followUndoneDeletions = true) => throw Sketch.NI;
}
public readonly record struct AbsolutePosition(YType Type, int Index, int Assoc);

