#nullable enable
using System;
using System.Collections.Generic;
using YSharp.Store;

namespace YSharp;

// Transaction cleanup's garbage collection and struct merging (TASK-4.15): the part of Yjs cleanupTransactions that runs in its finally block,
// before the client-id check and afterTransactionCleanup. Deleted content is released (ADR 0018 content rules) and adjacent structs merge.
public sealed partial class Doc
{
    private List<ItemRef>? _gcItems;
    private Stack<Branch>? _gcTypes;

    partial void HookGarbageCollection(Transaction tx)
    {
        var ds = tx.State.DeleteRanges;
        if (Gc) TryGcDeleteSet(ds);
        TryMergeDeleteSet(ds);
        MergeChangedClients(tx);
        MergeSplitStructs();
    }

    /// <summary>Yjs <c>tryGcDeleteSet</c>: every deleted item of the transaction that is not kept and passes the filter gives up its content.</summary>
    private void TryGcDeleteSet(DeleteRanges ds)
    {
        var store = Store;
        var filter = Options.GcFilter;
        for (var r = ds.Count - 1; r >= 0; r--)
        {
            var range = ds[r];
            var count = store.StructCount(range.Client);
            for (var si = store.FindIndex(range.Client, range.Clock); si < count; si++)
            {
                var s = store.StructAt(range.Client, si);
                if (s.Clock >= range.End) break;
                if (!s.IsGc && s.IsDeleted && !s.IsKeep && (filter is null || filter(new ItemView(s, store)))) GcItem(s, parentCollected: false);
            }
        }
    }

    /// <summary>
    /// Yjs <c>Item.gc</c>: a nested type first collects everything it holds, at any depth, into GC structs (iteratively, ADR 0005); then the item's
    /// content becomes deleted content, or the item itself a GC struct when its parent was collected.
    /// </summary>
    private void GcItem(ItemRef item, bool parentCollected)
    {
        var store = Store;
        if (item.Kind == ContentKind.Type)
        {
            var types = _gcTypes ??= new Stack<Branch>();
            var items = _gcItems ??= new List<ItemRef>();
            types.Push((Branch)store.GetPayload(item));
            while (types.Count > 0)
            {
                var type = types.Pop();
                items.Clear();
                StructStore.CollectContent(type, items);
                foreach (var child in items)
                {
                    if (child.IsGc) continue;
                    if (!child.IsDeleted) throw new YInternalException("A collected type holds an item that is not deleted.");
                    if (child.Kind == ContentKind.Type) types.Push((Branch)store.GetPayload(child));
                    store.ReplaceWithGc(child);
                }

                type.ClearContent();
            }

            items.Clear();
        }

        if (parentCollected) store.ReplaceWithGc(item);
        else store.GcContent(item);
    }

    /// <summary>Yjs <c>tryMergeDeleteSet</c>: merges deleted and collected structs, from the right of each deleted range leftwards.</summary>
    private void TryMergeDeleteSet(DeleteRanges ds)
    {
        var store = Store;
        for (var r = ds.Count - 1; r >= 0; r--)
        {
            var range = ds[r];
            var client = range.Client;
            var si = Math.Min(store.StructCount(client) - 1, 1 + store.FindIndex(client, range.End - 1));
            while (si > 0 && store.StructAt(client, si).Clock >= range.Clock) si -= 1 + store.TryToMergeWithLefts(client, si);
        }
    }

    /// <summary>Merges the structs every client added in this transaction with their left neighbours, right to left.</summary>
    private void MergeChangedClients(Transaction tx)
    {
        var store = Store;
        for (var rank = 0; rank < store.ClientCount; rank++)
        {
            var client = store.ClientIdAtRank(rank);
            var before = tx.BeforeClock(client);
            if (before == tx.AfterClock(client)) continue;
            var count = store.StructCount(client);
            if (count == 0) continue;
            var firstChange = Math.Max(store.FindIndex(client, before), 1);
            for (var i = count - 1; i >= firstChange;) i -= 1 + store.TryToMergeWithLefts(client, i);
        }
    }

    /// <summary>Merges the structs split or replaced during the transaction (Yjs <c>transaction._mergeStructs</c>) with their neighbours.</summary>
    private void MergeSplitStructs()
    {
        var store = Store;
        for (var i = store.MergeStructs.Count - 1; i >= 0; i--)
        {
            var id = store.IdOfMergeStruct(i);
            var count = store.StructCount(id.Client);
            var pos = store.FindIndex(id.Client, id.Clock);
            if (pos + 1 < count && store.TryToMergeWithLefts(id.Client, pos + 1) > 1) continue;   // both are merged already
            if (pos > 0) store.TryToMergeWithLefts(id.Client, pos);
        }

        store.MergeStructs.Clear();
    }
}
