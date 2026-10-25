#nullable enable
using System;
using System.Collections.Generic;
using YSharp.Store;

namespace YSharp;

// ---------------------------------------------------------------- YMap

// Yjs YMap over the map core (Types/Core/TypeMap.cs). Every member takes an implicit transaction unless the calling thread has one open
// (CC-8). Until it is integrated, writes go to the prelim entries as JS sets _prelimContent (a Map, so a key keeps its first position until it
// is deleted); reads see nothing (T-0.4), and integration sets every entry in the integrating transaction (T-0.6).
public class YMap : YType
{
    internal override byte WireTypeRef => Store.TypeRefs.Map;

    private OrderedDictionary<string, YEntry>? _prelim = new(StringComparer.Ordinal);

    public YMap() { }

    /// <summary>Yjs <c>new YMap(entries)</c>: a prelim map holding <paramref name="entries"/>; a repeated key keeps its first position and last value.</summary>
    public YMap(IEnumerable<KeyValuePair<string, YEntry>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var (key, value) in entries) SetPrelim(key, value);
    }

    /// <summary>Yjs <c>size</c>: the keys whose current item is not deleted.</summary>
    public int Count
    {
        get
        {
            if (AttachedDoc is not { } doc) return 0;
            var h = doc.EnterRead();
            try
            {
                return TypeMap.Entries(this).Count;
            }
            finally
            {
                doc.Leave(h);
            }
        }
    }

    /// <summary>Yjs <c>set</c> of a shared type: returns <paramref name="type"/>, which is integrated once this returns on an integrated map (T-0.12).</summary>
    public T Set<T>(string key, T type) where T : YType
    {
        ArgumentNullException.ThrowIfNull(type);
        Set(key, (YEntry)type);
        return type;
    }

    /// <summary>Yjs <c>set</c>: undefined and null are values (M-3).</summary>
    public void Set(string key, YEntry value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (AttachedDoc is not { } doc)
        {
            SetPrelim(key, value);
            return;
        }

        var h = doc.EnterWrite(null, local: true);
        try
        {
            TypeMap.Set(h.Tx, this, key, value);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>Yjs <c>get</c>: false only when the key is absent or deleted; a key set to undefined is present (T-0.11).</summary>
    public bool TryGet(string key, out YEntry value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (AttachedDoc is not { } doc)
        {
            value = default;
            return false;
        }

        var h = doc.EnterRead();
        try
        {
            return TypeMap.TryGet(this, key, out value);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>Yjs <c>has</c>: true for a key set to undefined (T-0.11).</summary>
    public bool Has(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (AttachedDoc is not { } doc) return false;
        var h = doc.EnterRead();
        try
        {
            return TypeMap.Has(this, key);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>
    /// Yjs <c>delete</c>: true when a live entry was deleted. The write transaction runs even when the key is absent, as in JS (M-4); it changes
    /// nothing.
    /// </summary>
    public bool Delete(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (AttachedDoc is not { } doc) return _prelim!.Remove(key);
        var h = doc.EnterWrite(null, local: true);
        try
        {
            return TypeMap.Delete(h.Tx, this, key);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>Yjs <c>clear</c>: deletes every key in one transaction (M-5).</summary>
    public void Clear()
    {
        if (AttachedDoc is not { } doc)
        {
            _prelim!.Clear();
            return;
        }

        var h = doc.EnterWrite(null, local: true);
        try
        {
            foreach (var (key, _) in TypeMap.Entries(this)) TypeMap.Delete(h.Tx, this, key);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>Yjs <c>keys()</c> as a snapshot, in the order the keys were first stored (Q26).</summary>
    public IReadOnlyList<string> Keys
    {
        get
        {
            var entries = Snapshot();
            var keys = new string[entries.Length];
            for (var i = 0; i < entries.Length; i++) keys[i] = entries[i].Key;
            return keys;
        }
    }

    public MapEnumerator GetEnumerator()   // bound to an explicit transaction; snapshot otherwise (CC-9)
    {
        if (AttachedDoc is { CurrentTransaction: { } tx }) return new MapEnumerator(Snapshot(), tx);
        return new MapEnumerator(Snapshot(), null);
    }

    /// <summary>Yjs <c>YMap.clone</c>: a prelim map with the entries, nested types cloned.</summary>
    public override YType Clone()
    {
        var copy = new YMap();
        foreach (var (key, value) in Snapshot()) copy._prelim![key] = value.Kind == YEntryKind.Type ? value.Type!.Clone() : value;
        return copy;
    }

    /// <summary>Yjs <c>YMap.toJSON</c>: nested types converted; a key set to undefined stays a member (M-7).</summary>
    public override YValue ToJson()
    {
        var entries = Snapshot();
        var members = new KeyValuePair<string, YValue>[entries.Length];
        for (var i = 0; i < entries.Length; i++) members[i] = new(entries[i].Key, ListEnumerator.Json(entries[i].Value));
        return YValue.Object(members);
    }

    /// <summary>Yjs <c>YMap._integrate</c>: every prelim entry is set, in order, inside the integrating transaction.</summary>
    internal override void OnIntegrated(Transaction tx)
    {
        var prelim = _prelim;
        _prelim = null;
        if (prelim is null) return;
        foreach (var (key, value) in prelim) TypeMap.Set(tx, this, key, value);
    }

    private void SetPrelim(string key, in YEntry value)
    {
        ArgumentNullException.ThrowIfNull(key);
        TypeList.Validate(new ReadOnlySpan<YEntry>(in value), undefinedAllowed: true);
        _prelim![key] = value;
    }

    private KeyValuePair<string, YEntry>[] Snapshot()
    {
        if (AttachedDoc is not { } doc) return Array.Empty<KeyValuePair<string, YEntry>>();
        var h = doc.EnterRead();
        try
        {
            return TypeMap.Entries(this).ToArray();
        }
        finally
        {
            doc.Leave(h);
        }
    }
}

/// <summary>
/// Enumerates a map's live entries. Taken inside an explicit transaction it throws <see cref="YLifetimeException"/> once that transaction ended;
/// taken outside one it holds no lock (CC-9). Either way it enumerates the entries as they were when it was taken.
/// </summary>
public struct MapEnumerator
{
    private readonly KeyValuePair<string, YEntry>[] _entries;
    private readonly Transaction? _tx;
    private int _index;

    internal MapEnumerator(KeyValuePair<string, YEntry>[] entries, Transaction? tx)
    {
        _entries = entries;
        _tx = tx;
        _index = -1;
    }

    public KeyValuePair<string, YEntry> Current => _entries[_index];

    public bool MoveNext()
    {
        if (_tx is { IsValid: false }) throw new YLifetimeException();
        return _entries is not null && ++_index < _entries.Length;
    }
}
