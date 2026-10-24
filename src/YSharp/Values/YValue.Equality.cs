#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using YSharp.Lib0;

namespace YSharp;

// Wire equality, hashing and the YText attribute rule. Containers can be nested without limit (ADR 0007), so every walk here keeps its own
// stack on the heap and never recurses.
public readonly partial struct YValue
{
    private const long CanonicalNaNBits = 0x7FF8000000000000;

    private readonly struct Frame
    {
        public readonly YValue A, B;
        public readonly int Next;

        public Frame(YValue a, YValue b, int next)
        {
            A = a;
            B = b;
            Next = next;
        }
    }

    /// <summary>Wire equality: the same case and the same payload, where NaN equals NaN and +0 differs from -0 (Q03, Q04). Object members compare in order.</summary>
    public bool Equals(YValue other)
    {
        if (ReferenceEquals(payload, other.payload) && bits == other.bits) return true;
        var kind = Kind;
        if (kind != other.Kind) return false;
        return kind >= YValueKind.Array ? DeepEquals(this, other) : ScalarEquals(this, other, kind);
    }

    public override bool Equals(object? o) => o is YValue v && Equals(v);

    public static bool operator ==(YValue a, YValue b) => a.Equals(b);

    public static bool operator !=(YValue a, YValue b) => !a.Equals(b);

    private static bool ScalarEquals(in YValue a, in YValue b, YValueKind kind)
    {
        switch (kind)
        {
            case YValueKind.Undefined:
            case YValueKind.Null:
                return true;
            case YValueKind.Bool:
            case YValueKind.BigInt:
                return a.bits == b.bits;
            case YValueKind.Number:
                return a.bits == b.bits || (IsNaN(a.bits) && IsNaN(b.bits));
            case YValueKind.String:
                return string.Equals((string)a.payload!, (string)b.payload!, StringComparison.Ordinal);
            default:
                return ((byte[])a.payload!).AsSpan().SequenceEqual((byte[])b.payload!);
        }
    }

    private static bool IsNaN(long doubleBits) => double.IsNaN(BitConverter.Int64BitsToDouble(doubleBits));

    private static bool DeepEquals(YValue a, YValue b)
    {
        if (a.ChildCount != b.ChildCount) return false;
        var count = a.ChildCount;
        var i = 0;
        List<Frame>? stack = null;
        while (true)
        {
            var descended = false;
            for (; i < count; i++)
            {
                var ca = a.ChildAt(i, out var ka);
                var cb = b.ChildAt(i, out var kb);
                if (!string.Equals(ka, kb, StringComparison.Ordinal)) return false;
                var kind = ca.Kind;
                if (kind != cb.Kind) return false;
                if (kind < YValueKind.Array)
                {
                    if (!ScalarEquals(ca, cb, kind)) return false;
                    continue;
                }
                if (ReferenceEquals(ca.payload, cb.payload)) continue;
                if (ca.ChildCount != cb.ChildCount) return false;
                (stack ??= new List<Frame>()).Add(new Frame(a, b, i + 1));
                a = ca;
                b = cb;
                count = ca.ChildCount;
                i = 0;
                descended = true;
                break;
            }
            if (descended) continue;
            if (stack is null || stack.Count == 0) return true;
            var top = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            a = top.A;
            b = top.B;
            i = top.Next;
            count = a.ChildCount;
        }
    }

    /// <summary>Agrees with <see cref="Equals(YValue)"/>: NaN hashes as one value, and the order of array items and object members matters.</summary>
    public override int GetHashCode()
    {
        var kind = Kind;
        if (kind < YValueKind.Array)
        {
            var h = new HashCode();
            AddScalar(ref h, this, kind);
            return h.ToHashCode();
        }

        var hash = new HashCode();
        var stack = new List<(YValue Value, int Next)>();
        var current = this;
        var next = 0;
        hash.Add((int)kind);
        hash.Add(current.ChildCount);
        while (true)
        {
            var count = current.ChildCount;
            var descended = false;
            for (; next < count; next++)
            {
                var child = current.ChildAt(next, out var key);
                if (key is not null) hash.Add(key, StringComparer.Ordinal);
                var childKind = child.Kind;
                if (childKind < YValueKind.Array)
                {
                    AddScalar(ref hash, child, childKind);
                    continue;
                }
                hash.Add((int)childKind);
                hash.Add(child.ChildCount);
                stack.Add((current, next + 1));
                current = child;
                next = 0;
                descended = true;
                break;
            }
            if (descended) continue;
            if (stack.Count == 0) return hash.ToHashCode();
            (current, next) = stack[^1];
            stack.RemoveAt(stack.Count - 1);
        }
    }

    private static void AddScalar(ref HashCode h, in YValue v, YValueKind kind)
    {
        h.Add((int)kind);
        switch (kind)
        {
            case YValueKind.Bool:
            case YValueKind.BigInt:
                h.Add(v.bits);
                break;
            case YValueKind.Number:
                h.Add(IsNaN(v.bits) ? CanonicalNaNBits : v.bits);
                break;
            case YValueKind.String:
                h.Add((string)v.payload!, StringComparer.Ordinal);
                break;
            case YValueKind.Bytes:
            {
                var bytes = (byte[])v.payload!;
                h.AddBytes(bytes);
                break;
            }
        }
    }

    // ------------------------------------------------------------------ the YText attribute rule (Q27)

    /// <summary>
    /// Yjs <c>equalAttrs</c>: strict equality for scalars (NaN never equals NaN, +0 equals -0, Null differs from Undefined), and for two
    /// containers the same instance, or <c>equalFlat</c>: equal key counts and every key of one strictly equal to the same key of the other,
    /// where a nested container only counts as equal when it is the same instance. A value that was decoded is never the same instance as
    /// one built earlier, so remote attributes always compare as changed. The container type is not looked at, as in JS: <c>{"0":1}</c> equals <c>[1]</c>.
    /// </summary>
    public bool AttributeEquals(in YValue other)
    {
        var kind = Kind;
        var otherKind = other.Kind;
        var container = kind >= YValueKind.Bytes;
        if (container != (otherKind >= YValueKind.Bytes)) return false;
        if (!container) return kind == otherKind && StrictEquals(this, other, kind);
        if (ReferenceEquals(payload, other.payload)) return true;
        return FlatEquals(this, kind, other, otherKind);
    }

    /// <summary>JS <c>===</c> between two values of the same case; containers are equal only as the same instance.</summary>
    private static bool StrictEquals(in YValue a, in YValue b, YValueKind kind)
    {
        switch (kind)
        {
            case YValueKind.Undefined:
            case YValueKind.Null:
                return true;
            case YValueKind.Bool:
            case YValueKind.BigInt:
                return a.bits == b.bits;
            case YValueKind.Number:
                return BitConverter.Int64BitsToDouble(a.bits) == BitConverter.Int64BitsToDouble(b.bits);
            case YValueKind.String:
                return string.Equals((string)a.payload!, (string)b.payload!, StringComparison.Ordinal);
            default:
                return ReferenceEquals(a.payload, b.payload);
        }
    }

    private static bool FlatEquals(in YValue a, YValueKind ka, in YValue b, YValueKind kb)
    {
        if (ka == YValueKind.Bytes && kb == YValueKind.Bytes)
            return ((byte[])a.payload!).AsSpan().SequenceEqual((byte[])b.payload!);

        var count = FlatCount(a, ka);
        if (count != FlatCount(b, kb)) return false;
        if (ka == YValueKind.Array && kb == YValueKind.Array)
        {
            var x = (YValue[])a.payload!;
            var y = (YValue[])b.payload!;
            for (var i = 0; i < x.Length; i++)
            {
                var k = x[i].Kind;
                if (k != y[i].Kind || !StrictEquals(x[i], y[i], k)) return false;
            }
            return true;
        }
        for (var i = 0; i < count; i++)
        {
            var key = FlatEntry(a, ka, i, out var value);
            if (!FlatLookup(b, kb, key, out var found)) return false;
            var k = value.Kind;
            if (k != found.Kind || !StrictEquals(value, found, k)) return false;
        }
        return true;
    }

    private static int FlatCount(in YValue v, YValueKind kind) =>
        kind == YValueKind.Bytes ? ((byte[])v.payload!).Length : v.ChildCount;

    private static string FlatEntry(in YValue v, YValueKind kind, int i, out YValue value)
    {
        if (kind == YValueKind.Object)
        {
            var m = ((YObjectData)v.payload!).Members[i];
            value = m.Value;
            return m.Key;
        }
        value = kind == YValueKind.Bytes ? (double)((byte[])v.payload!)[i] : ((YValue[])v.payload!)[i];
        return i.ToString(CultureInfo.InvariantCulture);
    }

    private static bool FlatLookup(in YValue v, YValueKind kind, string key, out YValue value)
    {
        if (kind == YValueKind.Object) return ((YObjectData)v.payload!).TryGetValue(key, out value);
        value = default;
        if (!JsKeyOrder.IsArrayIndex(key, out var index)) return false;
        if (kind == YValueKind.Bytes)
        {
            var bytes = (byte[])v.payload!;
            if (index >= bytes.Length) return false;
            value = (double)bytes[index];
            return true;
        }
        var items = (YValue[])v.payload!;
        if (index >= items.Length) return false;
        value = items[index];
        return true;
    }
}
