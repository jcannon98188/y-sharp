// PROTOTYPE (TASK-1.14): throwaway API sketch. Nothing is implemented.
#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

internal static class Sketch { public static Exception NI => new NotImplementedException(); }

// ---------------------------------------------------------------- errors (ADR 0005)

public enum YError { None, Malformed, LimitExceeded, Rejected, UnknownMessage }

/// Result of a non-throwing operation that does not apply updates.
public readonly struct YStatus { public YError Error { get; } public bool Ok => Error == YError.None; }

public readonly struct ApplyResult
{
    public YError Error { get; }
    public bool Ok => Error == YError.None;
    public int RejectedStructs { get; }
    public int ObserverFaults { get; }
}

public class YException : Exception { }
public sealed class YDecodeException : YException { public YError Error { get; } }
public sealed class YConcurrencyException : YException { }   // lock-protocol misuse: read-to-write upgrade (CC-3), lock-order violation (CC-18)
/// Use of a pooled or live view after the transaction or dispatch that produced it ended (CC-9): a Transaction, YEvent, change collection or
/// enumerator taken inside an explicit transaction. Thrown by every member except IsValid; never returns old contents (ADR 0005, 0006).
public sealed class YLifetimeException : YException { }
public sealed class YInternalException : YException { }
public sealed class YTypeConflictException : YException { }   // doc.Get(name) with a different concrete type

/// Decode limits (ADR 0005). A limit is exceeded when the value is greater than the limit: a value equal to the limit is accepted, one more is
/// LimitExceeded. Each is checked against the length claim before anything is allocated, on every decode path (apply, merge, diff, convert,
/// state vector, validate; V1 and every V2 column), and a rejection leaves the document unchanged.
public sealed record YLimits
{
    public static YLimits Default { get; } = new();
    public int MaxUpdateBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxAnyDepth { get; init; } = 128;
    public int MaxStructsPerUpdate { get; init; } = 10_000_000;
    public int MaxPendingBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxStringBytes { get; init; } = 64 * 1024 * 1024;
}

public enum UpdateFormat { V1, V2 }

// ---------------------------------------------------------------- values (ADR 0002, 0007)

public enum YValueKind { Undefined, Null, Bool, Number, BigInt, String, Bytes, Array, Object }

/// 16 bytes, immutable. default(YValue) is Undefined.
public readonly struct YValue : IEquatable<YValue>
{
    public static YValue Undefined => default;
    public static YValue Null => throw Sketch.NI;
    public YValueKind Kind => throw Sketch.NI;

    public static YValue BigInt(long v) => throw Sketch.NI;               // never implicit
    public static YValue Number(ulong v) => throw Sketch.NI;               // errors above 2^53
    public static YValue Bytes(ReadOnlySpan<byte> copy) => throw Sketch.NI;
    public static YValue BytesOwned(byte[] transfer) => throw Sketch.NI;
    public static YValue Array(ReadOnlySpan<YValue> items) => throw Sketch.NI;
    public static YValue Object(ReadOnlySpan<KeyValuePair<string, YValue>> members) => throw Sketch.NI;

    public static implicit operator YValue(bool v) => throw Sketch.NI;
    public static implicit operator YValue(double v) => throw Sketch.NI;
    public static implicit operator YValue(int v) => throw Sketch.NI;
    public static implicit operator YValue(uint v) => throw Sketch.NI;
    public static implicit operator YValue(string? v) => throw Sketch.NI;   // null => YValue.Null

    // Primary access is non-throwing; As* throws InvalidOperationException (test convenience).
    public bool TryGetBool(out bool v) => throw Sketch.NI;
    public bool TryGetNumber(out double v) => throw Sketch.NI;
    public bool TryGetBigInt(out long v) => throw Sketch.NI;
    public bool TryGetString(out string v) { v = ""; throw Sketch.NI; }
    public bool TryGetBytes(out ReadOnlySpan<byte> v) { v = default; throw Sketch.NI; }
    public bool TryGetArray(out ReadOnlySpan<YValue> v) { v = default; throw Sketch.NI; }
    public bool TryGetObject(out YObjectView v) { v = default; throw Sketch.NI; }
    public bool AsBool() => throw Sketch.NI;
    public double AsNumber() => throw Sketch.NI;
    public string AsString() => throw Sketch.NI;

    public bool Equals(YValue other) => throw Sketch.NI;                    // wire equality
    public override bool Equals(object? o) => o is YValue v && Equals(v);
    public override int GetHashCode() => throw Sketch.NI;
    public static bool operator ==(YValue a, YValue b) => a.Equals(b);
    public static bool operator !=(YValue a, YValue b) => !a.Equals(b);
    public bool AttributeEquals(in YValue other) => throw Sketch.NI;        // YText format rule (Q27)

    /// JS-compatible JSON (Q30). Errors for top-level Undefined/BigInt.
    public YStatus TryWriteJson(IBufferWriter<byte> writer) => throw Sketch.NI;
    public static YStatus TryParseJson(ReadOnlySpan<byte> utf8, in YLimits? limits, out YValue value) { value = default; throw Sketch.NI; }
}

/// Members in insertion order. JS array-index-first ordering is applied only on encode/JSON.
public readonly struct YObjectView
{
    public int Count => throw Sketch.NI;
    public KeyValuePair<string, YValue> this[int i] => throw Sketch.NI;
    public bool TryGetValue(string key, out YValue v) { v = default; throw Sketch.NI; }
}

// ---------------------------------------------------------------- ids, state vectors, delete sets

public readonly record struct ItemId(ulong Client, long Clock);   // client ids are 53-bit capable (ADR 0008, Q12); clock <= 2^53-1, so long

public sealed class StateVector
{
    public static StateVector Decode(ReadOnlySpan<byte> v1) => throw Sketch.NI;
    public static StateVector Of(Doc doc) => throw Sketch.NI;
    public long this[ulong client] => throw Sketch.NI;            // 0 when unknown
    public IEnumerable<KeyValuePair<ulong, long>> Clients => throw Sketch.NI;
    public void Encode(IBufferWriter<byte> writer) => throw Sketch.NI;
}

public sealed class DeleteSet
{
    public bool Contains(ItemId id) => throw Sketch.NI;
    public void Encode(IBufferWriter<byte> writer, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
}

// ---------------------------------------------------------------- transactions (ADR 0006)

public sealed class Transaction
{
    public Doc Doc => throw Sketch.NI;
    public object? Origin => throw Sketch.NI;
    public bool IsLocal => throw Sketch.NI;
    public bool IsWrite => throw Sketch.NI;
    public StateVector BeforeState => throw Sketch.NI;
    public StateVector AfterState => throw Sketch.NI;
    public DeleteSet DeleteSet => throw Sketch.NI;
    public IReadOnlyCollection<YType> ChangedTypes => throw Sketch.NI;
    public IReadOnlyCollection<YType> ChangedParentTypes => throw Sketch.NI;
    public bool IsValid => throw Sketch.NI;       // false once the transaction ended (CC-9): the object is pooled. Every other member, and the collections it returned, throw YLifetimeException then
}

/// using-form of a transaction. ref struct so it cannot cross await; thread-affine (CC-15).
public ref struct TransactionScope
{
    public Transaction Transaction => throw Sketch.NI;
    public void Dispose() => throw Sketch.NI;
}

public enum TryTransactStatus { Acquired, Contended }
public readonly struct TryTransactResult { public TryTransactStatus Status { get; } public bool Acquired => Status == TryTransactStatus.Acquired; }
