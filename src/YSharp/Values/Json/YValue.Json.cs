#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public readonly partial struct YValue
{
    /// JS-compatible JSON (Q30). Errors for top-level Undefined/BigInt.
    public YStatus TryWriteJson(IBufferWriter<byte> writer) => throw Sketch.NI;
    public static YStatus TryParseJson(ReadOnlySpan<byte> utf8, in YLimits? limits, out YValue value) { value = default; throw Sketch.NI; }

}
