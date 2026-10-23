#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;

namespace YSharp;

// ---------------------------------------------------------------- awareness (y-protocols)

public readonly struct AwarenessChange
{
    public IReadOnlyList<uint> Added { get; }
    public IReadOnlyList<uint> Updated { get; }
    public IReadOnlyList<uint> Removed { get; }
}

public readonly record struct AwarenessMeta(long Clock, DateTimeOffset LastUpdated);

public delegate void AwarenessHandler(AwarenessChange change, object? origin);   // origin: "local", "timeout" or the caller's

public sealed class Awareness : IDisposable
{
    public static readonly TimeSpan OutdatedTimeout = TimeSpan.FromMilliseconds(30000);
    public Awareness(Doc doc, TimeProvider? timeProvider = null) { }
    public uint ClientId => throw Sketch.NI;
    public YValue LocalState => throw Sketch.NI;                     // YValue.Null when removed (AW-7)
    public void SetLocalState(in YValue state) => throw Sketch.NI;   // Null removes (AW-8)
    public void SetLocalStateField(string field, in YValue value) => throw Sketch.NI;
    public IReadOnlyDictionary<uint, YValue> GetStates() => throw Sketch.NI;     // REVIEW: snapshot, not the live map (AW-11, AW-13)
    public IReadOnlyDictionary<uint, AwarenessMeta> Meta => throw Sketch.NI;
    public event AwarenessHandler? Change, Update;
    public event Action? Destroyed;
    public void Dispose() => throw Sketch.NI;

    public void RemoveStates(ReadOnlySpan<uint> clients, object? origin) => throw Sketch.NI;
    public void EncodeUpdate(IBufferWriter<byte> writer, ReadOnlySpan<uint> clients = default) => throw Sketch.NI;   // empty = all
    public ApplyResult TryApplyUpdate(ReadOnlySpan<byte> update, object? origin) => throw Sketch.NI;
    public static YStatus TryModifyUpdate(ReadOnlySpan<byte> update, Func<YValue, YValue> modify, IBufferWriter<byte> output) => throw Sketch.NI;
}

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
    public static YStatus TryReadMessage(ref ReadOnlySpan<byte> input, IBufferWriter<byte> reply, Doc doc, object? origin, out SyncReadResult result) { result = default; throw Sketch.NI; }
}

public static class YAuth
{
    public static void WritePermissionDenied(IBufferWriter<byte> writer, string reason) => throw Sketch.NI;
    public static YStatus TryReadMessage(ref ReadOnlySpan<byte> input, Doc doc, Action<Doc, string> permissionDeniedHandler) => throw Sketch.NI;
}
