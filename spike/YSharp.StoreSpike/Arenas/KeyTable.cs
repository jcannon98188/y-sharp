using System;
using YSharp.StoreSpike.Content;

namespace YSharp.StoreSpike.Arenas;

/// <summary>
/// The map key table: key to the slot of the key's current item (Yjs <c>parent._map</c>). Open addressing with linear probing over a
/// <see cref="ChunkedList{T}"/> of slot indexes, so it grows in chunks and holds no string object per key. The key text is read back from the item's
/// payload, which keeps the key once in the shared text buffer.
/// </summary>
internal sealed class KeyTable
{
    private const int Empty = -1;

    private readonly StructArena _arena;
    private readonly MapPayloads _payloads;
    // Allocated on the first key: an empty table costs nothing (a ChunkedList block is a full 64 KB).
    private ChunkedList<int>? _buckets;
    private int _count;

    public KeyTable(StructArena arena, MapPayloads payloads)
    {
        _arena = arena;
        _payloads = payloads;
    }

    public int Count => _count;

    public long MaxChunkBytes => _buckets?.MaxBlockBytes ?? 0;

    /// <summary>The slot of the current item for <paramref name="key"/>, or -1.</summary>
    public int Find(ReadOnlySpan<char> key)
    {
        if (_buckets is null) return -1;
        var mask = _buckets.Count - 1;
        for (var b = Hash(key) & mask; ; b = (b + 1) & mask)
        {
            var item = _buckets[b];
            if (item == Empty) return -1;
            if (KeyOf(item).SequenceEqual(key)) return item;
        }
    }

    /// <summary>Points <paramref name="key"/> at <paramref name="item"/>, adding the key or replacing its previous item.</summary>
    public void Set(ReadOnlySpan<char> key, int item)
    {
        _buckets ??= NewBuckets(16);
        if ((_count + 1) * 2 > _buckets.Count) Grow();
        var mask = _buckets.Count - 1;
        for (var b = Hash(key) & mask; ; b = (b + 1) & mask)
        {
            var existing = _buckets[b];
            if (existing == Empty)
            {
                _buckets[b] = item;
                _count++;
                return;
            }

            if (KeyOf(existing).SequenceEqual(key))
            {
                _buckets[b] = item;
                return;
            }
        }
    }

    private ReadOnlySpan<char> KeyOf(int item) => _payloads.KeyOf(_arena[item].ParentSub);

    private void Grow()
    {
        var old = _buckets!;
        _buckets = NewBuckets(old.Count * 2);
        var mask = _buckets.Count - 1;
        for (var i = 0; i < old.Count; i++)
        {
            var item = old[i];
            if (item == Empty) continue;
            var b = Hash(KeyOf(item)) & mask;
            while (_buckets[b] != Empty) b = (b + 1) & mask;
            _buckets[b] = item;
        }
    }

    private static ChunkedList<int> NewBuckets(int capacity)
    {
        var buckets = new ChunkedList<int>();
        for (var i = 0; i < capacity; i++) buckets.Add(Empty);
        return buckets;
    }

    private static int Hash(ReadOnlySpan<char> key) => string.GetHashCode(key) & int.MaxValue;
}
