using System.Collections.Generic;

namespace YSharp.Store;

// Accessors that item integration (TASK-4.13, Integration/) needs through the ItemRef seam (ADR 0021): origin comparison, the struct an
// origin names, the start of a sequence or of a key's chain, and a type's item. Additive: none of them changes the layout operations.
internal sealed partial class StructStore
{
    /// <summary>Yjs <c>compareIDs(a.origin, b.origin)</c>: both absent, or the same id.</summary>
    public bool SameOrigin(ItemRef a, ItemRef b)
    {
        var x = a.Item!;
        var y = b.Item!;
        return x.OriginClient == y.OriginClient && (x.OriginClient == Item.NoClient || x.OriginClock == y.OriginClock);
    }

    /// <summary>Yjs <c>compareIDs(a.rightOrigin, b.rightOrigin)</c>.</summary>
    public bool SameRightOrigin(ItemRef a, ItemRef b)
    {
        var x = a.Item!;
        var y = b.Item!;
        return x.RightOriginClient == y.RightOriginClient && (x.RightOriginClient == Item.NoClient || x.RightOriginClock == y.RightOriginClock);
    }

    public bool HasOrigin(ItemRef item) => item.Item!.OriginClient != Item.NoClient;

    /// <summary>Yjs <c>getItem(store, item.origin)</c>: the struct holding the origin id, without splitting it.</summary>
    public ItemRef OriginItem(ItemRef item)
    {
        var i = item.Item!;
        var owner = Clients[i.OriginClient];
        return new(owner.Structs[owner.FindIndex(i.OriginClock)]);
    }

    /// <summary>The client id of <paramref name="item"/>'s own client, compared as Yjs compares <c>id.client</c>.</summary>
    public ulong ClientIdOf(ItemRef item) => Clients[item.Item!.Client].Id;

    /// <summary>The first item of <paramref name="parent"/>'s sequence (Yjs <c>parent._start</c>), deleted or not.</summary>
    public static ItemRef StartOf(Branch parent) => new(parent.Start);

    /// <summary>
    /// The leftmost item of the key chain that <paramref name="item"/>'s key names in <paramref name="parent"/> (Yjs: <c>_map.get(parentSub)</c>,
    /// then <c>.left</c> to the end), or null when the key has no item yet.
    /// </summary>
    public ItemRef FirstOfKey(Branch parent, ItemRef item)
    {
        var r = parent.Map?.Get(Keys.Get(item.Item!.ParentSub));
        while (r?.Left is not null) r = r.Left;
        return new(r);
    }

    /// <summary>The item whose content is <paramref name="branch"/> (Yjs <c>type._item</c>); null for a root type.</summary>
    public static ItemRef ItemOf(Branch branch) => new(branch.Item);

    /// <summary>Makes <paramref name="item"/> the item of <paramref name="branch"/> (the storage half of Yjs <c>type._integrate</c>).</summary>
    public static void Bind(Branch branch, ItemRef item) => branch.Item = item.Item;

    /// <summary>The current item of every key of <paramref name="branch"/>'s map (Yjs <c>_map.values()</c>), into <paramref name="destination"/>.</summary>
    public static void CopyMapItems(Branch branch, List<ItemRef> destination)
    {
        if (branch.Map is not { } map) return;
        var items = new ChunkedList<Item>();
        map.CopyItemsTo(items);
        for (var i = 0; i < items.Count; i++) destination.Add(new ItemRef(items[i]));
    }

    /// <summary>Records <paramref name="item"/> in <see cref="MergeStructs"/> so cleanup tries to merge it (Yjs <c>transaction._mergeStructs.push</c>).</summary>
    public void AddMergeStruct(ItemRef item) => MergeStructs.Add(item.Item!);
}
