using System;

namespace YSharp.Store;

/// <summary>
/// A map's key table (Yjs <c>parent._map</c>): key to the item that holds the key's current value. Open addressing with linear probing over a
/// bucket array of item references, stored in chunks of at most 64 KB so the table never reaches the large object heap. It stores no key text
/// and no hashes: an item's key is read from the store's <see cref="KeyTable"/>, and growing rehashes from that text. Keys are never removed
/// (Yjs keeps a deleted key's last item in <c>_map</c>), so the table needs no tombstones.
/// </summary>
internal sealed class MapKeyTable
{
    private const int InitialCapacity = 8;

    private readonly KeyTable _keys;
    private ChunkedList<Item?> _buckets;
    private int _mask;
    private int _count;

    internal MapKeyTable(KeyTable keys)
    {
        _keys = keys;
        _buckets = NewTable(InitialCapacity);
        _mask = InitialCapacity - 1;
    }

    public int Count => _count;

    internal long MaxChunkBytes => _buckets.MaxBlockBytes;

    /// <summary>The current item for <paramref name="key"/>, or null.</summary>
    internal Item? Get(ReadOnlySpan<char> key)
    {
        for (var i = string.GetHashCode(key) & _mask; ; i = (i + 1) & _mask)
        {
            var item = _buckets[i];
            if (item is null || KeyOf(item).SequenceEqual(key)) return item;
        }
    }

    /// <summary>Makes <paramref name="item"/> the current item for its own key.</summary>
    internal void Set(Item item)
    {
        if ((_count + 1) * 4 > (_mask + 1) * 3) Grow();
        var keySlot = item.ParentSub;
        var key = _keys.Get(keySlot);
        for (var i = string.GetHashCode(key) & _mask; ; i = (i + 1) & _mask)
        {
            var existing = _buckets[i];
            if (existing is null)
            {
                _buckets[i] = item;
                _count++;
                return;
            }

            if (existing.ParentSub == keySlot || KeyOf(existing).SequenceEqual(key))
            {
                _buckets[i] = item;
                return;
            }
        }
    }

    /// <summary>Every current item, in table order.</summary>
    internal void CopyItemsTo(ChunkedList<Item> destination)
    {
        for (var i = 0; i <= _mask; i++) if (_buckets[i] is { } item) destination.Add(item);
    }

    private ReadOnlySpan<char> KeyOf(Item item) => _keys.Get(item.ParentSub);

    private void Grow()
    {
        var capacity = (_mask + 1) * 2;
        var mask = capacity - 1;
        var buckets = NewTable(capacity);
        for (var b = 0; b <= _mask; b++)
        {
            var item = _buckets[b];
            if (item is null) continue;
            var i = string.GetHashCode(KeyOf(item)) & mask;
            while (buckets[i] is not null) i = (i + 1) & mask;
            buckets[i] = item;
        }

        _buckets = buckets;
        _mask = mask;
    }

    private static ChunkedList<Item?> NewTable(int capacity)
    {
        var buckets = new ChunkedList<Item?>();
        for (var i = 0; i < capacity; i++) buckets.Add(null);
        return buckets;
    }
}
