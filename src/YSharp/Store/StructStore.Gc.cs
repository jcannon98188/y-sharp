using System.Collections.Generic;

namespace YSharp.Store;

// Accessors for transaction cleanup (spec 15, ADR 0021 additive): finding structs by client and walking a garbage-collected type's items.
internal sealed partial class StructStore
{
    /// <summary>Yjs <c>findIndexSS</c> on <paramref name="client"/>'s structs.</summary>
    public int FindIndex(ulong client, long clock) => Owner(client).FindIndex(clock);

    /// <summary>The client id of a struct that may since have merged into its left neighbour (its own fields still hold its id).</summary>
    public ItemId IdOfMergeStruct(int index)
    {
        var item = MergeStructs[index];
        return new ItemId(Clients[item.Client].Id, item.Clock);
    }

    /// <summary>
    /// Every item a type holds, for <c>ContentType.gc</c>: its sequence from the start rightwards, then each key's item and its overwritten
    /// predecessors leftwards. Appends to <paramref name="items"/>.
    /// </summary>
    public static void CollectContent(Branch branch, List<ItemRef> items)
    {
        for (var item = branch.Start; item is not null; item = item.Right) items.Add(new ItemRef(item));
        if (branch.Map is not { } map) return;
        var current = new ChunkedList<Item>();
        map.CopyItemsTo(current);
        for (var i = 0; i < current.Count; i++)
            for (var item = current[i]; item is not null; item = item.Left) items.Add(new ItemRef(item));
    }
}
