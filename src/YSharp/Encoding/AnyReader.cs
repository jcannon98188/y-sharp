#nullable enable
using System;
using System.Buffers;

namespace YSharp.Lib0;

/// <summary>
/// The Any reader (<c>lib0/decoding.js:461-497</c>) with the hostile-input rules of ADR 0005. It is iterative: nesting costs a
/// frame in a small pooled stack, never the call stack, and is capped by <c>maxDepth</c> (a container nested deeper is
/// LimitExceeded; an array holding an array is depth 2 and a scalar leaf adds none). A tag outside 116 to 127, a truncated
/// payload and a container whose count cannot fit the bytes remaining are Malformed, and the count is checked before the sink
/// is asked to begin the container, so no allocation follows a bare length claim.
/// </summary>
internal static class AnyReader
{
    private const int InlineFrames = 32;

    /// <summary>Reads one Any value into <paramref name="sink"/>. Returns <see cref="YError.None"/> or the error; trailing bytes are left unread.</summary>
    public static YError Read<TSink>(ref Lib0Reader reader, ref TSink sink, int maxDepth) where TSink : IAnySink
    {
        // A frame is (members left << 1) | isObject.
        Span<long> frames = stackalloc long[InlineFrames];
        long[]? rented = null;
        try
        {
            var depth = 0;
            while (true)
            {
                if (depth > 0 && (frames[depth - 1] & 1) != 0 && !ReadKey(ref reader, ref sink)) return reader.Error;

                if (!reader.TryReadUint8(out var tag)) return reader.Error;
                var opened = false;
                switch (tag)
                {
                    case AnyTags.Undefined: sink.Undefined(); break;
                    case AnyTags.Null: sink.Null(); break;
                    case AnyTags.False: sink.Bool(false); break;
                    case AnyTags.True: sink.Bool(true); break;
                    case AnyTags.Integer:
                        if (!reader.TryReadVarInt(out var magnitude, out var negative)) return reader.Error;
                        sink.Number(negative ? -(double)magnitude : (double)magnitude);
                        break;
                    case AnyTags.Float32:
                        if (!reader.TryReadFloat32(out var f)) return reader.Error;
                        sink.Number(f);
                        break;
                    case AnyTags.Float64:
                        if (!reader.TryReadFloat64(out var d)) return reader.Error;
                        sink.Number(d);
                        break;
                    case AnyTags.BigInt:
                        if (!reader.TryReadBigInt64(out var big)) return reader.Error;
                        sink.BigInt(big);
                        break;
                    case AnyTags.String:
                        if (!reader.TryReadVarString(out var s)) return reader.Error;
                        sink.String(s);
                        break;
                    case AnyTags.Bytes:
                        if (!reader.TryReadVarBytes(out var bytes)) return reader.Error;
                        sink.Bytes(bytes);
                        break;
                    case AnyTags.Array:
                    case AnyTags.Object:
                    {
                        var isObject = tag == AnyTags.Object;
                        if (!reader.TryReadVarUint(out var claim)) return reader.Error;
                        // every element is at least a tag byte, every member at least a key length byte and a tag byte
                        var minimum = isObject ? 2UL : 1UL;
                        if (claim > (ulong)reader.Remaining / minimum) return YError.Malformed;
                        if (depth + 1 > maxDepth) return YError.LimitExceeded;
                        var count = (int)claim;
                        if (isObject) sink.BeginObject(count); else sink.BeginArray(count);
                        if (count == 0)
                        {
                            if (isObject) sink.EndObject(); else sink.EndArray();
                        }
                        else
                        {
                            if (depth == frames.Length) frames = Grow(frames, depth, ref rented);
                            frames[depth++] = ((long)count << 1) | (isObject ? 1L : 0L);
                            opened = true;
                        }
                        break;
                    }
                    default:
                        return YError.Malformed;
                }
                if (opened) continue;

                // a value is complete: count it against its parents, closing every container it finishes
                while (depth > 0)
                {
                    var frame = frames[depth - 1] - 2;
                    frames[depth - 1] = frame;
                    if (frame >> 1 > 0) break;
                    if ((frame & 1) != 0) sink.EndObject(); else sink.EndArray();
                    depth--;
                }
                if (depth == 0) return YError.None;
            }
        }
        finally
        {
            if (rented != null) ArrayPool<long>.Shared.Return(rented);
        }
    }

    private static bool ReadKey<TSink>(ref Lib0Reader reader, ref TSink sink) where TSink : IAnySink
    {
        if (!reader.TryReadVarString(out var key)) return false;
        sink.Key(key);
        return true;
    }

    private static Span<long> Grow(Span<long> frames, int used, ref long[]? rented)
    {
        var bigger = ArrayPool<long>.Shared.Rent(Math.Max(frames.Length * 2, 64));
        frames[..used].CopyTo(bigger);
        if (rented != null) ArrayPool<long>.Shared.Return(rented);
        rented = bigger;
        return bigger;
    }
}
