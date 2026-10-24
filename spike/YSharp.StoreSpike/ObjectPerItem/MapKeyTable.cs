using System;
using YSharp.StoreSpike.Content;

namespace YSharp.StoreSpike.ObjectPerItem;

/// <summary>
/// The map key table (Yjs <c>parent._map</c>): key to the item that holds the key's current value. Open addressing with linear probing over
/// a bucket array of item references and a parallel array of key hashes, both stored in chunks of at most 64 KB, so the table never reaches
/// the large object heap. It stores no key strings: an item's key is the key text of its payload in <see cref="MapPayloads"/>, read back as
/// a span only when the stored hashes match. Growing rehashes from the stored hashes without reading any key. Keys are never removed (Yjs
/// keeps a deleted key's last item in <c>_map</c>), so the table needs no tombstones.
/// </summary>
public sealed class MapKeyTable
{
    private const int InitialCapacity = 16;

    private readonly MapPayloads _payloads;
    private SmallStartList<Item?> _buckets;
    private SmallStartList<int> _hashes;
    private int _mask;
    private int _count;

    // The bucket and hash the last Get ended on, for SetLookedUp; -1 when stale.
    private int _lookedUpBucket = -1;
    private int _lookedUpHash;

    internal MapKeyTable(MapPayloads payloads)
    {
        _payloads = payloads;
        (_buckets, _hashes) = NewTable(InitialCapacity);
        _mask = InitialCapacity - 1;
    }

    public int Count => _count;

    internal long MaxChunkBytes => Math.Max(_buckets.MaxBlockBytes, _hashes.MaxBlockBytes);

    /// <summary>The current item for <paramref name="key"/>, or null.</summary>
    internal Item? Get(ReadOnlySpan<char> key)
    {
        var hash = string.GetHashCode(key);
        for (var i = hash & _mask; ; i = (i + 1) & _mask)
        {
            var item = _buckets[i];
            if (item is null || (_hashes[i] == hash && KeyOf(item).SequenceEqual(key)))
            {
                _lookedUpBucket = i;
                _lookedUpHash = hash;
                return item;
            }
        }
    }

    /// <summary>Makes <paramref name="item"/> the current item for its own key.</summary>
    internal void Set(Item item)
    {
        _lookedUpBucket = -1;
        if ((_count + 1) * 4 > (_mask + 1) * 3) Grow();
        var key = KeyOf(item);
        var hash = string.GetHashCode(key);
        for (var i = hash & _mask; ; i = (i + 1) & _mask)
        {
            var existing = _buckets[i];
            if (existing is null)
            {
                _buckets[i] = item;
                _hashes[i] = hash;
                _count++;
                return;
            }

            if (_hashes[i] == hash && KeyOf(existing).SequenceEqual(key))
            {
                _buckets[i] = item;
                return;
            }
        }
    }

    /// <summary>
    /// <see cref="Set"/> for an item whose key is the key of the immediately preceding <see cref="Get"/>: it reuses the bucket and hash that
    /// lookup ended on, so a map set hashes and probes its key once instead of twice. Falls back to <see cref="Set"/> when the table must grow.
    /// </summary>
    internal void SetLookedUp(Item item)
    {
        var bucket = _lookedUpBucket;
        _lookedUpBucket = -1;
        var existing = bucket < 0 ? null : _buckets[bucket];
        if (bucket < 0 || (existing is null && (_count + 1) * 4 > (_mask + 1) * 3))
        {
            Set(item);
            return;
        }

        System.Diagnostics.Debug.Assert(string.GetHashCode(KeyOf(item)) == _lookedUpHash, "the item's key is not the looked-up key");
        _buckets[bucket] = item;
        if (existing is null)
        {
            _hashes[bucket] = _lookedUpHash;
            _count++;
        }
    }

    private ReadOnlySpan<char> KeyOf(Item item) => _payloads.KeyOf(item.ParentSub);

    private void Grow()
    {
        var capacity = (_mask + 1) * 2;
        var mask = capacity - 1;
        var (buckets, hashes) = NewTable(capacity);
        for (var b = 0; b <= _mask; b++)
        {
            var item = _buckets[b];
            if (item is null) continue;
            var hash = _hashes[b];
            var i = hash & mask;
            while (buckets[i] is not null) i = (i + 1) & mask;
            buckets[i] = item;
            hashes[i] = hash;
        }

        _buckets = buckets;
        _hashes = hashes;
        _mask = mask;
    }

    private static (SmallStartList<Item?>, SmallStartList<int>) NewTable(int capacity)
    {
        var buckets = new SmallStartList<Item?>();
        var hashes = new SmallStartList<int>();
        for (var i = 0; i < capacity; i++)
        {
            buckets.Add(null);
            hashes.Add(0);
        }

        return (buckets, hashes);
    }
}
