#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- sync + auth protocols

public enum SyncMessageType : byte { Step1 = 0, Step2 = 1, Update = 2 }

public readonly struct SyncReadResult
{
    public SyncMessageType Type { get; }
    public ApplyResult Apply { get; }      // meaningful for Step2/Update; Ok for Step1
    public bool ReplyWritten { get; }      // Step1 appends a Step2 reply (SY-12)
}

public static class YSync
{
    public static void WriteStep1(IBufferWriter<byte> writer, Doc doc) => throw Sketch.NI;
    public static void WriteStep2(IBufferWriter<byte> writer, Doc doc, ReadOnlySpan<byte> targetStateVector = default) => throw Sketch.NI;
    public static void WriteUpdate(IBufferWriter<byte> writer, ReadOnlySpan<byte> update) => throw Sketch.NI;
    /// Reads one message from the front of `input`; unknown type or truncation is an error result, never an exception (ADR 0005).
    public static YStatus TryReadMessage(ref ReadOnlySpan<byte> input, IBufferWriter<byte> reply, Doc doc, object? origin, out SyncReadResult result, Action<YError>? errorHandler = null) { result = default; throw Sketch.NI; }
}

