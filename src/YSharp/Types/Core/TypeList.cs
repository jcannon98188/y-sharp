#nullable enable
using System;
using System.Collections.Generic;
using YSharp.Store;

namespace YSharp;

// The list core under YArray, YText and the XML list types (Yjs AbstractType.js: typeListToArray, typeListGet, typeListInsertGenerics,
// typeListInsertGenericsAfter, typeListDelete), over the branch's items and search markers. The caller holds the document lock; the
// mutators validate everything before the first change, so a rejected call changes nothing (ADR 0005).
internal static class TypeList
{
    /// <summary>Yjs <c>typeListToArray</c>: the content of every countable, undeleted item, in order.</summary>
    public static List<YEntry> ToEntries(YType type)
    {
        var entries = new List<YEntry>();
        if (type.AttachedDoc is not { } doc) return entries;
        for (var n = StructStore.StartOf(type.Branch); !n.IsNull; n = n.Right)
            if (n.IsCountable && !n.IsDeleted) AddContent(doc, n, entries);
        return entries;
    }

    /// <summary>Yjs <c>typeListGet</c>: the element at <paramref name="index"/>, found from the nearest search marker.</summary>
    public static YEntry Get(YType type, int index)
    {
        if (type.AttachedDoc is not { } doc || (uint)index >= (uint)type.Branch.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var branch = type.Branch;
        var n = StructStore.StartOf(branch);
        // a reader shares the lock with other readers, so it walks from the markers without updating them (CC-4)
        var hit = doc.HoldsWriteLock ? branch.MarkersFor(doc.Store)?.Find(branch, index) : branch.Markers?.Peek(branch, index);
        if (hit is { } marker)
        {
            n = new ItemRef(marker.Item);
            index -= marker.Index;
        }

        for (; !n.IsNull; n = n.Right)
        {
            if (n.IsDeleted || !n.IsCountable) continue;
            if (index < n.Length) return ContentAt(doc, n, index);
            index -= (int)n.Length;
        }

        throw new YInternalException("The list is shorter than its length.");
    }

    /// <summary>
    /// Yjs <c>typeListInsertGenerics</c>: inserts <paramref name="content"/> at <paramref name="index"/>, consecutive values packed into one Any
    /// item. Throws <see cref="ArgumentOutOfRangeException"/> past the end and <see cref="ArgumentException"/> for an entry that cannot be inserted.
    /// </summary>
    public static void Insert(Transaction tx, YType type, int index, ReadOnlySpan<YEntry> content)
    {
        var doc = type.AttachedDoc ?? throw new InvalidOperationException("The type is not integrated.");
        var branch = type.Branch;
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        if (index > branch.Length) throw new ArgumentOutOfRangeException(nameof(index), "Length exceeded!");
        Validate(content, undefinedAllowed: false);
        if (content.IsEmpty) return;

        var store = doc.Store;
        var markers = branch.MarkersFor(store);
        if (index == 0)
        {
            markers?.Update(0, content.Length);
            InsertAfter(tx, doc, branch, ItemRef.Null, content);
            return;
        }

        var startIndex = index;
        var n = StructStore.StartOf(branch);
        if (markers?.Find(branch, index) is { } marker)
        {
            n = new ItemRef(marker.Item);
            index -= marker.Index;
            if (index == 0)
            {
                n = Prev(n);   // the left undeleted item, so that the index can decrease
                index += !n.IsNull && n.IsCountable && !n.IsDeleted ? (int)n.Length : 0;
            }
        }

        for (; !n.IsNull; n = n.Right)
        {
            if (n.IsDeleted || !n.IsCountable) continue;
            if (index <= n.Length)
            {
                if (index < n.Length) store.GetItemCleanStart(store.ClientOf(n), n.Clock + index);
                break;
            }

            index -= (int)n.Length;
        }

        markers?.Update(startIndex, content.Length);
        InsertAfter(tx, doc, branch, n, content);
    }

    /// <summary>
    /// Yjs <c>typeListDelete</c>: deletes <paramref name="length"/> elements from <paramref name="index"/>. As in JS, a range that runs past the
    /// end deletes what exists and then throws "Length exceeded!" (A-5); the deleted part stays. A start past the end deletes nothing.
    /// </summary>
    public static void Delete(Transaction tx, YType type, int index, int length)
    {
        var doc = type.AttachedDoc ?? throw new InvalidOperationException("The type is not integrated.");
        var branch = type.Branch;
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length == 0) return;
        if (index > branch.Length) throw new ArgumentOutOfRangeException(nameof(length), "Length exceeded!");

        var store = doc.Store;
        var markers = branch.MarkersFor(store);
        var startIndex = index;
        var startLength = length;
        var n = StructStore.StartOf(branch);
        if (markers?.Find(branch, index) is { } marker)
        {
            n = new ItemRef(marker.Item);
            index -= marker.Index;
        }

        // the first item to delete
        for (; !n.IsNull && index > 0; n = n.Right)
        {
            if (n.IsDeleted || !n.IsCountable) continue;
            if (index < n.Length) store.GetItemCleanStart(store.ClientOf(n), n.Clock + index);
            index -= (int)n.Length;
        }

        while (length > 0 && !n.IsNull)
        {
            if (!n.IsDeleted)
            {
                if (length < n.Length) store.GetItemCleanStart(store.ClientOf(n), n.Clock + length);
                doc.DeleteItem(tx, n);
                length -= (int)n.Length;
            }

            n = n.Right;
        }

        // JS throws before updating the markers and leaves them stale; y-sharp updates them first (no wire effect)
        markers?.Update(startIndex, -startLength + length);
        if (length > 0) throw new ArgumentOutOfRangeException(nameof(length), "Length exceeded!");
    }

    /// <summary>
    /// Yjs <c>typeListPushGenerics</c>: inserts after the last item, deleted or not. The branch keeps its last item, so this needs no walk
    /// (ADR 0023); the markers need no update, as nothing after the end moves.
    /// </summary>
    public static void Push(Transaction tx, YType type, ReadOnlySpan<YEntry> content)
    {
        var doc = type.AttachedDoc ?? throw new InvalidOperationException("The type is not integrated.");
        Validate(content, undefinedAllowed: false);
        if (content.IsEmpty) return;
        InsertAfter(tx, doc, type.Branch, new ItemRef(type.Branch.End), content);
    }

    /// <summary>
    /// Yjs <c>typeListInsertGenericsAfter</c> as YXmlFragment.insertAfter calls it: inserts right after <paramref name="reference"/> (the start when
    /// null), without updating search markers, which the XML list types do not keep.
    /// </summary>
    public static void InsertAfterItem(Transaction tx, YType type, ItemRef reference, ReadOnlySpan<YEntry> content)
    {
        var doc = type.AttachedDoc ?? throw new InvalidOperationException("The type is not integrated.");
        Validate(content, undefinedAllowed: false);
        if (content.IsEmpty) return;
        InsertAfter(tx, doc, type.Branch, reference, content);
    }

    /// <summary>Yjs <c>typeListSlice</c> with JS <c>Array.prototype.slice</c> bounds: negative indexes count from the end, both are clamped.</summary>
    public static List<YEntry> Slice(YType type, int start, int end)
    {
        var length = type.Branch.Length;
        start = start < 0 ? Math.Max(0, length + start) : Math.Min(start, length);
        end = end < 0 ? Math.Max(0, length + end) : Math.Min(end, length);
        var result = new List<YEntry>(Math.Max(0, end - start));
        if (type.AttachedDoc is not { } doc || end <= start) return result;
        var left = end - start;
        for (var n = StructStore.StartOf(type.Branch); !n.IsNull && left > 0; n = n.Right)
        {
            if (n.IsDeleted || !n.IsCountable) continue;
            if (n.Length <= start)
            {
                start -= (int)n.Length;
                continue;
            }

            for (var i = start; i < n.Length && left > 0; i++, left--) result.Add(ContentAt(doc, n, i));
            start = 0;
        }

        return result;
    }

    /// <summary>
    /// Rejects what Yjs cannot insert (ADR 0005, Q10): undefined in a list, and a type that is already integrated (T-0.13; JS has no guard).
    /// </summary>
    internal static void Validate(ReadOnlySpan<YEntry> content, bool undefinedAllowed)
    {
        foreach (var c in content)
        {
            switch (c.Kind)
            {
                case YEntryKind.Any when !undefinedAllowed && c.Value.Kind == YValueKind.Undefined:
                    throw new ArgumentException("Unexpected content type in insert operation: undefined.", nameof(content));
                case YEntryKind.Type when c.Type!.IsIntegrated || c.Type.Branch.Item is not null:
                    throw new ArgumentException("This type was already integrated.", nameof(content));
                case YEntryKind.Type when c.Type is YUntyped:
                    throw new ArgumentException("An untyped root placeholder cannot be inserted.", nameof(content));
            }
        }

        for (var i = 0; i < content.Length; i++)
            for (var j = i + 1; j < content.Length; j++)
                if (content[i].Kind == YEntryKind.Type && ReferenceEquals(content[i].Type, content[j].Type))
                    throw new ArgumentException("The same type cannot be inserted twice.", nameof(content));
    }

    /// <summary>Yjs <c>typeListInsertGenericsAfter</c>: values are packed into one Any item; every other entry is an item of its own.</summary>
    private static void InsertAfter(Transaction tx, Doc doc, Branch parent, ItemRef reference, ReadOnlySpan<YEntry> content)
    {
        var store = doc.Store;
        var left = reference;
        var right = reference.IsNull ? StructStore.StartOf(parent) : reference.Right;
        var packed = 0;
        for (var i = 0; i <= content.Length; i++)
        {
            if (i < content.Length && content[i].Kind == YEntryKind.Any) continue;
            if (i > packed)
            {
                var first = store.AppendValue(content[packed].Value);
                for (var k = packed + 1; k < i; k++) store.AppendValue(content[k].Value);
                left = Integrate(tx, doc, parent, left, right, ContentKind.Any, first, i - packed);
            }

            packed = i + 1;
            if (i == content.Length) break;
            var (kind, payload) = Payload(content[i]);
            left = Integrate(tx, doc, parent, left, right, kind, store.AddPayload(payload), 1);
        }
    }

    private static ItemRef Integrate(Transaction tx, Doc doc, Branch parent, ItemRef left, ItemRef right, ContentKind kind, int content, long length)
    {
        var item = doc.Store.CreateLocalItem(doc.ClientId, parent, left, right, default, false, kind, content, length);
        doc.IntegrateLocal(tx, item);
        return item;
    }

    /// <summary>The content kind and payload of a non-Any entry (Yjs <c>ContentBinary</c>, <c>ContentType</c>, <c>ContentDoc</c>).</summary>
    internal static (ContentKind Kind, object Payload) Payload(in YEntry entry)
    {
        switch (entry.Kind)
        {
            case YEntryKind.Binary:
                entry.Value.TryGetBytes(out var bytes);
                return (ContentKind.Binary, bytes.ToArray());
            case YEntryKind.Type:
                return (ContentKind.Type, entry.Type!.Branch);
            case YEntryKind.Doc:
                return (ContentKind.Doc, SubdocContent.Of(entry.Doc!));   // JS logs, and goes on, when the document is already a subdocument (SUB-05)
            default:
                throw new YInternalException($"A {entry.Kind} entry has no payload.");
        }
    }

    /// <summary>Yjs <c>item.prev</c>: the nearest undeleted item to the left.</summary>
    private static ItemRef Prev(ItemRef n)
    {
        var l = n.Left;
        while (!l.IsNull && l.IsDeleted) l = l.Left;
        return l;
    }

    /// <summary>
    /// Yjs <c>content.getContent()</c>, appended to <paramref name="into"/>. A format or deleted content gives <c>[]</c>: a foreign update can
    /// put a format item in an array (its origin decides the parent), and the array's event delta then inserts nothing for it.
    /// </summary>
    internal static void AddContent(Doc doc, ItemRef n, List<YEntry> into)
    {
        if (n.Kind is ContentKind.Format or ContentKind.Deleted) return;
        for (var i = 0; i < n.Length; i++) into.Add(ContentAt(doc, n, i));
    }

    /// <summary>Element <paramref name="offset"/> of <c>content.getContent()</c>: a value, a byte string, a type, or one UTF-16 unit of a string.</summary>
    internal static YEntry ContentAt(Doc doc, ItemRef n, int offset)
    {
        var store = doc.Store;
        switch (n.Kind)
        {
            case ContentKind.Any:
            case ContentKind.Json:
                return store.GetValue(n, offset);
            case ContentKind.String:
                return store.GetString(n).Substring(offset, 1);
            case ContentKind.Binary:
                return YValue.Bytes((byte[])store.GetPayload(n));
            case ContentKind.Embed:
                return ((EmbedContent)store.GetPayload(n)).Value;
            case ContentKind.Type:
                return YType.Of((Branch)store.GetPayload(n), doc);
            case ContentKind.Doc:
                return ((SubdocContent)store.GetPayload(n)).Doc ?? throw new YInternalException("An integrated subdocument item has no document.");
            default:
                throw new YInternalException($"A {n.Kind} item has no content to read.");
        }
    }
}
