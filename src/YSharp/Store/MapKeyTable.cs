using System;

namespace YSharp.Store;

/// <summary>
/// A map's key table (Yjs <c>parent._map</c>): key to the item that holds the key's current value, iterated in the order keys were first set (a
/// JS Map's insertion order, which Yjs observes when it deletes or collects a map's children and when it lists entries). Entries live in an
/// append-only list; open addressing with linear probing maps a key's hash to its entry's index plus one. Both are stored in chunks of at most
/// 64 KB so the table never reaches the large object heap. It stores no key text and no hashes: an item's key is read from the store's
/// <see cref="KeyTable"/>, and growing rehashes from that text. Keys are never removed (Yjs keeps a deleted key's last item in <c>_map</c>),
/// so the table needs no tombstones and the entry list never has holes.
/// </summary>
internal sealed class MapKeyTable
{
    private const int InitialCapacity = 8;

    private readonly KeyTable _keys;
    private readonly ChunkedList<Item> _entries = new();
    private ChunkedList<int> _buckets;
    private int _mask;

    internal MapKeyTable(KeyTable keys)
    {
        _keys = keys;
        _buckets = NewTable(InitialCapacity);
        _mask = InitialCapacity - 1;
    }

    public int Count => _entries.Count;

    internal long MaxChunkBytes => Math.Max(_buckets.MaxBlockBytes, _entries.MaxBlockBytes);

    /// <summary>The current item for <paramref name="key"/>, or null.</summary>
    internal Item? Get(ReadOnlySpan<char> key)
    {
        for (var i = string.GetHashCode(key) & _mask; ; i = (i + 1) & _mask)
        {
            var slot = _buckets[i];
            if (slot == 0) return null;
            var item = _entries[slot - 1];
            if (KeyOf(item).SequenceEqual(key)) return item;
        }
    }

    /// <summary>Makes <paramref name="item"/> the current item for its own key; a new key goes last in iteration order.</summary>
    internal void Set(Item item)
    {
        if ((_entries.Count + 1) * 4 > (_mask + 1) * 3) Grow();
        var keySlot = item.ParentSub;
        var key = _keys.Get(keySlot);
        for (var i = string.GetHashCode(key) & _mask; ; i = (i + 1) & _mask)
        {
            var slot = _buckets[i];
            if (slot == 0)
            {
                _entries.Add(item);
                _buckets[i] = _entries.Count;
                return;
            }

            var existing = _entries[slot - 1];
            if (existing.ParentSub == keySlot || KeyOf(existing).SequenceEqual(key))
            {
                _entries[slot - 1] = item;
                return;
            }
        }
    }

    /// <summary>Every current item, in the order its key was first set.</summary>
    internal void CopyItemsTo(ChunkedList<Item> destination)
    {
        for (var i = 0; i < _entries.Count; i++) destination.Add(_entries[i]);
    }

    private ReadOnlySpan<char> KeyOf(Item item) => _keys.Get(item.ParentSub);

    private void Grow()
    {
        var capacity = (_mask + 1) * 2;
        var mask = capacity - 1;
        var buckets = NewTable(capacity);
        for (var e = 0; e < _entries.Count; e++)
        {
            var i = string.GetHashCode(KeyOf(_entries[e])) & mask;
            while (buckets[i] != 0) i = (i + 1) & mask;
            buckets[i] = e + 1;
        }

        _buckets = buckets;
        _mask = mask;
    }

    private static ChunkedList<int> NewTable(int capacity)
    {
        var buckets = new ChunkedList<int>();
        for (var i = 0; i < capacity; i++) buckets.Add(0);
        return buckets;
    }
}
