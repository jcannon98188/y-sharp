namespace YSharp.StoreSpike.Arenas;

/// <summary>
/// The opaque item handle of ADR 0021 as candidate B implements it: a slot index into the <see cref="StructArena"/> plus the slot's generation.
/// Integration, codecs and types would hold only this value and call the store's accessors; they never see the slot struct.
/// </summary>
/// <remarks>
/// <para><b>Index-reuse policy: generation check.</b> A slot freed by a commit merge (the right half of <c>Item.mergeWith</c>) goes on a free list and
/// is reused by the next new item. Freeing bumps the slot's generation, so a handle taken before the merge no longer matches and every accessor
/// rejects it instead of reading the slot's new occupant. Links inside the arena (left, right, the key table, search markers) are plain indexes:
/// the store repoints them before it frees a slot, as Yjs repoints search markers in <c>mergeWith</c>.</para>
/// <para>Only merged-away slots are freed. A deleted or garbage-collected item keeps its slot, because the state vector and the delete set still
/// need its id and length (ADR 0018, Content).</para>
/// </remarks>
public readonly record struct ItemRef(int Index, int Generation)
{
    public static ItemRef Null => new(-1, 0);

    public bool IsNull => Index < 0;
}

/// <summary>What an item's payload fields refer to. <see cref="Free"/> marks a slot on the free list.</summary>
public enum ContentKind : byte
{
    Free = 0,

    /// <summary>A slice of the shared text buffer: payload 0 is the chunk, payload 1 the start; the item length is the slice length.</summary>
    Text = 1,

    /// <summary>A run of the shared 8-byte value column: payload 0 is the first column index; the item length is the run length.</summary>
    Array = 2,

    /// <summary>One map payload (key slice and value): payload 0 is its index in <c>MapPayloads</c>.</summary>
    Map = 3,

    /// <summary>Deleted content, length only. Its payload storage has been released (ADR 0018, Content).</summary>
    Deleted = 4,
}
