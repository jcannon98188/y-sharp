#nullable enable
using System;
using System.Collections.Generic;

namespace YSharp;

/// <summary>
/// The members of an Object value, in insertion order. JS array-index-first ordering is applied only on encode and JSON output. Keys are
/// unique; the view never allocates.
/// </summary>
public readonly struct YObjectView
{
    private readonly YObjectData? data;

    internal YObjectView(YObjectData data) => this.data = data;

    public int Count => data?.Members.Length ?? 0;

    public KeyValuePair<string, YValue> this[int i] => (data?.Members ?? System.Array.Empty<KeyValuePair<string, YValue>>())[i];

    public bool TryGetValue(string key, out YValue v)
    {
        if (data is not null && data.TryGetValue(key, out v)) return true;
        v = default;
        return false;
    }
}

/// <summary>The storage behind an Object value: the members, and a key index once there are enough of them for a scan to be slow.</summary>
internal sealed class YObjectData
{
    /// <summary>Up to this many members a linear scan beats a dictionary.</summary>
    private const int LinearLimit = 8;

    private readonly Dictionary<string, int>? index;

    private YObjectData(KeyValuePair<string, YValue>[] members, Dictionary<string, int>? index)
    {
        Members = members;
        this.index = index;
    }

    public KeyValuePair<string, YValue>[] Members { get; }

    /// <summary>Builds from members that may repeat a key: a repeated key keeps its first position and takes the last value (Q09).</summary>
    public static YObjectData Create(ReadOnlySpan<KeyValuePair<string, YValue>> input)
    {
        if (input.IsEmpty) return new YObjectData(System.Array.Empty<KeyValuePair<string, YValue>>(), null);
        return Dedupe(input.ToArray());
    }

    /// <summary>Takes ownership of <paramref name="members"/>, which may repeat a key, and compacts it in place.</summary>
    public static YObjectData Dedupe(KeyValuePair<string, YValue>[] members)
    {
        var map = members.Length > LinearLimit ? new Dictionary<string, int>(members.Length, StringComparer.Ordinal) : null;
        var n = 0;
        foreach (var m in members)
        {
            if (m.Key is null) throw new ArgumentNullException(nameof(members), "An object key cannot be null.");
            var at = -1;
            if (map is not null)
            {
                if (!map.TryGetValue(m.Key, out at)) at = -1;
            }
            else
            {
                for (var i = 0; i < n; i++)
                {
                    if (!string.Equals(members[i].Key, m.Key, StringComparison.Ordinal)) continue;
                    at = i;
                    break;
                }
            }
            if (at >= 0)
            {
                members[at] = new KeyValuePair<string, YValue>(members[at].Key, m.Value);
            }
            else
            {
                map?.Add(m.Key, n);
                members[n++] = m;
            }
        }
        if (n != members.Length) System.Array.Resize(ref members, n);
        return new YObjectData(members, n > LinearLimit ? map : null);
    }

    public bool TryGetValue(string key, out YValue value)
    {
        if (index is not null)
        {
            if (index.TryGetValue(key, out var at)) { value = Members[at].Value; return true; }
        }
        else
        {
            foreach (var m in Members)
            {
                if (!string.Equals(m.Key, key, StringComparison.Ordinal)) continue;
                value = m.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
