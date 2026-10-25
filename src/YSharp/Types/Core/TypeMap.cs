#nullable enable
using System;
using System.Collections.Generic;
using YSharp.Store;

namespace YSharp;

// The map core under YMap and the XML attributes (Yjs AbstractType.js: typeMapSet, typeMapGet, typeMapHas, typeMapDelete, typeMapGetAll). Each
// key's current item is the right end of its chain; a new value is integrated to its right and deletes it (last writer wins). The caller holds
// the document lock.
internal static class TypeMap
{
    /// <summary>
    /// Yjs <c>typeMapSet</c>: undefined and null are values, as in JS (M-3). Throws <see cref="ArgumentException"/> for a type that is already
    /// integrated (T-0.13) before changing anything.
    /// </summary>
    public static void Set(Transaction tx, YType type, string key, in YEntry value)
    {
        ArgumentNullException.ThrowIfNull(key);
        var doc = type.AttachedDoc ?? throw new InvalidOperationException("The type is not integrated.");
        TypeList.Validate(new ReadOnlySpan<YEntry>(in value), undefinedAllowed: true);
        var store = doc.Store;
        var branch = type.Branch;
        var left = new ItemRef(branch.Map?.Get(key));
        ContentKind kind;
        int content;
        if (value.Kind == YEntryKind.Any)
        {
            kind = ContentKind.Any;
            content = store.AppendValue(value.Value);
        }
        else
        {
            (kind, var payload) = TypeList.Payload(value);
            content = store.AddPayload(payload);
        }

        var item = store.CreateLocalItem(doc.ClientId, branch, left, ItemRef.Null, key, true, kind, content, 1);
        doc.IntegrateLocal(tx, item);
    }

    /// <summary>Yjs <c>typeMapDelete</c>: deletes the key's current item, if any.</summary>
    public static void Delete(Transaction tx, YType type, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (type.AttachedDoc is not { } doc) return;
        var current = new ItemRef(type.Branch.Map?.Get(key));
        if (!current.IsNull) doc.DeleteItem(tx, current);
    }

    /// <summary>Yjs <c>typeMapGet</c>: the last element of the key's current item, or false when it is absent or deleted.</summary>
    public static bool TryGet(YType type, string key, out YEntry value)
    {
        ArgumentNullException.ThrowIfNull(key);
        value = default;
        if (type.AttachedDoc is not { } doc) return false;
        var current = new ItemRef(type.Branch.Map?.Get(key));
        if (current.IsNull || current.IsDeleted) return false;
        value = TypeList.ContentAt(doc, current, (int)current.Length - 1);
        return true;
    }

    /// <summary>Yjs <c>typeMapHas</c>.</summary>
    public static bool Has(YType type, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (type.AttachedDoc is null) return false;
        var current = type.Branch.Map?.Get(key);
        return current is not null && !current.Deleted;
    }

    /// <summary>
    /// Yjs <c>typeMapGetAll</c>: every key whose current item is not deleted, with its value, in the order the keys first appeared (a JS Map's
    /// insertion order; a key's slot is allocated when the key is first stored).
    /// </summary>
    public static List<KeyValuePair<string, YEntry>> Entries(YType type)
    {
        var entries = new List<KeyValuePair<string, YEntry>>();
        if (type.AttachedDoc is not { } doc) return entries;
        var items = new List<ItemRef>();
        StructStore.CopyMapItems(type.Branch, items);
        items.Sort(static (a, b) => a.Item!.ParentSub.CompareTo(b.Item!.ParentSub));
        foreach (var item in items)
        {
            if (item.IsDeleted) continue;
            entries.Add(new(doc.Store.ParentSubOf(item)!, TypeList.ContentAt(doc, item, (int)item.Length - 1)));
        }

        return entries;
    }
}
