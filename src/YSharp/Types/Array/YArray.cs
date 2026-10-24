#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- YArray

public sealed class YArray : YType
{
    public YArray() { }
    public static YArray From(IEnumerable<YEntry> items) => throw Sketch.NI;
    public int Length => throw Sketch.NI;
    public YEntry this[int index] => throw Sketch.NI;                       // ArgumentOutOfRangeException (REVIEW: JS returns undefined)
    public void Insert(int index, ReadOnlySpan<YEntry> content) => throw Sketch.NI;   // index > Length throws, as JS ("Length exceeded!")
    public void Push(ReadOnlySpan<YEntry> content) => throw Sketch.NI;
    public void Unshift(ReadOnlySpan<YEntry> content) => throw Sketch.NI;
    public void Delete(int index, int length = 1) => throw Sketch.NI;       // partial effect then throws, as JS (A-5)
    public void CopyTo(int start, Span<YEntry> destination) => throw Sketch.NI;
    public YEntry[] ToArray() => throw Sketch.NI;
    public YEntry[] Slice(int start, int? end = null) => throw Sketch.NI;      // JS slice: negative indexes count from the end, an end past Length stops at the end; end defaults to Length (A-8)
    public ListEnumerator GetEnumerator() => throw Sketch.NI;               // live inside an explicit transaction; snapshot otherwise (CC-9)
    public override YType Clone() => throw Sketch.NI;
    public override YValue ToJson() => throw Sketch.NI;
}

public struct ListEnumerator { public YEntry Current => throw Sketch.NI; public bool MoveNext() => throw Sketch.NI; }

