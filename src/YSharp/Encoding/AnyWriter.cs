#nullable enable
using System;

namespace YSharp.Lib0;

/// <summary>
/// The Any writer (<c>lib0/encoding.js:536-598</c>) as small steps over a <see cref="Lib0Writer"/>, so it does not depend on the
/// value type. A container is a header with its count followed by the caller's members; an object member is
/// <see cref="WriteKey"/> then a value, in the order <see cref="JsKeyOrder"/> gives (Q08).
/// </summary>
internal static class AnyWriter
{
    private const double MaxIntegerTag = 0x7FFFFFFF;
    private const long CanonicalNaNBits = 0x7FF8000000000000;

    public static void WriteUndefined(Lib0Writer w) => w.WriteUint8(AnyTags.Undefined);

    public static void WriteNull(Lib0Writer w) => w.WriteUint8(AnyTags.Null);

    public static void WriteBool(Lib0Writer w, bool value) => w.WriteUint8(value ? AnyTags.True : AnyTags.False);

    /// <summary>
    /// The tag comes from the value (Q01): an integer with |n| at most 2^31-1 is a VarInt, -0 included (Q03); otherwise a value that
    /// survives a float32 round trip is a float32, which takes the infinities, -2^31 and 2^53; otherwise a float64. Every NaN is
    /// written as the V8 pattern (Q04).
    /// </summary>
    public static void WriteNumber(Lib0Writer w, double value)
    {
        if (double.IsNaN(value))
        {
            w.WriteUint8(AnyTags.Float64);
            w.WriteFloat64(BitConverter.Int64BitsToDouble(CanonicalNaNBits));
        }
        else if (Math.Floor(value) == value && Math.Abs(value) <= MaxIntegerTag)
        {
            w.WriteUint8(AnyTags.Integer);
            w.WriteVarInt((ulong)Math.Abs(value), double.IsNegative(value));
        }
        else if ((double)(float)value == value)
        {
            w.WriteUint8(AnyTags.Float32);
            w.WriteFloat32((float)value);
        }
        else
        {
            w.WriteUint8(AnyTags.Float64);
            w.WriteFloat64(value);
        }
    }

    public static void WriteBigInt(Lib0Writer w, long value)
    {
        w.WriteUint8(AnyTags.BigInt);
        w.WriteBigInt64(value);
    }

    public static void WriteString(Lib0Writer w, string value)
    {
        w.WriteUint8(AnyTags.String);
        w.WriteVarString(value);
    }

    public static void WriteBytes(Lib0Writer w, ReadOnlySpan<byte> value)
    {
        w.WriteUint8(AnyTags.Bytes);
        w.WriteVarBytes(value);
    }

    public static void WriteArrayHeader(Lib0Writer w, int count)
    {
        w.WriteUint8(AnyTags.Array);
        w.WriteVarUint((uint)count);
    }

    public static void WriteObjectHeader(Lib0Writer w, int count)
    {
        w.WriteUint8(AnyTags.Object);
        w.WriteVarUint((uint)count);
    }

    public static void WriteKey(Lib0Writer w, string key) => w.WriteVarString(key);
}
