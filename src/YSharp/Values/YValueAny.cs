#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using YSharp.Lib0;

namespace YSharp;

/// <summary>
/// The seam between <see cref="YValue"/> and the lib0 Any codec (ADR 0002, 0007). <see cref="Read"/> builds a value from
/// <see cref="AnyReader"/>'s callbacks; <see cref="Write"/> walks a value into <see cref="AnyWriter"/>. Both are iterative, so a deeply
/// nested value costs heap, not stack, and both stop at the nesting cap (<see cref="YLimits.MaxAnyDepth"/>): a container nested deeper than
/// the cap is <see cref="YError.LimitExceeded"/>, an array holding an array being depth 2 and a scalar leaf adding none.
/// </summary>
internal static class YValueAny
{
    /// <summary>Reads one Any value. On a failure <paramref name="value"/> is Undefined and the document-level caller decides what to report.</summary>
    public static YError Read(ref Lib0Reader reader, int maxDepth, out YValue value)
    {
        var builder = new YValueBuilder();
        var error = AnyReader.Read(ref reader, ref builder, maxDepth);
        value = error == YError.None ? builder.Result : default;
        return error;
    }

    /// <summary>
    /// Writes <paramref name="value"/> as Any. The tag of a number comes from the value, every NaN is written as the V8 pattern, and object keys
    /// are written in JS order (array-index keys ascending, then insertion order; Q01 to Q09). On <see cref="YError.LimitExceeded"/> the writer
    /// holds a partial value and must be discarded or reset.
    /// </summary>
    public static YError Write(Lib0Writer writer, in YValue value, int maxDepth)
    {
        var kind = value.Kind;
        if (kind < YValueKind.Array)
        {
            WriteScalar(writer, value, kind);
            return YError.None;
        }
        if (maxDepth < 1) return YError.LimitExceeded;

        var stack = new List<WriteFrame>();
        var current = Open(writer, value, kind);
        while (true)
        {
            if (current.Next == current.Count)
            {
                if (stack.Count == 0) return YError.None;
                current = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            var position = current.Order is null ? current.Next : current.Order[current.Next];
            current.Next++;
            var child = current.Container.ChildAt(position, out var key);
            if (key is not null) AnyWriter.WriteKey(writer, key);
            var childKind = child.Kind;
            if (childKind < YValueKind.Array)
            {
                WriteScalar(writer, child, childKind);
                continue;
            }
            if (stack.Count + 2 > maxDepth) return YError.LimitExceeded;
            stack.Add(current);
            current = Open(writer, child, childKind);
        }
    }

    private struct WriteFrame
    {
        public YValue Container;
        public int Count;
        public int Next;
        public int[]? Order;
    }

    private static WriteFrame Open(Lib0Writer writer, in YValue container, YValueKind kind)
    {
        var count = container.ChildCount;
        int[]? order = null;
        if (kind == YValueKind.Array)
        {
            AnyWriter.WriteArrayHeader(writer, count);
        }
        else
        {
            AnyWriter.WriteObjectHeader(writer, count);
            order = KeyOrder(container, count);
        }
        return new WriteFrame { Container = container, Count = count, Order = order };
    }

    /// <summary>The write order of an object's members, or null when insertion order is already the JS order (no array-index key).</summary>
    private static int[]? KeyOrder(in YValue container, int count)
    {
        var members = ((YObjectData)container.Payload!).Members;
        var any = false;
        foreach (var m in members)
        {
            if (!JsKeyOrder.IsArrayIndex(m.Key, out _)) continue;
            any = true;
            break;
        }
        if (!any) return null;
        var keys = new string[count];
        for (var i = 0; i < count; i++) keys[i] = members[i].Key;
        var order = new int[count];
        JsKeyOrder.Order(keys, order);
        return order;
    }

    private static void WriteScalar(Lib0Writer w, in YValue v, YValueKind kind)
    {
        switch (kind)
        {
            case YValueKind.Undefined: AnyWriter.WriteUndefined(w); break;
            case YValueKind.Null: AnyWriter.WriteNull(w); break;
            case YValueKind.Bool: AnyWriter.WriteBool(w, v.Bits != 0); break;
            case YValueKind.Number: AnyWriter.WriteNumber(w, BitConverter.Int64BitsToDouble(v.Bits)); break;
            case YValueKind.BigInt: AnyWriter.WriteBigInt(w, v.Bits); break;
            case YValueKind.String: AnyWriter.WriteString(w, (string)v.Payload!); break;
            default: AnyWriter.WriteBytes(w, (byte[])v.Payload!); break;
        }
    }

    /// <summary>
    /// What <see cref="AnyReader"/> delivers, built into a <see cref="YValue"/> tree (ADR 0007). A struct, so a scalar leaves no builder
    /// behind. Children collect on one shared list per kind of frame and are copied out exactly when their container closes, so an
    /// element count claimed by the input is never trusted for an allocation. An object with a repeated key keeps the first position and the
    /// last value, and <c>__proto__</c> is an ordinary key (Q09).
    /// </summary>
    private struct YValueBuilder : IAnySink
    {
        private struct Open
        {
            public bool IsObject;
            public int ValueStart;
            public int KeyStart;
        }

        private YValue top;
        private List<YValue>? values;
        private List<string>? keys;
        private List<Open>? open;

        public readonly YValue Result => top;

        private void Add(YValue v)
        {
            if (open is null || open.Count == 0) top = v;
            else values!.Add(v);
        }

        public void Undefined() => Add(default);

        public void Null() => Add(YValue.Null);

        public void Bool(bool value) => Add(YValue.FromBool(value));

        public void Number(double value) => Add(YValue.FromNumber(value));

        public void BigInt(long value) => Add(YValue.BigInt(value));

        public void String(string value) => Add(value);

        public void Bytes(ReadOnlySpan<byte> value) => Add(YValue.Bytes(value));

        public void BeginArray(int count) => Begin(false);

        public void BeginObject(int count) => Begin(true);

        private void Begin(bool isObject)
        {
            values ??= new List<YValue>();
            open ??= new List<Open>();
            keys ??= new List<string>();
            open.Add(new Open { IsObject = isObject, ValueStart = values.Count, KeyStart = keys.Count });
        }

        public void Key(string key) => keys!.Add(key);

        public void EndArray()
        {
            var frame = Pop();
            var items = new YValue[values!.Count - frame.ValueStart];
            values.CopyTo(frame.ValueStart, items, 0, items.Length);
            values.RemoveRange(frame.ValueStart, items.Length);
            Add(YValue.FromArrayOwned(items));
        }

        public void EndObject()
        {
            var frame = Pop();
            var members = new KeyValuePair<string, YValue>[values!.Count - frame.ValueStart];
            for (var i = 0; i < members.Length; i++) members[i] = new KeyValuePair<string, YValue>(keys![frame.KeyStart + i], values[frame.ValueStart + i]);
            values.RemoveRange(frame.ValueStart, members.Length);
            keys!.RemoveRange(frame.KeyStart, members.Length);
            Add(YValue.FromObjectOwned(YObjectData.Dedupe(members)));
        }

        private Open Pop()
        {
            var frame = open![^1];
            open.RemoveAt(open.Count - 1);
            return frame;
        }
    }
}
