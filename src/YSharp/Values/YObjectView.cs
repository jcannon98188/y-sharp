#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

/// Members in insertion order. JS array-index-first ordering is applied only on encode/JSON.
public readonly struct YObjectView
{
    public int Count => throw Sketch.NI;
    public KeyValuePair<string, YValue> this[int i] => throw Sketch.NI;
    public bool TryGetValue(string key, out YValue v) { v = default; throw Sketch.NI; }
}

