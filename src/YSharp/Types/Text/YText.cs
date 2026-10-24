#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- YText

public class YText : YType
{
    public YText(string? initial = null) { }
    public int Length => throw Sketch.NI;                                    // UTF-16 code units (Q17)
    public void Insert(int index, string text, in YValue attributes = default) => throw Sketch.NI;   // clamps index, as JS
    public void InsertEmbed(int index, in YValue embed, in YValue attributes = default) => throw Sketch.NI;
    public void InsertEmbed(int index, YType embed, in YValue attributes = default) => throw Sketch.NI;
    public void Delete(int index, int length) => throw Sketch.NI;
    public void Format(int index, int length, in YValue attributes) => throw Sketch.NI;
    public void ApplyDelta(ReadOnlySpan<TextDeltaOp> delta, bool sanitize = true) => throw Sketch.NI;
    public TextDeltaOp[] ToDelta(YSnapshot? snapshot = null, YSnapshot? previous = null, Func<string, ItemId, YValue>? computeYChange = null) => throw Sketch.NI;
    public void CopyTo(Span<char> destination) => throw Sketch.NI;
    public override string ToString() => throw Sketch.NI;
    /// Counterpart of JS cleanupYTextFormatting: the number of redundant format items removed (Spec 10, ADR 0015).
    internal int CleanupFormatting() => throw Sketch.NI;
    // map-side attributes, shared with YXmlText
    public void SetAttribute(string name, in YValue value) => throw Sketch.NI;
    public bool TryGetAttribute(string name, out YValue value) { value = default; throw Sketch.NI; }
    public void RemoveAttribute(string name) => throw Sketch.NI;
    public IReadOnlyDictionary<string, YValue> GetAttributes() => throw Sketch.NI;   // TASK-2.7: live attributes (X-2); YXmlElement has the same method
    public override YType Clone() => throw Sketch.NI;
    public override YValue ToJson() => throw Sketch.NI;                      // plain text for YText (X-T3)
}

