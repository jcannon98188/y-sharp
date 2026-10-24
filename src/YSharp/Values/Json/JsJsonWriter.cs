#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using YSharp.Lib0;

namespace YSharp;

/// <summary>
/// <c>JSON.stringify(value)</c> as UTF-8, byte for byte (docs/research/json.md sections 2 to 4). Hand written because no System.Text.Json
/// setting reproduces the escaping. Iterative: nesting costs a frame in a list, never the call stack, and is capped at
/// <see cref="YLimits.MaxAnyDepth"/>. The text is built privately and handed to the writer only when complete, so a failure writes nothing.
/// </summary>
internal static class JsJsonWriter
{
    private struct Frame
    {
        public YValue Value;
        public int Next;          // position in the iteration order
        public int[]? Order;      // object member order; null for arrays and for objects without index keys
        public bool Wrote;        // an object member was already written
    }

    public static YStatus Write(in YValue root, IBufferWriter<byte> destination)
    {
        if (root.Kind is YValueKind.Undefined) return new YStatus(YError.Rejected);   // no JSON text (Q30)
        var buf = new Buf();
        try
        {
            var status = WriteTree(root, ref buf, YLimits.Default.MaxAnyDepth);
            if (!status.Ok) return status;
            destination.Write(buf.Span);
            return default;
        }
        finally { buf.Dispose(); }
    }

    private static YStatus WriteTree(in YValue root, ref Buf buf, int maxDepth)
    {
        var stack = new List<Frame>();
        var current = root;
        while (true)
        {
            // write `current`; containers push a frame and the loop below picks up their first child
            switch (current.Kind)
            {
                case YValueKind.Null:
                case YValueKind.Undefined:   // only an array element gets here; object members were skipped
                    buf.Ascii("null");
                    break;
                case YValueKind.Bool: buf.Ascii(current.AsBool() ? "true" : "false"); break;
                case YValueKind.Number: WriteNumber(ref buf, current.AsNumber()); break;
                case YValueKind.BigInt: return new YStatus(YError.Rejected);
                case YValueKind.String: WriteString(ref buf, current.AsString()); break;
                case YValueKind.Bytes:
                {
                    current.TryGetBytes(out var bytes);
                    buf.Byte('{');
                    for (var i = 0; i < bytes.Length; i++)
                    {
                        if (i > 0) buf.Byte(',');
                        buf.Byte('"');
                        buf.Ascii(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        buf.Byte('"');
                        buf.Byte(':');
                        WriteNumber(ref buf, bytes[i]);
                    }
                    buf.Byte('}');
                    break;
                }
                case YValueKind.Array:
                    if (stack.Count >= maxDepth) return new YStatus(YError.LimitExceeded);
                    buf.Byte('[');
                    stack.Add(new Frame { Value = current });
                    break;
                default:
                {
                    if (stack.Count >= maxDepth) return new YStatus(YError.LimitExceeded);
                    buf.Byte('{');
                    current.TryGetObject(out var view);
                    var keys = new string[view.Count];
                    var anyIndex = false;
                    for (var i = 0; i < keys.Length; i++)
                    {
                        keys[i] = view[i].Key;
                        anyIndex |= JsKeyOrder.IsArrayIndex(keys[i], out _);
                    }
                    int[]? order = null;
                    if (anyIndex)
                    {
                        order = new int[keys.Length];
                        JsKeyOrder.Order(keys, order);
                    }
                    stack.Add(new Frame { Value = current, Order = order });
                    break;
                }
            }

            // find the next value to write, closing finished containers on the way
            var advanced = false;
            while (stack.Count > 0 && !advanced)
            {
                var f = stack[^1];
                if (f.Value.Kind == YValueKind.Array)
                {
                    f.Value.TryGetArray(out var items);
                    if (f.Next < items.Length)
                    {
                        if (f.Next > 0) buf.Byte(',');
                        current = items[f.Next++];
                        stack[^1] = f;
                        advanced = true;
                    }
                    else
                    {
                        buf.Byte(']');
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
                else
                {
                    f.Value.TryGetObject(out var view);
                    while (f.Next < view.Count && view[f.Order is null ? f.Next : f.Order[f.Next]].Value.Kind == YValueKind.Undefined) f.Next++;
                    if (f.Next < view.Count)
                    {
                        var member = view[f.Order is null ? f.Next : f.Order[f.Next]];
                        f.Next++;
                        if (f.Wrote) buf.Byte(',');
                        f.Wrote = true;
                        WriteString(ref buf, member.Key);
                        buf.Byte(':');
                        current = member.Value;
                        stack[^1] = f;
                        advanced = true;
                    }
                    else
                    {
                        buf.Byte('}');
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
            }
            if (!advanced) return default;
        }
    }

    private static void WriteNumber(ref Buf buf, double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) { buf.Ascii("null"); return; }
        Span<char> chars = stackalloc char[JsNumberFormatter.MaxLength];
        var n = JsNumberFormatter.Format(v, chars);
        for (var i = 0; i < n; i++) buf.Byte((byte)chars[i]);
    }

    private const string Hex = "0123456789abcdef";

    private static void WriteString(ref Buf buf, string s)
    {
        buf.Byte('"');
        for (var i = 0; i < s.Length; i++)
        {
            int c = s[i];
            switch (c)
            {
                case '"': buf.Ascii("\\\""); break;
                case '\\': buf.Ascii("\\\\"); break;
                case '\b': buf.Ascii("\\b"); break;
                case '\t': buf.Ascii("\\t"); break;
                case '\n': buf.Ascii("\\n"); break;
                case '\f': buf.Ascii("\\f"); break;
                case '\r': buf.Ascii("\\r"); break;
                default:
                    var pair = c >= 0xD800 && c <= 0xDBFF && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
                    if (c < 0x20 || (c >= 0xD800 && c <= 0xDFFF && !pair))
                    {
                        // control character or lone surrogate: \u and four lowercase hex digits
                        buf.Ascii("\\u");
                        buf.Byte((byte)Hex[(c >> 12) & 15]);
                        buf.Byte((byte)Hex[(c >> 8) & 15]);
                        buf.Byte((byte)Hex[(c >> 4) & 15]);
                        buf.Byte((byte)Hex[c & 15]);
                    }
                    else if (c < 0x80) buf.Byte((byte)c);
                    else if (c < 0x800)
                    {
                        buf.Byte((byte)(0xC0 | (c >> 6)));
                        buf.Byte((byte)(0x80 | (c & 0x3F)));
                    }
                    else if (pair)
                    {
                        var cp = char.ConvertToUtf32((char)c, s[++i]);
                        buf.Byte((byte)(0xF0 | (cp >> 18)));
                        buf.Byte((byte)(0x80 | ((cp >> 12) & 0x3F)));
                        buf.Byte((byte)(0x80 | ((cp >> 6) & 0x3F)));
                        buf.Byte((byte)(0x80 | (cp & 0x3F)));
                    }
                    else
                    {
                        buf.Byte((byte)(0xE0 | (c >> 12)));
                        buf.Byte((byte)(0x80 | ((c >> 6) & 0x3F)));
                        buf.Byte((byte)(0x80 | (c & 0x3F)));
                    }
                    break;
            }
        }
        buf.Byte('"');
    }

    /// <summary>A growable pooled byte buffer.</summary>
    private struct Buf
    {
        private byte[]? array;
        private int length;

        public readonly ReadOnlySpan<byte> Span => array is null ? default : array.AsSpan(0, length);

        public void Byte(char b) => Byte((byte)b);

        public void Byte(byte b)
        {
            if (array is null || length == array.Length) Grow();
            array![length++] = b;
        }

        public void Ascii(string s)
        {
            foreach (var c in s) Byte((byte)c);
        }

        private void Grow()
        {
            var bigger = ArrayPool<byte>.Shared.Rent(array is null ? 256 : array.Length * 2);
            if (array is not null)
            {
                Buffer.BlockCopy(array, 0, bigger, 0, length);
                ArrayPool<byte>.Shared.Return(array);
            }
            array = bigger;
        }

        public void Dispose()
        {
            if (array is not null) ArrayPool<byte>.Shared.Return(array);
            array = null;
        }
    }
}
