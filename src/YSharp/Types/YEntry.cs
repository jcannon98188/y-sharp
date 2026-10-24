#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public enum YEntryKind { Any, Binary, Type, Doc }

/// What can sit in a list or map position. Binary vs Any is wire-visible (ContentBinary vs ContentAny).
public readonly struct YEntry
{
    public YEntryKind Kind => throw Sketch.NI;
    public YValue Value => throw Sketch.NI;            // Any: the value; Binary: YValue Bytes
    public YType? Type => throw Sketch.NI;
    public Doc? Doc => throw Sketch.NI;
    public static implicit operator YEntry(YValue v) => throw Sketch.NI;   // REVIEW: top-level Bytes becomes ContentBinary, as in JS (T-0.10)
    public static implicit operator YEntry(YType t) => throw Sketch.NI;    // prelim or live; already integrated elsewhere throws (T-0.13)
    public static implicit operator YEntry(Doc d) => throw Sketch.NI;
    public static implicit operator YEntry(string s) => throw Sketch.NI;
    public static implicit operator YEntry(double d) => throw Sketch.NI;
    public static implicit operator YEntry(bool b) => throw Sketch.NI;
}

