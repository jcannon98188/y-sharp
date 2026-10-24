namespace YSharp.StoreSpike.ObjectPerItem;

/// <summary>What an item's two content fields mean. Payloads live in the shared content layer; an item holds indexes, never a payload object.</summary>
public enum ContentKind : byte
{
    /// <summary>Released content: only the length is kept (Yjs <c>ContentDeleted</c>).</summary>
    Deleted = 0,

    /// <summary>Text: <c>ContentA</c> is the text-buffer chunk, <c>ContentB</c> the start; the slice length is the item length.</summary>
    Text = 1,

    /// <summary>Array scalars: <c>ContentA</c> is the first index in the 8-byte value column; the run length is the item length.</summary>
    Values = 2,

    /// <summary>One map value: <c>ContentA</c> is the payload index in <see cref="Content.MapPayloads"/> (key and 8-byte value).</summary>
    MapValue = 3,
}

/// <summary>Per-item bit flags, as Yjs <c>Item.info</c>.</summary>
[System.Flags]
public enum ItemFlags : byte
{
    None = 0,
    Keep = 1,
    Countable = 2,
    Deleted = 4,
    Marker = 8,
}

/// <summary>
/// Candidate A's item: one heap object per item. Neighbour links are direct references; ids, origins and the parent sub-key are inline values
/// (a client is its slot in the client table plus a clock), so no link needs a separate object (ADR 0018). The item carries no finalizer, lock,
/// event, delegate or closure. Content is two indexes into the shared content layer.
/// Layout on x64: 16 bytes of object header, 3 references, 10 ints and 2 bytes, 88 bytes in all.
/// </summary>
public sealed class Item
{
    internal Item? Left;
    internal Item? Right;
    internal Branch Parent;

    /// <summary>Own id: client slot and clock.</summary>
    internal int Client;
    internal uint Clock;

    /// <summary>Origin id (the left neighbour's last id at creation); <see cref="NoClient"/> when there is none.</summary>
    internal int OriginClient;
    internal uint OriginClock;

    /// <summary>Right-origin id (the right neighbour's id at creation); <see cref="NoClient"/> when there is none.</summary>
    internal int RightOriginClient;
    internal uint RightOriginClock;

    internal int Length;
    internal int ContentA;
    internal int ContentB;

    /// <summary>The map key, as the payload index that holds the key text; <see cref="NoParentSub"/> for sequence items.</summary>
    internal int ParentSub;

    internal ContentKind Kind;
    internal ItemFlags Flags;

    public const int NoClient = -1;
    public const int NoParentSub = -1;

    internal Item(Branch parent) => Parent = parent;

    internal bool Deleted => (Flags & ItemFlags.Deleted) != 0;

    internal bool Countable => (Flags & ItemFlags.Countable) != 0;

    internal bool Marker
    {
        get => (Flags & ItemFlags.Marker) != 0;
        set => Flags = value ? Flags | ItemFlags.Marker : Flags & ~ItemFlags.Marker;
    }

    /// <summary>Clock of the last element in this item (Yjs <c>lastId</c>).</summary>
    internal uint LastClock => Clock + (uint)Length - 1;
}
