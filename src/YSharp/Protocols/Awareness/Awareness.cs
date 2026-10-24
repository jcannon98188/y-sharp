#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- awareness (y-protocols)

public readonly struct AwarenessChange
{
    public IReadOnlyList<ulong> Added { get; }
    public IReadOnlyList<ulong> Updated { get; }
    public IReadOnlyList<ulong> Removed { get; }
}

public readonly record struct AwarenessMeta(long Clock, DateTimeOffset LastUpdated);

public delegate void AwarenessHandler(AwarenessChange change, object? origin);   // origin: "local", "timeout" or the caller's

public sealed class Awareness : IDisposable
{
    public static readonly TimeSpan OutdatedTimeout = TimeSpan.FromMilliseconds(30000);
    public Awareness(Doc doc, TimeProvider? timeProvider = null) { }
    public ulong ClientId => throw Sketch.NI;
    public YValue LocalState => throw Sketch.NI;                     // YValue.Null when removed (AW-7)
    public void SetLocalState(in YValue state) => throw Sketch.NI;   // Null removes (AW-8)
    public void SetLocalStateField(string field, in YValue value) => throw Sketch.NI;
    public IReadOnlyDictionary<ulong, YValue> GetStates() => throw Sketch.NI;     // a snapshot, not the live map JS returns (AW-11, AW-13; ADR 0008)
    public IReadOnlyDictionary<ulong, AwarenessMeta> Meta => throw Sketch.NI;
    public event AwarenessHandler? Change, Update;
    public event Action? Destroyed;
    public void Dispose() => throw Sketch.NI;

    public void RemoveStates(ReadOnlySpan<ulong> clients, object? origin) => throw Sketch.NI;
    public void EncodeUpdate(IBufferWriter<byte> writer) => throw Sketch.NI;   // every client with a live state
    /// Entries in the order given, duplicates written twice, an empty list is the single byte 0 (AW-31). An id with no meta fails (AW-32, ADR 0014).
    /// `states` substitutes the state map but not the meta (AW-33).
    public YStatus TryEncodeUpdate(IBufferWriter<byte> writer, ReadOnlySpan<ulong> clients, IReadOnlyDictionary<ulong, YValue>? states = null) => throw Sketch.NI;
    public ApplyResult TryApplyUpdate(ReadOnlySpan<byte> update, object? origin) => throw Sketch.NI;
    public static YStatus TryModifyUpdate(ReadOnlySpan<byte> update, Func<YValue, YValue> modify, IBufferWriter<byte> output) => throw Sketch.NI;
}

