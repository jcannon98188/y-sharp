using System.Runtime.CompilerServices;

namespace YSharp.StoreSpike.ObjectPerItem;

/// <summary>
/// The opaque item handle of ADR 0021, as candidate A would provide it. Integration, codecs and types hold an <see cref="ItemRef"/> and call
/// small accessors; they never see whether an item is an object (this candidate) or an index into an arena (candidate B). Here the handle is
/// one reference wide, so passing it costs what passing a raw <see cref="Item"/> costs, and each accessor is one inlined field read. Accessors
/// that need document tables (client ids, origins) are on the store: <see cref="ObjectPerItemStore.ClientOf"/>,
/// <see cref="ObjectPerItemStore.OriginOf"/>, <see cref="ObjectPerItemStore.RightOriginOf"/>. The handle compares by identity, and
/// <see cref="IsNull"/> stands for Yjs's <c>null</c> neighbour.
/// </summary>
public readonly struct ItemRef : System.IEquatable<ItemRef>
{
    internal readonly Item? Item;

    internal ItemRef(Item? item) => Item = item;

    public bool IsNull => Item is null;

    public uint Clock
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Item!.Clock;
    }

    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Item!.Length;
    }

    public bool IsDeleted
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Item!.Deleted;
    }

    public bool IsCountable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Item!.Countable;
    }

    public ContentKind Kind
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Item!.Kind;
    }

    public ItemRef Left
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(Item!.Left);
    }

    public ItemRef Right
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(Item!.Right);
    }

    public bool Equals(ItemRef other) => ReferenceEquals(Item, other.Item);

    public override bool Equals(object? obj) => obj is ItemRef other && Equals(other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(Item);

    public static bool operator ==(ItemRef a, ItemRef b) => a.Equals(b);

    public static bool operator !=(ItemRef a, ItemRef b) => !a.Equals(b);
}
