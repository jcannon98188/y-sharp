#nullable enable
using System;
using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;

namespace YSharp.Lib0;

/// <summary>
/// lib0's decoder (<c>lib0/decoding.js</c>) over a span, with strict EOF (ADR 0005): every read that would pass the end, every
/// varint longer than ten bytes or above 2^53-1, and every invalid UTF-8 string fails as <see cref="YError.Malformed"/>. A string
/// whose length claim is above <c>maxStringBytes</c> fails as <see cref="YError.LimitExceeded"/> before anything is read for it.
/// No allocation is sized from a length claim until the claim has been checked against the bytes remaining.
/// After a failed read <see cref="Error"/> is set and the position is unspecified.
/// </summary>
internal ref struct Lib0Reader
{
    /// <summary>The largest varint value: 2^53-1, the exact range of a JS number (Q12).</summary>
    public const ulong MaxVarint = (1UL << 53) - 1;

    private const int MaxVarintBytes = 10;

    private static readonly UTF8Encoding Decoder = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly ReadOnlySpan<byte> data;
    private readonly int maxStringBytes;
    private int pos;

    public Lib0Reader(ReadOnlySpan<byte> data, int maxStringBytes = int.MaxValue)
    {
        this.data = data;
        this.maxStringBytes = maxStringBytes;
        pos = 0;
        Error = YError.None;
    }

    public YError Error { get; private set; }

    public readonly int Position => pos;

    public readonly int Remaining => data.Length - pos;

    public readonly bool HasContent => pos < data.Length;

    private bool Fail(YError error = YError.Malformed)
    {
        Error = error;
        return false;
    }

    public bool TryReadUint8(out byte value)
    {
        if (pos >= data.Length) { value = 0; return Fail(); }
        value = data[pos++];
        return true;
    }

    /// <summary>The next byte without consuming it.</summary>
    public bool TryPeekUint8(out byte value)
    {
        if (pos >= data.Length) { value = 0; return Fail(); }
        value = data[pos];
        return true;
    }

    /// <summary>
    /// Little-endian base-128. Overlong forms such as <c>80 00</c> are accepted, as in JS. More than ten bytes, or a value above
    /// 2^53-1, is Malformed (Q12: JS rounds silently there).
    /// </summary>
    public bool TryReadVarUint(out ulong value)
    {
        value = 0;
        var shift = 0;
        for (var n = 0; n < MaxVarintBytes; n++)
        {
            if (pos >= data.Length) return Fail();
            var b = data[pos++];
            if (!Accumulate(ref value, (ulong)(b & 0x7f), shift)) return Fail();
            if (b < 0x80) return true;
            shift += 7;
        }
        return Fail();
    }

    /// <summary>
    /// lib0's sign-magnitude varint. The sign is returned apart from the magnitude because -0 (<c>40</c>) is meaningful (Q03, Q13).
    /// The magnitude has the same limits as <see cref="TryReadVarUint"/>, unless the caller allows a larger <paramref name="maxMagnitude"/>.
    /// </summary>
    public bool TryReadVarInt(out ulong magnitude, out bool negative, ulong maxMagnitude = MaxVarint)
    {
        magnitude = 0;
        negative = false;
        if (pos >= data.Length) return Fail();
        var first = data[pos++];
        negative = (first & 0x40) != 0;
        magnitude = (ulong)(first & 0x3f);
        if (first < 0x80) return true;
        var shift = 6;
        for (var n = 1; n < MaxVarintBytes; n++)
        {
            if (pos >= data.Length) return Fail();
            var b = data[pos++];
            if (!Accumulate(ref magnitude, (ulong)(b & 0x7f), shift, maxMagnitude)) return Fail();
            if (b < 0x80) return true;
            shift += 7;
        }
        return Fail();
    }

    /// <summary>Adds <c>group &lt;&lt; shift</c> unless the total would pass <paramref name="max"/> (2^53-1 unless the caller says otherwise).</summary>
    private static bool Accumulate(ref ulong value, ulong group, int shift, ulong max = MaxVarint)
    {
        if (group == 0) return true;
        if (shift >= 63 || group > max >> shift) return false;
        var total = value + (group << shift);
        if (total > max) return false;
        value = total;
        return true;
    }

    /// <summary>A VarUint that must fit an <see cref="int"/>, for counts and lengths.</summary>
    public bool TryReadVarUintAsInt(out int value)
    {
        value = 0;
        if (!TryReadVarUint(out var v)) return false;
        if (v > int.MaxValue) return Fail();
        value = (int)v;
        return true;
    }

    public bool TryReadFloat32(out float value)
    {
        if (Remaining < 4) { value = 0; return Fail(); }
        value = BinaryPrimitives.ReadSingleBigEndian(data.Slice(pos, 4));
        pos += 4;
        return true;
    }

    public bool TryReadFloat64(out double value)
    {
        if (Remaining < 8) { value = 0; return Fail(); }
        value = BinaryPrimitives.ReadDoubleBigEndian(data.Slice(pos, 8));
        pos += 8;
        return true;
    }

    public bool TryReadBigInt64(out long value)
    {
        if (Remaining < 8) { value = 0; return Fail(); }
        value = BinaryPrimitives.ReadInt64BigEndian(data.Slice(pos, 8));
        pos += 8;
        return true;
    }

    /// <summary>The next <paramref name="count"/> bytes as a view into the input; callers copy what they keep.</summary>
    public bool TryReadBytes(int count, out ReadOnlySpan<byte> bytes)
    {
        if (count < 0 || count > Remaining) { bytes = default; return Fail(); }
        bytes = data.Slice(pos, count);
        pos += count;
        return true;
    }

    /// <summary>VarUint(length) then that many bytes, as a view into the input. A claim above the bytes left is Malformed.</summary>
    public bool TryReadVarBytes(out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (!TryReadVarUint(out var claim)) return false;
        if (claim > (ulong)Remaining) return Fail();
        return TryReadBytes((int)claim, out bytes);
    }

    /// <summary>VarUint(UTF-8 length) then strict UTF-8. A byte-order mark and NUL are kept; a claim above the limit is LimitExceeded.</summary>
    public bool TryReadVarString(out string value)
    {
        value = "";
        if (!TryReadVarUint(out var claim)) return false;
        if (claim > (ulong)maxStringBytes) return Fail(YError.LimitExceeded);
        if (claim > (ulong)Remaining) return Fail();
        var bytes = data.Slice(pos, (int)claim);
        if (!Utf8.IsValid(bytes)) return Fail();
        pos += bytes.Length;
        value = bytes.Length == 0 ? "" : Decoder.GetString(bytes);
        return true;
    }
}
