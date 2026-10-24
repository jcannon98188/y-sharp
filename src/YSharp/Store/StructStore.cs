using System;
using System.Buffers;
using System.Text;

namespace YSharp.Store;

/// <summary>
/// A document's struct store (Yjs <c>StructStore</c>, STR-52) and the content layer its items point into (ADR 0023, object per item). It owns
/// the client table and each client's clock-ordered struct list, the text buffer, the value column, the map keys and the payload table, and it
/// provides the layout operations the rest of the library builds on: lookup by id, split, link, delete marking, merge and the release of deleted
/// payloads. Conflict resolution, transactions, codecs and types live in their own modules and drive these operations through
/// <see cref="ItemRef"/> handles. Everything it holds is reachable only from the document (ADR 0018).
/// </summary>
internal sealed class StructStore
{
    internal readonly ClientTable Clients = new();
    internal readonly TextBuffer Text = new();
    internal readonly ValueColumn Values = new();
    internal readonly KeyTable Keys = new();

    /// <summary>Payloads of the one-element contents: binary, embed, format, type and subdocument. A released payload's slot is nulled.</summary>
    internal readonly ChunkedList<object?> Payloads = new();

    internal readonly MarkerClock MarkerClock = new();

    /// <summary>Right halves of the splits made in the open transaction (Yjs <c>transaction._mergeStructs</c>). The transaction clears it at commit.</summary>
    internal readonly ChunkedList<Item> MergeStructs = new();

    // ------------------------------------------------------------------ ids and state

    /// <summary>Yjs <c>getState</c>: the next clock expected from <paramref name="client"/>, 0 for an unknown client.</summary>
    public long GetState(ulong client)
    {
        var slot = Clients.TryGetSlot(client);
        return slot < 0 ? 0 : Clients[slot].NextClock;
    }

    /// <summary>The slot of <paramref name="client"/>, adding it if it is new.</summary>
    public int SlotOf(ulong client) => Clients.SlotOf(client);

    public ulong ClientOf(ItemRef item) => Clients[item.Item!.Client].Id;

    public ItemId IdOf(ItemRef item) => new(ClientOf(item), item.Item!.Clock);

    public ItemId? OriginOf(ItemRef item)
    {
        var i = item.Item!;
        return i.OriginClient == Item.NoClient ? null : new ItemId(Clients[i.OriginClient].Id, i.OriginClock);
    }

    public ItemId? RightOriginOf(ItemRef item)
    {
        var i = item.Item!;
        return i.RightOriginClient == Item.NoClient ? null : new ItemId(Clients[i.RightOriginClient].Id, i.RightOriginClock);
    }

    /// <summary>The map key of <paramref name="item"/> (Yjs <c>parentSub</c>), or null for a sequence item.</summary>
    public string? ParentSubOf(ItemRef item)
    {
        var key = item.Item!.ParentSub;
        return key < 0 ? null : Keys.Get(key).ToString();
    }

    /// <summary>Number of structs held for <paramref name="client"/>.</summary>
    public int StructCount(ulong client)
    {
        var slot = Clients.TryGetSlot(client);
        return slot < 0 ? 0 : Clients[slot].Structs.Count;
    }

    /// <summary>The struct at <paramref name="index"/> in <paramref name="client"/>'s clock-ordered list.</summary>
    public ItemRef StructAt(ulong client, int index) => new(Owner(client).Structs[index]);

    // ------------------------------------------------------------------ lookup (STR-46 to STR-51)

    /// <summary>Yjs <c>find</c>/<c>getItem</c> (STR-47): the struct that holds the clock. The caller checks the state first, as Yjs does.</summary>
    public ItemRef Find(ulong client, long clock)
    {
        var owner = Owner(client);
        return new(owner.Structs[owner.FindIndex(clock)]);
    }

    /// <summary>Yjs <c>findIndexCleanStart</c> (STR-48): the index of the struct starting at <paramref name="clock"/>, splitting an item if needed.</summary>
    public int FindIndexCleanStart(ulong client, long clock) => FindIndexCleanStart(Owner(client), clock);

    /// <summary>Yjs <c>getItemCleanStart</c> (STR-49).</summary>
    public ItemRef GetItemCleanStart(ulong client, long clock)
    {
        var owner = Owner(client);
        return new(owner.Structs[FindIndexCleanStart(owner, clock)]);
    }

    /// <summary>Yjs <c>getItemCleanEnd</c>: the struct ending at <paramref name="clock"/>, splitting an item if needed.</summary>
    public ItemRef GetItemCleanEnd(ulong client, long clock)
    {
        var owner = Owner(client);
        var index = owner.FindIndex(clock);
        var item = owner.Structs[index];
        if (clock != item.Clock + item.Length - 1 && !item.IsGc) owner.Structs.Insert(index + 1, Split(item, clock - item.Clock + 1));
        return new(item);
    }

    /// <summary>Yjs <c>addStruct</c> (STR-03): appends a struct to its client's list. Its clock must be the client's state.</summary>
    public void AddStruct(ItemRef item)
    {
        var i = item.Item!;
        var owner = Clients[i.Client];
        if (owner.Structs.Count > 0 && owner.NextClock != i.Clock)
            throw new YInternalException($"A struct at clock {i.Clock} does not follow the client's state {owner.NextClock}.");
        owner.Structs.Add(i);
    }

    private int FindIndexCleanStart(ClientStructs owner, long clock)
    {
        var index = owner.FindIndex(clock);
        var item = owner.Structs[index];
        if (item.Clock < clock && !item.IsGc)
        {
            owner.Structs.Insert(index + 1, Split(item, clock - item.Clock));
            return index + 1;
        }

        return index;
    }

    private ClientStructs Owner(ulong client)
    {
        var slot = Clients.TryGetSlot(client);
        if (slot < 0) throw new YInternalException($"The store holds no structs for client {client}.");
        return Clients[slot];
    }

    // ------------------------------------------------------------------ content

    /// <summary>Copies <paramref name="text"/> into the text buffer and returns its address, for a <see cref="ContentKind.String"/> item.</summary>
    public int AppendText(ReadOnlySpan<char> text) => Text.Append(text);

    /// <summary>Copies <paramref name="values"/> into the value column and returns the first index, for an Any or JSON item.</summary>
    public int AppendValues(ReadOnlySpan<YValue> values) => Values.AppendRange(values);

    public int AppendValue(in YValue value) => Values.Append(value);

    /// <summary>Stores the payload of a binary, embed, format, type or subdocument item and returns its index.</summary>
    public int AddPayload(object payload)
    {
        Payloads.Add(payload);
        return Payloads.Count - 1;
    }

    public string GetString(ItemRef item)
    {
        var i = Expect(item, ContentKind.String);
        return Text.GetString(i.Content, (int)i.Length);
    }

    public void AppendString(StringBuilder sb, ItemRef item)
    {
        var i = Expect(item, ContentKind.String);
        Text.AppendTo(sb, i.Content, (int)i.Length);
    }

    /// <summary>Value <paramref name="offset"/> of an Any or JSON item.</summary>
    public YValue GetValue(ItemRef item, int offset)
    {
        var i = item.Item!;
        if (i.Kind is not (ContentKind.Any or ContentKind.Json)) throw new InvalidOperationException($"A {i.Kind} item holds no values.");
        if ((uint)offset >= (ulong)i.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        return Values.Get(i.Content + offset);
    }

    /// <summary>The payload of a binary, embed, format, type or subdocument item.</summary>
    public object GetPayload(ItemRef item)
    {
        var i = item.Item!;
        if (i.Kind < ContentKind.Binary) throw new InvalidOperationException($"A {i.Kind} item has no payload object.");
        return Payloads[i.Content] ?? throw new YInternalException("Read of a released payload.");
    }

    private static Item Expect(ItemRef item, ContentKind kind)
    {
        var i = item.Item!;
        if (i.Kind != kind) throw new InvalidOperationException($"The item holds {i.Kind}, not {kind}.");
        return i;
    }

    // ------------------------------------------------------------------ creating and linking structs

    /// <summary>
    /// A new item that is not linked yet. <paramref name="content"/> is the index <see cref="ContentKind"/> describes (text address, first value,
    /// payload index), and is ignored for <see cref="ContentKind.Deleted"/>. A map item takes <paramref name="key"/>: the key slot of an item
    /// that already holds the key in <paramref name="parent"/> is reused, so a key is stored once per map.
    /// </summary>
    public ItemRef CreateItem(ulong client, long clock, ItemId? origin, ItemId? rightOrigin, Branch parent, ReadOnlySpan<char> key, bool keyed,
        ContentKind kind, int content, long length)
    {
        if (kind == ContentKind.Gc) throw new ArgumentException("Use CreateGc for a GC struct.", nameof(kind));
        Item item = keyed ? new KeyedItem(KeySlot(parent, key)) : new Item();
        item.Parent = parent;
        item.Client = SlotOf(client);
        item.Clock = clock;
        SetOrigin(item, origin);
        SetRightOrigin(item, rightOrigin);
        item.Kind = kind;
        if (kind != ContentKind.Deleted) item.Content = content;
        item.SetLength(length);
        item.Flags = Item.IsCountableKind(kind) ? ItemFlags.Countable : ItemFlags.None;
        return new(item);
    }

    /// <summary>A local item between <paramref name="left"/> and <paramref name="right"/>: its origins are their ids (Yjs <c>left.lastId</c>, <c>right.id</c>).</summary>
    public ItemRef CreateLocalItem(ulong client, Branch parent, ItemRef left, ItemRef right, ReadOnlySpan<char> key, bool keyed, ContentKind kind,
        int content, long length)
    {
        var item = CreateItem(client, GetState(client), null, null, parent, key, keyed, kind, content, length).Item!;
        item.Left = left.Item;
        item.Right = right.Item;
        if (left.Item is { } l)
        {
            item.OriginClient = l.Client;
            item.OriginClock = l.LastClock;
        }

        if (right.Item is { } r)
        {
            item.RightOriginClient = r.Client;
            item.RightOriginClock = r.Clock;
        }

        return new(item);
    }

    /// <summary>A GC struct (Yjs <c>GC</c>), added to its client's list.</summary>
    public ItemRef AddGc(ulong client, long clock, long length)
    {
        var gc = new Item
        {
            Client = SlotOf(client),
            Clock = clock,
            OriginClient = Item.NoClient,
            RightOriginClient = Item.NoClient,
            Kind = ContentKind.Gc,
            Flags = ItemFlags.Deleted,
        };
        gc.SetLength(length);
        AddStruct(new(gc));
        return new(gc);
    }

    /// <summary>
    /// The layout half of Yjs <c>Item.integrate</c>, after conflict resolution has chosen <paramref name="left"/>: links the item between
    /// <paramref name="left"/> and its right neighbour (or at the start of the sequence, or before the key's earliest item), makes it the key's
    /// current item when nothing is to its right, adds its length to the parent and appends it to its client's structs. Deleting a replaced map
    /// value, recording changes and integrating content are left to the caller.
    /// </summary>
    public void Link(ItemRef item, ItemRef left)
    {
        var i = item.Item!;
        var parent = i.Parent ?? throw new YInternalException("A struct without a parent cannot be linked.");
        var keyed = i.ParentSub >= 0;
        i.Left = left.Item;
        if (left.Item is { } l)
        {
            i.Right = l.Right;
            l.Right = i;
        }
        else if (keyed)
        {
            var r = parent.Map?.Get(Keys.Get(i.ParentSub));
            while (r?.Left is not null) r = r.Left;
            i.Right = r;
        }
        else
        {
            i.Right = parent.Start;
            parent.Start = i;
        }

        if (i.Right is not null) i.Right.Left = i;
        else if (keyed) parent.MapFor(this).Set(i);
        else parent.End = i;

        if (!keyed && i.Countable && !i.Deleted) parent.Length += (int)i.Length;
        AddStruct(item);
    }

    // ------------------------------------------------------------------ deleting and releasing

    /// <summary>
    /// The layout half of Yjs <c>Item.delete</c>: marks the item deleted, takes its length off the parent and adds its range to
    /// <paramref name="deleteSet"/>. Returns false when it was already deleted. Recording the change and deleting nested content are the caller's.
    /// </summary>
    public bool Delete(ItemRef item, DeleteRanges deleteSet)
    {
        var i = item.Item!;
        if (i.Deleted) return false;
        if (i.Countable && i.ParentSub < 0 && i.Parent is { } parent) parent.Length -= (int)i.Length;
        i.Flags |= ItemFlags.Deleted;
        deleteSet.Add(Clients[i.Client].Id, i.Clock, i.Length);
        return true;
    }

    /// <summary>
    /// Yjs <c>Item.gc</c> with a live parent: the content becomes <see cref="ContentKind.Deleted"/>, the item stops being countable and its
    /// payload is released. Text releases a whole chunk once none of its characters is live (ADR 0023).
    /// </summary>
    public void GcContent(ItemRef item)
    {
        var i = item.Item!;
        if (!i.Deleted) throw new YInternalException("Only a deleted item can be garbage-collected.");
        if (i.HasNoContent) return;
        var length = i.Length;
        Release(i);
        i.Kind = ContentKind.Deleted;
        i.Content = 0;
        i.SetLength(length);
        i.Flags &= ~ItemFlags.Countable;
    }

    /// <summary>
    /// Yjs <c>Item.gc</c> with a garbage-collected parent (<c>replaceStruct</c>, STR-50): the struct becomes a GC struct with the same id and
    /// length, in place, so its slot in the client's list is unchanged.
    /// </summary>
    public void ReplaceWithGc(ItemRef item)
    {
        var i = item.Item!;
        if (!i.Deleted) throw new YInternalException("Only a deleted item can be garbage-collected.");
        var length = i.Length;
        if (!i.HasNoContent) Release(i);
        i.Kind = ContentKind.Gc;
        i.Content = 0;
        i.SetLength(length);
        i.Flags = ItemFlags.Deleted;
        i.Parent = null;
    }

    private void Release(Item i)
    {
        switch (i.Kind)
        {
            case ContentKind.String:
                Text.Free(i.Content, (int)i.Length);
                break;
            case ContentKind.Any:
            case ContentKind.Json:
                Values.Free(i.Content, (int)i.Length);
                break;
            case >= ContentKind.Binary:
                Payloads[i.Content] = null;
                break;
        }
    }

    // ------------------------------------------------------------------ split and merge

    /// <summary>
    /// Yjs <c>splitItem</c>: the part from <paramref name="diff"/> on becomes a new item, linked right after <paramref name="item"/> and recorded in
    /// <see cref="MergeStructs"/>. The caller places it in the client's list (the clean-start and clean-end lookups do).
    /// </summary>
    private Item Split(Item left, long diff)
    {
        if (diff <= 0 || diff >= left.Length) throw new YInternalException($"A split at {diff} is outside an item of length {left.Length}.");
        if (left.Kind >= ContentKind.Binary) throw new YInternalException($"A {left.Kind} item has length 1 and cannot split.");

        var right = left.ParentSub >= 0 ? new KeyedItem(left.ParentSub) : new Item();
        right.Left = left;
        right.Right = left.Right;
        right.Parent = left.Parent;
        right.Client = left.Client;
        right.Clock = left.Clock + diff;
        right.OriginClient = left.Client;
        right.OriginClock = left.Clock + diff - 1;
        right.RightOriginClient = left.RightOriginClient;
        right.RightOriginClock = left.RightOriginClock;
        right.Kind = left.Kind;
        right.Flags = left.Flags & ~ItemFlags.Marker;

        var total = left.Length;
        switch (left.Kind)
        {
            case ContentKind.String:
                right.Content = left.Content + (int)diff;
                // ContentString.splice: a split between the halves of a surrogate pair replaces both halves with U+FFFD (yjs#248).
                if (char.IsHighSurrogate(Text.CharAt(left.Content + (int)diff - 1)))
                {
                    Text.SetChar(left.Content + (int)diff - 1, '�');
                    Text.SetChar(right.Content, '�');
                }

                break;
            case ContentKind.Any:
            case ContentKind.Json:
                right.Content = left.Content + (int)diff;
                break;
        }

        right.SetLength(total - diff);
        left.SetLength(diff);

        left.Right = right;
        if (right.Right is not null) right.Right.Left = right;
        else if (right.ParentSub < 0 && right.Parent is { } parent && ReferenceEquals(parent.End, left)) parent.End = right;
        MergeStructs.Add(right);
        if (right.ParentSub >= 0 && right.Right is null) right.Parent!.MapFor(this).Set(right);
        return right;
    }

    /// <summary>
    /// Yjs <c>tryToMergeWithLefts</c>: merges the struct at <paramref name="pos"/> in <paramref name="client"/>'s list into its left neighbours
    /// for as long as <c>mergeWith</c> allows, removes the merged structs, and returns how many merged.
    /// </summary>
    public int TryToMergeWithLefts(ulong client, int pos) => TryToMergeWithLefts(Owner(client), pos);

    internal int TryToMergeWithLefts(ClientStructs owner, int pos)
    {
        var structs = owner.Structs;
        var i = pos;
        for (; i > 0; i--)
        {
            var right = structs[i];
            var left = structs[i - 1];
            if (left.Deleted != right.Deleted || left.IsGc != right.IsGc || !MergeWith(left, right)) break;
            if (right.ParentSub >= 0 && right.Parent?.Map is { } map && ReferenceEquals(map.Get(Keys.Get(right.ParentSub)), right)) map.Set(left);
        }

        var merged = pos - i;
        if (merged > 0) structs.RemoveRange(pos + 1 - merged, merged);
        return merged;
    }

    /// <summary>Yjs <c>Item.mergeWith</c> and <c>GC.mergeWith</c>, with the content merges of every mergeable content kind.</summary>
    internal bool MergeWith(Item left, Item right)
    {
        if (left.IsGc || right.IsGc)
        {
            if (!left.IsGc || !right.IsGc) return false;
            left.SetLength(left.Length + right.Length);
            return true;
        }

        if (right.OriginClient != left.Client || right.OriginClock != left.LastClock) return false;
        if (!ReferenceEquals(left.Right, right)) return false;
        if (left.RightOriginClient != right.RightOriginClient ||
            (left.RightOriginClient != Item.NoClient && left.RightOriginClock != right.RightOriginClock)) return false;
        if (left.Client != right.Client || left.Clock + left.Length != right.Clock) return false;
        if (left.Deleted != right.Deleted || left.Kind != right.Kind) return false;
        if (!MergeContent(left, right)) return false;

        left.Parent?.Markers?.Repoint(right, left);
        if (right.Keep) left.Keep = true;
        left.Right = right.Right;
        if (left.Right is not null) left.Right.Left = left;
        else if (left.Parent is { } parent && ReferenceEquals(parent.End, right)) parent.End = left;
        return true;
    }

    /// <summary>The content merges (ContentString, ContentAny, ContentJSON, ContentDeleted); sets the left item's new length.</summary>
    private bool MergeContent(Item left, Item right)
    {
        var total = left.Length + right.Length;
        switch (left.Kind)
        {
            case ContentKind.Deleted:
                left.SetLength(total);
                return true;
            case ContentKind.String:
                if (left.Content + left.Length != right.Content)
                {
                    // Not adjacent in the buffer: copy both halves to the end, so the merged item is one slice again.
                    left.Content = CopyText(left.Content, (int)left.Length, right.Content, (int)right.Length);
                }

                left.SetLength(total);
                return true;
            case ContentKind.Any:
            case ContentKind.Json:
                if (left.Content + left.Length != right.Content)
                {
                    var start = Values.AppendCopy(left.Content, (int)left.Length);
                    Values.AppendCopy(right.Content, (int)right.Length);
                    Values.Free(left.Content, (int)left.Length);
                    Values.Free(right.Content, (int)right.Length);
                    left.Content = start;
                }

                left.SetLength(total);
                return true;
            default:
                return false;
        }
    }

    private int CopyText(int leftAddress, int leftLength, int rightAddress, int rightLength)
    {
        var length = leftLength + rightLength;
        var rented = ArrayPool<char>.Shared.Rent(length);
        try
        {
            Text.CopyTo(leftAddress, leftLength, rented);
            Text.CopyTo(rightAddress, rightLength, rented.AsSpan(leftLength));
            var address = Text.Append(rented.AsSpan(0, length));
            Text.Free(leftAddress, leftLength);
            Text.Free(rightAddress, rightLength);
            return address;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    // ------------------------------------------------------------------ helpers

    private int KeySlot(Branch parent, ReadOnlySpan<char> key)
    {
        var existing = parent.Map?.Get(key);
        return existing is not null ? existing.ParentSub : Keys.Add(key);
    }

    private void SetOrigin(Item item, ItemId? origin)
    {
        if (origin is { } o)
        {
            item.OriginClient = SlotOf(o.Client);
            item.OriginClock = o.Clock;
        }
        else
        {
            item.OriginClient = Item.NoClient;
        }
    }

    private void SetRightOrigin(Item item, ItemId? rightOrigin)
    {
        if (rightOrigin is { } r)
        {
            item.RightOriginClient = SlotOf(r.Client);
            item.RightOriginClock = r.Clock;
        }
        else
        {
            item.RightOriginClient = Item.NoClient;
        }
    }

    /// <summary>Largest single block any structure of the store has allocated, in bytes (ADR 0018 chunk ceiling).</summary>
    internal long MaxChunkBytes
    {
        get
        {
            long max = 0;
            foreach (var v in new[] { Clients.MaxChunkBytes, Text.MaxChunkBytes, Values.MaxChunkBytes, Keys.MaxChunkBytes, Payloads.MaxBlockBytes, MergeStructs.MaxBlockBytes })
                max = Math.Max(max, v);
            return max;
        }
    }
}
