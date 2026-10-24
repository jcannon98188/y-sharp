using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using YSharp.StoreSpike.Content;

namespace YSharp.StoreSpike.Arenas;

[Flags]
internal enum ItemFlags : byte
{
    None = 0,
    Deleted = 1,
    Marker = 2,
}

/// <summary>
/// One item, inline in an arena chunk. It holds no reference, so the GC never traces it. Ids are (client index, clock): the client index is the
/// position in the <see cref="ClientTable"/>, which maps it to the 53-bit client id. -1 means "none" for every link and origin.
/// </summary>
internal struct ItemSlot
{
    public int Client;
    public int Clock;
    public int Length;
    public int Left;
    public int Right;
    public int OriginClient;
    public int OriginClock;
    public int RightOriginClient;
    public int RightOriginClock;
    public int Parent;

    /// <summary>The map key, as the index of the key's payload in <c>MapPayloads</c>; -1 for list items.</summary>
    public int ParentSub;

    public int Payload0;
    public int Payload1;
    public int Generation;
    public ContentKind Kind;
    public ItemFlags Flags;

    public readonly bool IsDeleted => (Flags & ItemFlags.Deleted) != 0;

    /// <summary>Visible, countable length; deleted items count 0.</summary>
    public readonly int CountableLength => IsDeleted ? 0 : Length;
}

/// <summary>
/// Item slots in chunks of at most <see cref="ChunkLimits.ChunkBytes"/>. Slots never move, so a <c>ref</c> to one stays valid while chunks are added.
/// Freed slots form a free list threaded through <see cref="ItemSlot.Right"/> and are reused first; freeing bumps the generation (see <see cref="ItemRef"/>).
/// </summary>
internal sealed class StructArena
{
    public static readonly int ChunkSlots = ChunkLimits.ChunkBytes / Unsafe.SizeOf<ItemSlot>();

    private readonly List<ItemSlot[]> _chunks = new();
    private int _highWater;
    private int _freeHead = -1;
    private int _freeCount;

    /// <summary>Slots in use: allocated and not on the free list.</summary>
    public int LiveCount => _highWater - _freeCount;

    public int FreeCount => _freeCount;

    public long MaxChunkBytes => _chunks.Count == 0 ? 0 : (long)ChunkSlots * Unsafe.SizeOf<ItemSlot>();

    public ref ItemSlot this[int index] => ref _chunks[index / ChunkSlots][index % ChunkSlots];

    /// <summary>A cleared slot, reused from the free list when there is one. Its generation is kept.</summary>
    public int Allocate()
    {
        if (_freeHead >= 0)
        {
            var reused = _freeHead;
            ref var slot = ref this[reused];
            _freeHead = slot.Right;
            _freeCount--;
            var generation = slot.Generation;
            slot = default;
            slot.Generation = generation;
            return reused;
        }

        if (_highWater == _chunks.Count * ChunkSlots) _chunks.Add(new ItemSlot[ChunkSlots]);
        return _highWater++;
    }

    public void Free(int index)
    {
        ref var slot = ref this[index];
        var generation = slot.Generation + 1;
        slot = default;
        slot.Generation = generation;
        slot.Kind = ContentKind.Free;
        slot.Right = _freeHead;
        _freeHead = index;
        _freeCount++;
    }

    public ItemRef RefOf(int index) => new(index, this[index].Generation);

    /// <summary>The slot index of <paramref name="item"/>, or an exception when the handle is stale (its slot was freed or reused).</summary>
    public int Resolve(ItemRef item)
    {
        if (!IsLive(item)) throw new InvalidOperationException("stale ItemRef: the item was merged away and its slot freed or reused");
        return item.Index;
    }

    public bool IsLive(ItemRef item) =>
        item.Index >= 0 && item.Index < _highWater && this[item.Index].Generation == item.Generation && this[item.Index].Kind != ContentKind.Free;
}
