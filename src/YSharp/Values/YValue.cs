#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace YSharp;

// ---------------------------------------------------------------- values (ADR 0002, 0007)

public enum YValueKind { Undefined, Null, Bool, Number, BigInt, String, Bytes, Array, Object }

/// <summary>
/// An immutable tree of the nine value cases a JS Any can carry. 16 bytes: a reference that tells the case apart and holds the payload of the
/// reference cases, and 8 bytes of payload for the scalar ones. <c>default(YValue)</c> is Undefined.
/// </summary>
public readonly partial struct YValue : IEquatable<YValue>
{
    /// <summary>Marks the scalar cases whose payload lives in the 8 bytes. A reference payload (string, byte[], YValue[], YObjectData) needs no tag.</summary>
    private sealed class Tag
    {
        public readonly YValueKind Kind;

        public Tag(YValueKind kind) => Kind = kind;
    }

    private static readonly Tag NullTag = new(YValueKind.Null);
    private static readonly Tag BoolTag = new(YValueKind.Bool);
    private static readonly Tag NumberTag = new(YValueKind.Number);
    private static readonly Tag BigIntTag = new(YValueKind.BigInt);

    private readonly object? payload;
    private readonly long bits;

    private YValue(object? payload, long bits)
    {
        this.payload = payload;
        this.bits = bits;
    }

    public static YValue Undefined => default;

    public static YValue Null => new(NullTag, 0);

    public YValueKind Kind
    {
        get
        {
            var p = payload;
            if (p is null) return YValueKind.Undefined;
            if (p is Tag tag) return tag.Kind;
            if (p is string) return YValueKind.String;
            if (p is byte[]) return YValueKind.Bytes;
            if (p is YValue[]) return YValueKind.Array;
            return YValueKind.Object;
        }
    }

    // ------------------------------------------------------------------ construction

    /// <summary>A 64-bit integer, which the wire carries as its own BigInt case. There is no implicit conversion from <see cref="long"/>.</summary>
    public static YValue BigInt(long v) => new(BigIntTag, v);

    /// <summary>A number from an unsigned integer. Values above 2^53 cannot be told apart from their neighbours as doubles, so they throw.</summary>
    public static YValue Number(ulong v) =>
        v <= 1UL << 53 ? new(NumberTag, BitConverter.DoubleToInt64Bits(v)) : throw new ArgumentOutOfRangeException(nameof(v), v, "A JS number holds integers exactly only up to 2^53.");

    /// <summary>A byte string. The bytes are copied, so the caller may reuse its buffer (Q31).</summary>
    public static YValue Bytes(ReadOnlySpan<byte> copy) => new(copy.IsEmpty ? System.Array.Empty<byte>() : copy.ToArray(), 0);

    /// <summary>A byte string that takes the array without copying. The caller must not change it afterwards.</summary>
    public static YValue BytesOwned(byte[] transfer) => new(transfer ?? throw new ArgumentNullException(nameof(transfer)), 0);

    public static YValue Array(ReadOnlySpan<YValue> items) => new(items.IsEmpty ? System.Array.Empty<YValue>() : items.ToArray(), 0);

    /// <summary>An object. Members keep their order; a repeated key keeps its first position and takes the last value.</summary>
    public static YValue Object(ReadOnlySpan<KeyValuePair<string, YValue>> members) => new(YObjectData.Create(members), 0);

    internal static YValue FromArrayOwned(YValue[] items) => new(items, 0);

    internal static YValue FromObjectOwned(YObjectData data) => new(data, 0);

    internal static YValue FromNumber(double v) => new(NumberTag, BitConverter.DoubleToInt64Bits(v));

    internal static YValue FromBool(bool v) => new(BoolTag, v ? 1 : 0);

    public static implicit operator YValue(bool v) => FromBool(v);

    public static implicit operator YValue(double v) => FromNumber(v);

    public static implicit operator YValue(int v) => FromNumber(v);

    public static implicit operator YValue(uint v) => FromNumber(v);

    /// <summary>A string; <c>null</c> becomes <see cref="Null"/>.</summary>
    public static implicit operator YValue(string? v) => v is null ? Null : new(v, 0);

    // ------------------------------------------------------------------ access

    public bool TryGetBool(out bool v)
    {
        if (ReferenceEquals(payload, BoolTag)) { v = bits != 0; return true; }
        v = false;
        return false;
    }

    public bool TryGetNumber(out double v)
    {
        if (ReferenceEquals(payload, NumberTag)) { v = BitConverter.Int64BitsToDouble(bits); return true; }
        v = 0;
        return false;
    }

    public bool TryGetBigInt(out long v)
    {
        if (ReferenceEquals(payload, BigIntTag)) { v = bits; return true; }
        v = 0;
        return false;
    }

    public bool TryGetString(out string v)
    {
        if (payload is string s) { v = s; return true; }
        v = "";
        return false;
    }

    public bool TryGetBytes(out ReadOnlySpan<byte> v)
    {
        if (payload is byte[] b) { v = b; return true; }
        v = default;
        return false;
    }

    public bool TryGetArray(out ReadOnlySpan<YValue> v)
    {
        if (payload is YValue[] a) { v = a; return true; }
        v = default;
        return false;
    }

    public bool TryGetObject(out YObjectView v)
    {
        if (payload is YObjectData o) { v = new YObjectView(o); return true; }
        v = default;
        return false;
    }

    public bool AsBool() => TryGetBool(out var v) ? v : throw Mismatch(YValueKind.Bool);

    public double AsNumber() => TryGetNumber(out var v) ? v : throw Mismatch(YValueKind.Number);

    public string AsString() => TryGetString(out var v) ? v : throw Mismatch(YValueKind.String);

    private InvalidOperationException Mismatch(YValueKind wanted) => new($"The value is {Kind}, not {wanted}.");

    // ------------------------------------------------------------------ shape, for the equality and encoding walks

    internal bool IsContainer => payload is YValue[] or YObjectData;

    /// <summary>The number of children of an Array or Object, otherwise 0.</summary>
    internal int ChildCount => payload is YValue[] a ? a.Length : payload is YObjectData o ? o.Members.Length : 0;

    /// <summary>Child <paramref name="i"/> of an Array (key null) or an Object (its key).</summary>
    internal YValue ChildAt(int i, out string? key)
    {
        if (payload is YValue[] a)
        {
            key = null;
            return a[i];
        }
        var m = ((YObjectData)payload!).Members[i];
        key = m.Key;
        return m.Value;
    }

    internal object? Payload => payload;

    internal long Bits => bits;

    public override string ToString() => Kind switch
    {
        YValueKind.Undefined => "Undefined",
        YValueKind.Null => "Null",
        YValueKind.Bool => bits != 0 ? "Bool(true)" : "Bool(false)",
        YValueKind.Number => "Number(" + BitConverter.Int64BitsToDouble(bits).ToString("R", CultureInfo.InvariantCulture) + ")",
        YValueKind.BigInt => "BigInt(" + bits.ToString(CultureInfo.InvariantCulture) + ")",
        YValueKind.String => "String(" + Truncate((string)payload!) + ")",
        YValueKind.Bytes => "Bytes(" + ((byte[])payload!).Length.ToString(CultureInfo.InvariantCulture) + ")",
        YValueKind.Array => "Array(" + ChildCount.ToString(CultureInfo.InvariantCulture) + ")",
        _ => "Object(" + ChildCount.ToString(CultureInfo.InvariantCulture) + ")",
    };

    private static string Truncate(string s) => s.Length <= 32 ? "\"" + s + "\"" : "\"" + s.AsSpan(0, 32).ToString() + "\"...";
}
