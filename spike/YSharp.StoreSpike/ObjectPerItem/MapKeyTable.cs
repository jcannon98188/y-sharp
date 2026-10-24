using System;
using YSharp.StoreSpike.Content;

namespace YSharp.StoreSpike.ObjectPerItem;

/// <summary>
/// The map key table (Yjs <c>parent._map</c>): key to the item that holds the key's current value. Open addressing with linear probing over
/// a bucket array of item references stored in chunks of at most 64 KB, so the table never reaches the large object heap. It stores no key
/// strings: an item's key is the key text of its payload in <see cref="MapPayloads"/>, read back as a span for hashing and comparing. Keys are
/// never removed (Yjs keeps a deleted key's last item in <c>_map</c>), so the table needs no tombstones.
/// </summary>
public sealed class MapKeyTable
{
    private const int InitialCapacity = 16;

    private readonly MapPayloads _payloads;
    private SmallStartList<Item?> _buckets;
    private int _mask;
    private int _count;

    internal MapKeyTable(MapPayloads payloads)
    {
        _payloads = payloads;
        _buckets = NewBuckets(InitialCapacity);
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
            if (item is null) return null;
            if (KeyOf(item).SequenceEqual(key)) return item;
        }
    }

    /// <summary>Makes <paramref name="item"/> the current item for its own key.</summary>
    internal void Set(Item item)
    {
        if ((_count + 1) * 4 > (_mask + 1) * 3) Grow();
        Place(_buckets, _mask, item, countNew: true);
    }

    private ReadOnlySpan<char> KeyOf(Item item) => _payloads.KeyOf(item.ParentSub);

    private void Place(SmallStartList<Item?> buckets, int mask, Item item, bool countNew)
    {
        var key = KeyOf(item);
        for (var i = string.GetHashCode(key) & mask; ; i = (i + 1) & mask)
        {
            var existing = buckets[i];
            if (existing is null)
            {
                buckets[i] = item;
                if (countNew) _count++;
                return;
            }

            if (KeyOf(existing).SequenceEqual(key))
            {
                buckets[i] = item;
                return;
            }
        }
    }

    private void Grow()
    {
        var capacity = (_mask + 1) * 2;
        var old = _buckets;
        var buckets = NewBuckets(capacity);
        var mask = capacity - 1;
        for (var i = 0; i < old.Count; i++)
        {
            var item = old[i];
            if (item is not null) Place(buckets, mask, item, countNew: false);
        }

        _buckets = buckets;
        _mask = mask;
    }

    private static SmallStartList<Item?> NewBuckets(int capacity)
    {
        var buckets = new SmallStartList<Item?>();
        for (var i = 0; i < capacity; i++) buckets.Add(null);
        return buckets;
    }
}
