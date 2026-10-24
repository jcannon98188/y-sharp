#nullable enable
using System;
using System.Collections.Generic;

namespace YSharp.Lib0;

/// <summary>
/// The order JS <c>Object.keys</c> gives, which the Any encoder writes (Q08): keys that are canonical array indices
/// (0 to 2^32-2, no leading zero) first in ascending numeric order, then the other keys in insertion order.
/// </summary>
internal static class JsKeyOrder
{
    public static bool IsArrayIndex(string key, out uint index)
    {
        index = 0;
        if (key.Length == 0 || key.Length > 10) return false;
        if (key[0] == '0') return key.Length == 1;
        ulong value = 0;
        foreach (var c in key)
        {
            if (c < '0' || c > '9') return false;
            value = value * 10 + (ulong)(c - '0');
        }
        if (value > uint.MaxValue - 1) return false;
        index = (uint)value;
        return true;
    }

    /// <summary>Fills <paramref name="order"/> with the positions of <paramref name="keys"/> in encoding order. Both spans have the same length.</summary>
    public static void Order(ReadOnlySpan<string> keys, Span<int> order)
    {
        if (order.Length != keys.Length) throw new ArgumentException("order must be as long as keys.", nameof(order));
        var indexed = new (uint Index, int Position)[keys.Length];
        var count = 0;
        for (var i = 0; i < keys.Length; i++)
            if (IsArrayIndex(keys[i], out var n)) indexed[count++] = (n, i);
        Array.Sort(indexed, 0, count, Comparer<(uint Index, int Position)>.Create(
            (a, b) => a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Position.CompareTo(b.Position)));
        var o = 0;
        for (var i = 0; i < count; i++) order[o++] = indexed[i].Position;
        for (var i = 0; i < keys.Length; i++)
            if (!IsArrayIndex(keys[i], out _)) order[o++] = i;
    }
}
