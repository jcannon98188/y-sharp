#nullable enable
using System;
using YSharp.Store;

namespace YSharp;

/// <summary>A read-only view of one item, handed to filters (<c>DocOptions.GcFilter</c>, <c>UndoManagerOptions.DeleteFilter</c>).</summary>
public readonly struct ItemView
{
    private readonly ItemRef item;
    private readonly StructStore? store;

    internal ItemView(ItemRef item, StructStore store)
    {
        this.item = item;
        this.store = store;
    }

    public ItemId Id => store is null ? throw new InvalidOperationException("The view is empty.") : store.IdOf(item);

    public bool IsDeleted => store is null ? throw new InvalidOperationException("The view is empty.") : item.IsDeleted;
}
