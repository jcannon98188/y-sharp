#nullable enable
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace YSharp.Lib0;

/// <summary>
/// lib0's encoder (<c>lib0/encoding.js</c>): varints, strings, byte arrays and big-endian floats, written byte-exact into a
/// rented buffer. Dispose returns the buffer; disposing twice is harmless and any write after it throws.
/// </summary>
internal sealed class Lib0Writer : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    [ThreadStatic]
    private static Lib0Writer? spare;

    private byte[]? buffer;
    private int length;
    private bool reusable;

    public Lib0Writer(int initialCapacity = 256) => buffer = BufferPool.Rent(Math.Max(initialCapacity, 16));

    /// <summary>
    /// A writer for a hot path that must not allocate (ADR 0009, BUD-06): the calling thread's spare writer when it has one, with a newly rented
    /// buffer. Disposing it returns the buffer, as for any writer, and keeps the writer object as the thread's spare.
    /// </summary>
    public static Lib0Writer Rent(int initialCapacity = 256)
    {
        var w = spare;
        if (w is null) return new Lib0Writer(initialCapacity) { reusable = true };
        spare = null;
        w.buffer = BufferPool.Rent(Math.Max(initialCapacity, 16));
        w.length = 0;
        return w;
    }

    public int Length => length;

    public ReadOnlySpan<byte> WrittenSpan => (buffer ?? throw Disposed()).AsSpan(0, length);

    public byte[] ToArray() => WrittenSpan.ToArray();

    /// <summary>Forgets what was written and keeps the rented buffer for reuse.</summary>
    public void Reset()
    {
        _ = buffer ?? throw Disposed();
        length = 0;
    }

    public void CopyTo(IBufferWriter<byte> destination) => destination.Write(WrittenSpan);

    public void Dispose()
    {
        var b = buffer;
        if (b == null) return;
        buffer = null;
        length = 0;
        BufferPool.Return(b);
        if (reusable) spare = this;
    }

    private static ObjectDisposedException Disposed() => new(nameof(Lib0Writer));

    private Span<byte> Take(int count)
    {
        var b = buffer ?? throw Disposed();
        if (b.Length - length < count) b = Grow(b, count);
        var span = b.AsSpan(length, count);
        length += count;
        return span;
    }

    private byte[] Grow(byte[] old, int count)
    {
        var needed = (long)length + count;
        if (needed > Array.MaxLength) throw new OutOfMemoryException();
        var size = (int)Math.Min(Math.Max(needed, 2L * old.Length), Array.MaxLength);
        var bigger = BufferPool.Rent(size);
        old.AsSpan(0, length).CopyTo(bigger);
        buffer = bigger;
        BufferPool.Return(old);
        return bigger;
    }

    public void WriteUint8(byte value) => Take(1)[0] = value;

    public void WriteRaw(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Take(bytes.Length));

    /// <summary>Little-endian base-128, seven bits per byte, bit 0x80 = more. Values above 2^53-1 are written faithfully; callers validate.</summary>
    public void WriteVarUint(ulong value)
    {
        var span = Take(VarUintLength(value));
        var i = 0;
        while (value > 127) { span[i++] = (byte)((value & 127) | 128); value >>= 7; }
        span[i] = (byte)value;
    }

    /// <summary>
    /// lib0's sign-magnitude varint: six magnitude bits, the sign in 0x40 and the continuation in 0x80 of the first byte, then seven
    /// bits per byte. The sign is separate from the magnitude because -0 is meaningful (Q03, Q13).
    /// </summary>
    public void WriteVarInt(ulong magnitude, bool negative)
    {
        var span = Take(VarIntLength(magnitude));
        var i = 0;
        span[i++] = (byte)((magnitude > 63 ? 128 : 0) | (negative ? 64 : 0) | (int)(magnitude & 63));
        magnitude >>= 6;
        while (magnitude > 0)
        {
            span[i++] = (byte)((magnitude > 127 ? 128 : 0) | (int)(magnitude & 127));
            magnitude >>= 7;
        }
    }

    public void WriteVarInt(long value) =>
        WriteVarInt(value < 0 ? (ulong)(-(value + 1)) + 1UL : (ulong)value, value < 0);

    public void WriteFloat32(float value) => BinaryPrimitives.WriteSingleBigEndian(Take(4), value);

    public void WriteFloat64(double value) => BinaryPrimitives.WriteDoubleBigEndian(Take(8), value);

    public void WriteBigInt64(long value) => BinaryPrimitives.WriteInt64BigEndian(Take(8), value);

    /// <summary>VarUint(byte length) then the bytes.</summary>
    public void WriteVarBytes(ReadOnlySpan<byte> bytes)
    {
        WriteVarUint((uint)bytes.Length);
        WriteRaw(bytes);
    }

    /// <summary>VarUint(UTF-8 byte length) then the UTF-8. A lone surrogate is written as U+FFFD, as <c>TextEncoder</c> does.</summary>
    public void WriteVarString(string value)
    {
        var byteCount = Utf8.GetByteCount(value);
        WriteVarUint((uint)byteCount);
        Utf8.GetBytes(value, Take(byteCount));
    }

    /// <summary>The span form of <see cref="WriteVarString(string)"/>, for text read out of a buffer without making a string.</summary>
    public void WriteVarString(ReadOnlySpan<char> value)
    {
        var byteCount = Utf8.GetByteCount(value);
        WriteVarUint((uint)byteCount);
        Utf8.GetBytes(value, Take(byteCount));
    }

    internal static int VarUintLength(ulong value)
    {
        var n = 1;
        while (value > 127) { value >>= 7; n++; }
        return n;
    }

    internal static int VarIntLength(ulong magnitude) => magnitude <= 63 ? 1 : 1 + VarUintLength(magnitude >> 6);
}
