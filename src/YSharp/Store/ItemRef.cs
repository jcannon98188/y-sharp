using System;
using System.Runtime.CompilerServices;

namespace YSharp.Store;

/// <summary>
/// The opaque struct handle of ADR 0021 and ADR 0023. Integration, codecs and types hold an <see cref="ItemRef"/> and call small accessors; they
/// never see that a struct is an object. The handle is one reference wide, each accessor is one inlined field read, equality is reference
/// identity, and <see cref="IsNull"/> stands for Yjs's <c>null</c>. Accessors that need document tables (client ids, origins, keys, content)
/// are on <see cref="StructStore"/>. A handle kept across a commit may name a struct that has since merged into its left neighbour; re-find
/// by id, as Yjs code does.
/// </summary>
internal readonly struct ItemRef : IEquatable<ItemRef>
{
    internal readonly Item? Item;

    internal ItemRef(Item? item) => Item = item;

    public static ItemRef Null => default;

    public bool IsNull => Item is null;

    public long Clock
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Item!.Clock;
    }

    public long Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Item!.Length;
    }

    /// <summary>Clock of the last element (Yjs <c>lastId.clock</c>).</summary>
    public long LastClock => Item!.LastClock;

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

    public bool IsKeep => Item!.Keep;

    /// <summary>True for a GC struct (Yjs <c>GC</c>), false for an item.</summary>
    public bool IsGc => Item!.IsGc;

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

    public Branch? Parent => Item!.Parent;

    /// <summary>True when the item has a map key (Yjs <c>parentSub !== null</c>).</summary>
    public bool HasParentSub => Item!.ParentSub >= 0;

    public bool Equals(ItemRef other) => ReferenceEquals(Item, other.Item);

    public override bool Equals(object? obj) => obj is ItemRef other && Equals(other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(Item);

    public static bool operator ==(ItemRef a, ItemRef b) => a.Equals(b);

    public static bool operator !=(ItemRef a, ItemRef b) => !a.Equals(b);

    public override string ToString() => Item is null ? "null" : $"{Item.Kind}@{Item.Client}:{Item.Clock}+{Item.Length}";
}
