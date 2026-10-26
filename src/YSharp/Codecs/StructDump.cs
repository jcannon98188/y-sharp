#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using YSharp.Lib0;
using YSharp.Store;

namespace YSharp.Codecs;

/// <summary>
/// The struct-store dump of ADR 0016, in the shape the fixture generator's <c>dumpStructs</c> writes: an array of <c>[client, [struct, ...]]</c>,
/// clients descending, structs in clock order. An item is <c>{id, len, kind, origin?, rightOrigin?, parent, parentSub?, content:{ref, value},
/// deleted, keep}</c>; a GC struct only <c>{id, len, kind}</c>. Values are ADR 0011 tagged values. Test-only (an internal seam, ADR 0015).
/// </summary>
internal static class StructDump
{
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Write(Doc doc)
    {
        var store = doc.Store;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Options))
        {
            w.WriteStartArray();
            for (var rank = store.ClientCount - 1; rank >= 0; rank--)
            {
                var count = store.StructCountAtRank(rank);
                if (count == 0) continue;
                var client = store.ClientIdAtRank(rank);
                w.WriteStartArray();
                w.WriteNumberValue(client);
                w.WriteStartArray();
                for (var i = 0; i < count; i++) WriteStruct(w, doc, store.StructAtRank(rank, i), client);
                w.WriteEndArray();
                w.WriteEndArray();
            }

            w.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static void WriteStruct(Utf8JsonWriter w, Doc doc, ItemRef s, ulong client)
    {
        var store = doc.Store;
        w.WriteStartObject();
        w.WritePropertyName("id");
        WriteId(w, new ItemId(client, s.Clock));
        w.WriteNumber("len", s.Length);
        if (s.IsGc)
        {
            w.WriteString("kind", "gc");
            w.WriteEndObject();
            return;
        }

        w.WriteString("kind", "item");
        if (store.OriginOf(s) is { } origin)
        {
            w.WritePropertyName("origin");
            WriteId(w, origin);
        }

        if (store.RightOriginOf(s) is { } rightOrigin)
        {
            w.WritePropertyName("rightOrigin");
            WriteId(w, rightOrigin);
        }

        w.WritePropertyName("parent");
        var parent = s.Parent;
        var parentItem = parent is null ? ItemRef.Null : StructStore.ItemOf(parent);
        if (parent is null) w.WriteNullValue();
        else if (!parentItem.IsNull) WriteId(w, store.IdOf(parentItem));
        else if (doc.RootNameOf(parent) is { } name) w.WriteStringValue(name);
        else w.WriteNullValue();
        if (store.ParentSubOf(s) is { } sub) w.WriteString("parentSub", sub);
        w.WritePropertyName("content");
        w.WriteStartObject();
        w.WriteNumber("ref", UpdateWriter.ContentRef(s.Kind));
        w.WritePropertyName("value");
        WriteContent(w, store, s);
        w.WriteEndObject();
        w.WriteBoolean("deleted", s.IsDeleted);
        w.WriteBoolean("keep", s.IsKeep);
        w.WriteEndObject();
    }

    private static void WriteContent(Utf8JsonWriter w, StructStore store, ItemRef s)
    {
        switch (s.Kind)
        {
            case ContentKind.Deleted:
                w.WriteNullValue();
                return;
            case ContentKind.String:
                WriteString(w, store.GetString(s));
                return;
            case ContentKind.Any:
            case ContentKind.Json:
                w.WriteStartArray();
                for (var i = 0; i < s.Length; i++) WriteValue(w, store.GetValue(s, i));
                w.WriteEndArray();
                return;
        }

        switch (store.GetPayload(s))
        {
            case byte[] bytes:
                WriteBytes(w, bytes);
                break;
            case EmbedContent embed:
                WriteValue(w, embed.Value);
                break;
            case FormatContent format:
                w.WriteStartArray();
                WriteString(w, format.Key);
                WriteValue(w, format.Value);
                w.WriteEndArray();
                break;
            case Branch type:
                w.WriteStringValue(TypeRefs.ClassName(type.TypeRef));
                break;
            case SubdocContent subdoc:
                w.WriteStartArray();
                WriteString(w, subdoc.Guid);
                WriteValue(w, subdoc.Options);
                w.WriteEndArray();
                break;
            default:
                w.WriteNullValue();
                break;
        }
    }

    private static void WriteId(Utf8JsonWriter w, ItemId id)
    {
        w.WriteStartArray();
        w.WriteNumberValue(id.Client);
        w.WriteNumberValue(id.Clock);
        w.WriteEndArray();
    }

    // ------------------------------------------------------------------ ADR 0011 tagged values

    /// <summary>A tagged value: plain JSON where it can carry the value, a <c>{"$": ...}</c> object where it cannot. Iterative on nesting.</summary>
    private static void WriteValue(Utf8JsonWriter w, in YValue root)
    {
        // each frame is a container being written and the index of its next child; a value is written when it is reached
        var stack = new Stack<(YValue Container, int Next, int[]? Order)>();
        var current = root;
        while (true)
        {
            if (current.IsContainer)
            {
                if (current.Kind == YValueKind.Array) w.WriteStartArray();
                else
                {
                    w.WriteStartObject();
                    w.WriteString("$", "obj");
                    w.WritePropertyName("pairs");
                    w.WriteStartArray();
                }

                stack.Push((current, 0, current.Kind == YValueKind.Object ? KeyOrder(current) : null));
            }
            else
            {
                WriteScalar(w, current);
            }

            // find the next child to write, closing finished containers
            while (true)
            {
                if (stack.Count == 0) return;
                var (container, next, order) = stack.Pop();
                if (next < container.ChildCount)
                {
                    stack.Push((container, next + 1, order));
                    var child = container.ChildAt(order is null ? next : order[next], out var key);
                    if (key is not null)
                    {
                        // an object member is a [key, value] pair; the pair array is closed when the member's value is done
                        w.WriteStartArray();
                        WriteString(w, key);
                        stack.Push((YValue.FromArrayOwned(Array.Empty<YValue>()), 0, null));
                    }

                    current = child;
                    break;
                }

                if (container.Kind == YValueKind.Array) w.WriteEndArray();
                else
                {
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
            }
        }
    }

    private static int[] KeyOrder(in YValue obj)
    {
        var count = obj.ChildCount;
        var keys = new string[count];
        for (var i = 0; i < count; i++)
        {
            obj.ChildAt(i, out var key);
            keys[i] = key!;
        }

        var order = new int[count];
        JsKeyOrder.Order(keys, order);
        return order;
    }

    private static void WriteScalar(Utf8JsonWriter w, in YValue v)
    {
        switch (v.Kind)
        {
            case YValueKind.Undefined:
                Tag(w, "undefined");
                break;
            case YValueKind.Null:
                w.WriteNullValue();
                break;
            case YValueKind.Bool:
                w.WriteBooleanValue(v.AsBool());
                break;
            case YValueKind.Number:
                var d = v.AsNumber();
                if (double.IsNaN(d)) Tag(w, "nan");
                else if (double.IsPositiveInfinity(d)) Tag(w, "inf");
                else if (double.IsNegativeInfinity(d)) Tag(w, "-inf");
                else if (d == 0 && double.IsNegative(d)) Tag(w, "-0");
                else w.WriteNumberValue(d);
                break;
            case YValueKind.BigInt:
                v.TryGetBigInt(out var big);
                w.WriteStartObject();
                w.WriteString("$", "bigint");
                w.WriteString("v", big.ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteEndObject();
                break;
            case YValueKind.String:
                WriteString(w, v.AsString());
                break;
            case YValueKind.Bytes:
                v.TryGetBytes(out var bytes);
                WriteBytes(w, bytes);
                break;
            default:
                throw new YInternalException($"A {v.Kind} value is not a scalar.");
        }
    }

    private static void Tag(Utf8JsonWriter w, string tag)
    {
        w.WriteStartObject();
        w.WriteString("$", tag);
        w.WriteEndObject();
    }

    private static void WriteBytes(Utf8JsonWriter w, ReadOnlySpan<byte> bytes)
    {
        w.WriteStartObject();
        w.WriteString("$", "bytes");
        w.WriteString("hex", Convert.ToHexStringLower(bytes));
        w.WriteEndObject();
    }

    /// <summary>A string, or <c>{"$":"str","units":[...]}</c> when it holds a lone surrogate.</summary>
    private static void WriteString(Utf8JsonWriter w, string s)
    {
        if (!HasLoneSurrogate(s))
        {
            w.WriteStringValue(s);
            return;
        }

        w.WriteStartObject();
        w.WriteString("$", "str");
        w.WritePropertyName("units");
        w.WriteStartArray();
        foreach (var c in s) w.WriteNumberValue(c);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    i++;
                    continue;
                }

                return true;
            }

            if (char.IsLowSurrogate(s[i])) return true;
        }

        return false;
    }
}
