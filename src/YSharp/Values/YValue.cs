#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- values (ADR 0002, 0007)

public enum YValueKind { Undefined, Null, Bool, Number, BigInt, String, Bytes, Array, Object }

/// 16 bytes, immutable. default(YValue) is Undefined.
public readonly partial struct YValue : IEquatable<YValue>
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

}
