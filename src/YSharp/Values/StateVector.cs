#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed class StateVector
{
    public static StateVector Decode(ReadOnlySpan<byte> v1) => throw Sketch.NI;
    public static StateVector Of(Doc doc) => throw Sketch.NI;
    public long this[ulong client] => throw Sketch.NI;            // 0 when unknown
    public IEnumerable<KeyValuePair<ulong, long>> Clients => throw Sketch.NI;
    public void Encode(IBufferWriter<byte> writer) => throw Sketch.NI;
}

