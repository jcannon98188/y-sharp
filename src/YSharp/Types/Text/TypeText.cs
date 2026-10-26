#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using YSharp.Lib0;
using YSharp.Store;

namespace YSharp;

// The rich-text core under YText and YXmlText (Yjs YText.js: ItemTextListPosition, findPosition, insertText, formatText, deleteText, the
// formatting cleanups and toDelta). A position walks the items left to right and tracks the formats in effect (a JS Map, so a key keeps its
// position until it is removed). The caller holds the document lock.
internal static class TypeText
{
    /// <summary>Yjs <c>ItemTextListPosition</c>: the items either side of a position, its index and the formats in effect there.</summary>
    internal sealed class Position
    {
        public ItemRef Left;
        public ItemRef Right;
        public int Index;
        public readonly OrderedDictionary<string, YValue> Current;

        public Position(ItemRef left, ItemRef right, int index, OrderedDictionary<string, YValue> current)
        {
            Left = left;
            Right = right;
            Index = index;
            Current = current;
        }

        /// <summary>Yjs <c>forward</c>: steps over <see cref="Right"/>, which must exist.</summary>
        public void Forward(StructStore store)
        {
            if (Right.IsNull) throw new YInternalException("A text position stepped past the end.");
            if (!Right.IsDeleted)
            {
                if (Right.Kind == ContentKind.Format) UpdateCurrent(Current, Format(store, Right));
                else Index += (int)Right.Length;
            }

            Left = Right;
            Right = Right.Right;
        }
    }

    /// <summary>
    /// A JS attributes object as <c>insertText</c> and <c>formatText</c> read it: members in insertion order, iterated in <c>for...in</c> order
    /// (array-index keys first, Q08), and a member set to undefined reads as absent.
    /// </summary>
    internal sealed class Attributes
    {
        private readonly List<KeyValuePair<string, YValue>> _members = new();

        public static Attributes From(in YValue value)
        {
            var attributes = new Attributes();
            if (value.TryGetObject(out var view))
                for (var i = 0; i < view.Count; i++) attributes._members.Add(view[i]);
            return attributes;
        }

        public static Attributes From(OrderedDictionary<string, YValue> current)
        {
            var attributes = new Attributes();
            foreach (var m in current) attributes._members.Add(m);
            return attributes;
        }

        /// <summary><c>attributes[key]</c>: undefined when absent.</summary>
        public YValue Get(string key)
        {
            foreach (var m in _members)
                if (string.Equals(m.Key, key, StringComparison.Ordinal)) return m.Value;
            return default;
        }

        public void Set(string key, in YValue value)
        {
            for (var i = 0; i < _members.Count; i++)
            {
                if (!string.Equals(_members[i].Key, key, StringComparison.Ordinal)) continue;
                _members[i] = new(key, value);
                return;
            }

            _members.Add(new(key, value));
        }

        /// <summary>The members in <c>for...in</c> order.</summary>
        public KeyValuePair<string, YValue>[] InJsOrder()
        {
            var keys = new string[_members.Count];
            for (var i = 0; i < keys.Length; i++) keys[i] = _members[i].Key;
            var order = new int[keys.Length];
            JsKeyOrder.Order(keys, order);
            var result = new KeyValuePair<string, YValue>[keys.Length];
            for (var i = 0; i < order.Length; i++) result[i] = _members[order[i]];
            return result;
        }
    }

    // ------------------------------------------------------------------ positions

    /// <summary>Yjs <c>findNextPosition</c>: moves <paramref name="count"/> units right, splitting the item the position lands in.</summary>
    public static Position FindNextPosition(StructStore store, Position pos, int count)
    {
        while (!pos.Right.IsNull && count > 0)
        {
            var right = pos.Right;
            if (!right.IsDeleted)
            {
                if (right.Kind == ContentKind.Format)
                {
                    UpdateCurrent(pos.Current, Format(store, right));
                }
                else
                {
                    if (count < right.Length) store.GetItemCleanStart(store.ClientOf(right), right.Clock + count);
                    pos.Index += (int)right.Length;
                    count -= (int)right.Length;
                }
            }

            pos.Left = right;
            pos.Right = right.Right;
        }

        return pos;
    }

    /// <summary>Yjs <c>findPosition</c>: the position at <paramref name="index"/> (clamped to the end), from a search marker when allowed.</summary>
    public static Position FindPosition(Doc doc, Branch parent, int index, bool useSearchMarker)
    {
        var current = new OrderedDictionary<string, YValue>(StringComparer.Ordinal);
        if (useSearchMarker && parent.MarkersFor(doc.Store)?.Find(parent, index) is { } marker)
        {
            var p = new ItemRef(marker.Item);
            return FindNextPosition(doc.Store, new Position(p.Left, p, marker.Index, current), index - marker.Index);
        }

        return FindNextPosition(doc.Store, new Position(ItemRef.Null, StructStore.StartOf(parent), 0, current), index);
    }

    // ------------------------------------------------------------------ insert and format

    /// <summary>Yjs <c>insertNegatedAttributes</c>: ends the formats the insertion started, unless the text to the right already does.</summary>
    private static void InsertNegatedAttributes(Transaction tx, Doc doc, Branch parent, Position pos, OrderedDictionary<string, YValue> negated)
    {
        var store = doc.Store;
        while (!pos.Right.IsNull &&
               (pos.Right.IsDeleted ||
                (pos.Right.Kind == ContentKind.Format && EqualAttrs(GetOrUndefined(negated, Format(store, pos.Right).Key), Format(store, pos.Right).Value))))
        {
            if (!pos.Right.IsDeleted) negated.Remove(Format(store, pos.Right).Key);
            pos.Forward(store);
        }

        foreach (var (key, value) in negated)
        {
            pos.Right = IntegrateFormat(tx, doc, parent, pos.Left, pos.Right, key, value);
            pos.Forward(store);
        }
    }

    /// <summary>Yjs <c>minimizeAttributeChanges</c>: skips deleted items and formats that already have the requested value.</summary>
    private static void MinimizeAttributeChanges(StructStore store, Position pos, Attributes attributes)
    {
        while (!pos.Right.IsNull)
        {
            if (!pos.Right.IsDeleted)
            {
                if (pos.Right.Kind != ContentKind.Format) break;
                var format = Format(store, pos.Right);
                if (!EqualAttrs(NullIfUndefined(attributes.Get(format.Key)), format.Value)) break;
            }

            pos.Forward(store);
        }
    }

    /// <summary>Yjs <c>insertAttributes</c>: a format item for each attribute that differs from the one in effect; returns what each was before.</summary>
    private static OrderedDictionary<string, YValue> InsertAttributes(Transaction tx, Doc doc, Branch parent, Position pos, Attributes attributes)
    {
        var negated = new OrderedDictionary<string, YValue>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes.InJsOrder())
        {
            var current = pos.Current.TryGetValue(key, out var c) ? NullIfUndefined(c) : YValue.Null;
            if (EqualAttrs(current, value)) continue;
            negated[key] = current;
            pos.Right = IntegrateFormat(tx, doc, parent, pos.Left, pos.Right, key, value);
            pos.Forward(doc.Store);
        }

        return negated;
    }

    /// <summary>
    /// Yjs <c>insertText</c>: inserts a string, an embedded value or a nested type at the position with exactly <paramref name="attributes"/>
    /// (a format in effect that it does not name is ended), then restores the formats in effect after it.
    /// </summary>
    public static void InsertText(Transaction tx, Doc doc, Branch parent, Position pos, in YEntry content, Attributes attributes)
    {
        var store = doc.Store;
        foreach (var (key, _) in pos.Current)
            if (attributes.Get(key).Kind == YValueKind.Undefined) attributes.Set(key, YValue.Null);

        MinimizeAttributeChanges(store, pos, attributes);
        var negated = InsertAttributes(tx, doc, parent, pos, attributes);

        ContentKind kind;
        int index;
        int length;
        if (content.Kind == YEntryKind.Any && content.Value.TryGetString(out var text))
        {
            kind = ContentKind.String;
            index = store.AppendText(text);
            length = text.Length;
        }
        else if (content.Kind == YEntryKind.Type)
        {
            kind = ContentKind.Type;
            index = store.AddPayload(content.Type!.Branch);
            length = 1;
        }
        else
        {
            kind = ContentKind.Embed;
            index = store.AddPayload(new EmbedContent(content.Value));
            length = 1;
        }

        parent.MarkersFor(store)?.Update(pos.Index, length);
        var item = store.CreateLocalItem(doc.ClientId, parent, pos.Left, pos.Right, default, false, kind, index, length);
        doc.IntegrateLocal(tx, item);
        pos.Right = item;
        pos.Forward(store);
        InsertNegatedAttributes(tx, doc, parent, pos, negated);
    }

    /// <summary>
    /// Yjs <c>formatText</c>: applies <paramref name="attributes"/> to <paramref name="length"/> units from the position, removing the formats
    /// it replaces. Past the end it appends that many newlines (Quill's trailing newline, T-9).
    /// </summary>
    public static void FormatText(Transaction tx, Doc doc, Branch parent, Position pos, int length, Attributes attributes)
    {
        var store = doc.Store;
        MinimizeAttributeChanges(store, pos, attributes);
        var negated = InsertAttributes(tx, doc, parent, pos, attributes);
        while (!pos.Right.IsNull &&
               (length > 0 || (negated.Count > 0 && (pos.Right.IsDeleted || pos.Right.Kind == ContentKind.Format))))
        {
            var right = pos.Right;
            if (!right.IsDeleted)
            {
                if (right.Kind == ContentKind.Format)
                {
                    var format = Format(store, right);
                    var attr = attributes.Get(format.Key);
                    if (attr.Kind != YValueKind.Undefined)
                    {
                        if (EqualAttrs(attr, format.Value))
                        {
                            negated.Remove(format.Key);
                        }
                        else
                        {
                            if (length == 0) break;   // no need to extend the negated attributes further
                            negated[format.Key] = format.Value;
                        }

                        doc.DeleteItem(tx, right);
                    }
                    else
                    {
                        pos.Current[format.Key] = format.Value;
                    }
                }
                else
                {
                    if (length < right.Length) store.GetItemCleanStart(store.ClientOf(right), right.Clock + length);
                    length -= (int)right.Length;
                }
            }

            pos.Forward(store);
        }

        if (length > 0)
        {
            var newlines = new string('\n', length);
            var item = store.CreateLocalItem(doc.ClientId, parent, pos.Left, pos.Right, default, false, ContentKind.String, store.AppendText(newlines),
                length);
            doc.IntegrateLocal(tx, item);
            pos.Right = item;
            pos.Forward(store);
        }

        InsertNegatedAttributes(tx, doc, parent, pos, negated);
    }

    // ------------------------------------------------------------------ delete and the formatting cleanups

    /// <summary>
    /// Yjs <c>cleanupFormattingGap</c>: after content between <paramref name="start"/> and the next content item was deleted, deletes the format
    /// items in the gap that are overwritten or redundant. Returns how many it deleted.
    /// </summary>
    private static int CleanupFormattingGap(Transaction tx, Doc doc, ItemRef start, ItemRef curr, OrderedDictionary<string, YValue> startAttributes,
        OrderedDictionary<string, YValue> currAttributes)
    {
        var store = doc.Store;
        var end = start;
        var endFormats = new Dictionary<string, ItemRef>(StringComparer.Ordinal);
        while (!end.IsNull && (!end.IsCountable || end.IsDeleted))
        {
            if (!end.IsDeleted && end.Kind == ContentKind.Format) endFormats[Format(store, end).Key] = end;
            end = end.Right;
        }

        var cleanups = 0;
        var reachedCurr = false;
        while (start != end)
        {
            if (curr == start) reachedCurr = true;
            if (!start.IsDeleted && start.Kind == ContentKind.Format)
            {
                var format = Format(store, start);
                var startValue = startAttributes.TryGetValue(format.Key, out var s) ? NullIfUndefined(s) : YValue.Null;
                if (!endFormats.TryGetValue(format.Key, out var endFormat) || endFormat != start || StrictEquals(startValue, format.Value))
                {
                    // either this format is overwritten or it is not needed because the attribute was already in effect
                    doc.DeleteItem(tx, start);
                    cleanups++;
                    var currValue = currAttributes.TryGetValue(format.Key, out var c) ? NullIfUndefined(c) : YValue.Null;
                    if (!reachedCurr && StrictEquals(currValue, format.Value) && !StrictEquals(startValue, format.Value))
                    {
                        if (startValue.Kind == YValueKind.Null) currAttributes.Remove(format.Key);
                        else currAttributes[format.Key] = startValue;
                    }
                }

                if (!reachedCurr && !start.IsDeleted) UpdateCurrent(currAttributes, format);
            }

            start = start.Right;
        }

        return cleanups;
    }

    /// <summary>Yjs <c>cleanupContextlessFormattingGap</c>: deletes repeated format keys in the format run around <paramref name="item"/>.</summary>
    internal static void CleanupContextlessFormattingGap(Transaction tx, Doc doc, ItemRef item)
    {
        var store = doc.Store;
        while (!item.IsNull && !item.Right.IsNull && (item.Right.IsDeleted || !item.Right.IsCountable)) item = item.Right;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (!item.IsNull && (item.IsDeleted || !item.IsCountable))
        {
            if (!item.IsDeleted && item.Kind == ContentKind.Format && !keys.Add(Format(store, item).Key)) doc.DeleteItem(tx, item);
            item = item.Left;
        }
    }

    /// <summary>Yjs <c>cleanupYTextFormatting</c>'s walk: removes every redundant format item of the text. Returns how many it removed.</summary>
    internal static int CleanupFormatting(Transaction tx, Doc doc, Branch parent)
    {
        var store = doc.Store;
        var result = 0;
        var start = StructStore.StartOf(parent);
        var end = start;
        var startAttributes = new OrderedDictionary<string, YValue>(StringComparer.Ordinal);
        var current = new OrderedDictionary<string, YValue>(StringComparer.Ordinal);
        while (!end.IsNull)
        {
            if (!end.IsDeleted)
            {
                if (end.Kind == ContentKind.Format)
                {
                    UpdateCurrent(current, Format(store, end));
                }
                else
                {
                    result += CleanupFormattingGap(tx, doc, start, end, startAttributes, current);
                    startAttributes = new OrderedDictionary<string, YValue>(current, StringComparer.Ordinal);
                    start = end;
                }
            }

            end = end.Right;
        }

        return result;
    }

    /// <summary>Yjs <c>deleteText</c>: deletes <paramref name="length"/> units of content from the position, then cleans up the formats around them.</summary>
    public static void DeleteText(Transaction tx, Doc doc, Position pos, int length)
    {
        var store = doc.Store;
        var startLength = length;
        var startAttributes = new OrderedDictionary<string, YValue>(pos.Current, StringComparer.Ordinal);
        var start = pos.Right;
        while (length > 0 && !pos.Right.IsNull)
        {
            var right = pos.Right;
            if (!right.IsDeleted && right.Kind is ContentKind.Type or ContentKind.Embed or ContentKind.String)
            {
                if (length < right.Length) store.GetItemCleanStart(store.ClientOf(right), right.Clock + length);
                length -= (int)right.Length;
                doc.DeleteItem(tx, right);
            }

            pos.Forward(store);
        }

        if (!start.IsNull) CleanupFormattingGap(tx, doc, start, pos.Right, startAttributes, pos.Current);
        var at = !pos.Left.IsNull ? pos.Left : pos.Right;
        if (!at.IsNull) at.Parent!.Markers?.Update(pos.Index, -startLength + length);
    }

    // ------------------------------------------------------------------ reads

    /// <summary>Yjs <c>YText.toString</c>: the undeleted strings, embeds skipped.</summary>
    public static string ToString(StructStore store, Branch branch)
    {
        var sb = new StringBuilder();
        for (var n = StructStore.StartOf(branch); !n.IsNull; n = n.Right)
            if (!n.IsDeleted && n.IsCountable && n.Kind == ContentKind.String) store.AppendString(sb, n);
        return sb.ToString();
    }

    /// <summary>Yjs <c>toDelta</c> without snapshots: inserts with the formats in effect; adjacent strings with the same formats are one op.</summary>
    public static TextDeltaOp[] ToDelta(Doc doc, Branch branch)
    {
        var store = doc.Store;
        var ops = new List<TextDeltaOp>();
        var current = new OrderedDictionary<string, YValue>(StringComparer.Ordinal);
        var sb = new StringBuilder();

        void PackStr()
        {
            if (sb.Length == 0) return;
            ops.Add(new TextDeltaOp(DeltaKind.Insert, sb.Length, sb.ToString(), default, AttributesOf(current)));
            sb.Clear();
        }

        for (var n = StructStore.StartOf(branch); !n.IsNull; n = n.Right)
        {
            if (n.IsDeleted) continue;
            switch (n.Kind)
            {
                case ContentKind.String:
                    store.AppendString(sb, n);
                    break;
                case ContentKind.Type:
                case ContentKind.Embed:
                    PackStr();
                    ops.Add(new TextDeltaOp(DeltaKind.Insert, 1, null, TypeList.ContentAt(doc, n, 0), AttributesOf(current)));
                    break;
                case ContentKind.Format:
                    PackStr();
                    UpdateCurrent(current, Format(store, n));
                    break;
            }
        }

        PackStr();
        return ops.ToArray();
    }

    /// <summary>
    /// Yjs <c>toDelta</c> with snapshots: walks every item visible in <paramref name="snapshot"/> (the live text when null) or in
    /// <paramref name="previous"/>. A string visible only in <paramref name="previous"/> is marked removed and one visible only in
    /// <paramref name="snapshot"/> added, through the <c>ychange</c> attribute; formats count only when visible in <paramref name="snapshot"/>.
    /// Collected content (a deleted item whose content is gone) reads as nothing (SNAP-11). The caller has split the structs.
    /// </summary>
    public static TextDeltaOp[] ToDelta(Doc doc, Branch branch, YSnapshot? snapshot, YSnapshot? previous, Func<string, ItemId, YValue>? computeYChange)
    {
        var store = doc.Store;
        var ops = new List<TextDeltaOp>();
        var current = new OrderedDictionary<string, YValue>(StringComparer.Ordinal);
        var sb = new StringBuilder();

        void PackStr()
        {
            if (sb.Length == 0) return;
            ops.Add(new TextDeltaOp(DeltaKind.Insert, sb.Length, sb.ToString(), default, AttributesOf(current)));
            sb.Clear();
        }

        bool Visible(ItemRef n, YSnapshot? s) => s is null ? !n.IsDeleted : s.IsVisible(store.ClientOf(n), n.Clock);

        void Mark(string type, ItemRef n)
        {
            var client = store.ClientOf(n);
            // JS compares cur.user and cur.type, so the default {type} (which has no user) starts a new op at every item
            if (current.TryGetValue("ychange", out var cur) && cur.TryGetObject(out var o) && o.TryGetValue("user", out var user)
                && StrictEquals(user, YValue.Number(client)) && o.TryGetValue("type", out var t) && StrictEquals(t, type)) return;
            PackStr();
            current["ychange"] = computeYChange is not null
                ? computeYChange(type, new ItemId(client, n.Clock))
                : YValue.Object([new KeyValuePair<string, YValue>("type", type)]);
        }

        for (var n = StructStore.StartOf(branch); !n.IsNull; n = n.Right)
        {
            if (!Visible(n, snapshot) && !(previous is not null && Visible(n, previous))) continue;
            switch (n.Kind)
            {
                case ContentKind.String:
                    if (snapshot is not null && !Visible(n, snapshot)) Mark("removed", n);
                    else if (previous is not null && !Visible(n, previous)) Mark("added", n);
                    else if (current.ContainsKey("ychange"))
                    {
                        PackStr();
                        current.Remove("ychange");
                    }

                    store.AppendString(sb, n);
                    break;
                case ContentKind.Type:
                case ContentKind.Embed:
                    PackStr();
                    ops.Add(new TextDeltaOp(DeltaKind.Insert, 1, null, TypeList.ContentAt(doc, n, 0), AttributesOf(current)));
                    break;
                case ContentKind.Format:
                    if (!Visible(n, snapshot)) break;
                    PackStr();
                    UpdateCurrent(current, Format(store, n));
                    break;
            }
        }

        PackStr();
        return ops.ToArray();
    }

    /// <summary>The formats in effect as a delta's <c>attributes</c> object; undefined when there are none.</summary>
    internal static YValue AttributesOf(OrderedDictionary<string, YValue> current)
    {
        if (current.Count == 0) return default;
        var members = new KeyValuePair<string, YValue>[current.Count];
        var i = 0;
        foreach (var m in current) members[i++] = m;
        return YValue.Object(members);
    }

    // ------------------------------------------------------------------ helpers

    private static ItemRef IntegrateFormat(Transaction tx, Doc doc, Branch parent, ItemRef left, ItemRef right, string key, in YValue value)
    {
        var store = doc.Store;
        var item = store.CreateLocalItem(doc.ClientId, parent, left, right, default, false, ContentKind.Format,
            store.AddPayload(new FormatContent(key, value)), 1);
        doc.IntegrateLocal(tx, item);
        return item;
    }

    internal static FormatContent Format(StructStore store, ItemRef item) => (FormatContent)store.GetPayload(item);

    /// <summary>Yjs <c>updateCurrentAttributes</c>: null removes the format.</summary>
    internal static void UpdateCurrent(OrderedDictionary<string, YValue> current, FormatContent format)
    {
        if (format.Value.Kind == YValueKind.Null) current.Remove(format.Key);
        else current[format.Key] = format.Value;
    }

    private static YValue GetOrUndefined(OrderedDictionary<string, YValue> map, string key) => map.TryGetValue(key, out var v) ? v : default;

    /// <summary>JS <c>value ?? null</c>.</summary>
    private static YValue NullIfUndefined(in YValue value) => value.Kind == YValueKind.Undefined ? YValue.Null : value;

    /// <summary>
    /// Yjs <c>equalAttrs</c>: <c>===</c>, or two non-null objects with the same own keys whose values are <c>===</c> (lib0 <c>equalFlat</c>,
    /// shallow). Arrays and byte arrays are objects too.
    /// </summary>
    internal static bool EqualAttrs(in YValue a, in YValue b)
    {
        if (StrictEquals(a, b)) return true;
        if (!IsObject(a) || !IsObject(b)) return false;
        var keysA = OwnKeys(a);
        var keysB = OwnKeys(b);
        if (keysA.Count != keysB.Count) return false;
        foreach (var (key, value) in keysA)
            if (!keysB.TryGetValue(key, out var other) || !StrictEquals(value, other)) return false;
        return true;
    }

    /// <summary>JS <c>===</c>: primitives by value (NaN differs from itself, +0 equals -0), objects, arrays and byte arrays by reference.</summary>
    internal static bool StrictEquals(in YValue a, in YValue b)
    {
        if (a.Kind != b.Kind) return false;
        switch (a.Kind)
        {
            case YValueKind.Undefined:
            case YValueKind.Null:
                return true;
            case YValueKind.Number:
                return a.AsNumber() == b.AsNumber();
            case YValueKind.Array:
            case YValueKind.Object:
            case YValueKind.Bytes:
                return ReferenceEquals(a.Payload, b.Payload);
            default:
                return a.Equals(b);
        }
    }

    private static bool IsObject(in YValue v) => v.Kind is YValueKind.Object or YValueKind.Array or YValueKind.Bytes;

    private static Dictionary<string, YValue> OwnKeys(in YValue v)
    {
        var keys = new Dictionary<string, YValue>(StringComparer.Ordinal);
        if (v.TryGetBytes(out var bytes))
        {
            for (var i = 0; i < bytes.Length; i++) keys[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = (int)bytes[i];
            return keys;
        }

        for (var i = 0; i < v.ChildCount; i++)
        {
            var child = v.ChildAt(i, out var key);
            keys[key ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = child;
        }

        return keys;
    }
}
