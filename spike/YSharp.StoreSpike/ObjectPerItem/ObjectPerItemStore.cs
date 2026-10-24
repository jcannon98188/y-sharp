using System;
using System.Collections.Generic;
using System.Text;
using YSharp.StoreSpike.Content;
using YSharp.StoreSpike.Harness;

namespace YSharp.StoreSpike.ObjectPerItem;

/// <summary>
/// Spike candidate A (ADR 0018 spike amendment): one heap object per item, linked by references, over the shared content layer. It runs local
/// operations through <see cref="IStoreDriver"/> on one document with a text "t", an array "a" and a map "m".
/// <para>
/// What it builds: the client table and per-client struct lists with split, neighbour and origin links and the parent sub-key, local insert
/// and delete, Yjs search markers, a chunked delete set, a chunked map key table, the Yjs commit cleanup (garbage-collect deleted content,
/// merge the delete set, merge new structs with <c>Item.mergeWith</c>), release of deleted payloads, and chunks of at most 64 KB.
/// </para>
/// <para>
/// Room for remote integration: every item records its origin and right origin, the client table takes any 53-bit client, split and
/// clean-start lookups work by (client, clock), and <see cref="Integrate"/> is the one place items enter a sequence. Its conflict-resolution
/// loop (Yjs <c>Item.integrate</c>, the scan between origin and right origin) is the only missing piece, and it throws where that loop would run.
/// Not built, per the amendment: that loop, codecs, events and the lock.
/// </para>
/// </summary>
public sealed class ObjectPerItemStore : IStoreDriver
{
    public const ulong DefaultClient = 0x1F_2345_6789_ABCD; // a 53-bit id above 2^32, so 32-bit truncation would show

    private readonly ClientTable _clients = new();
    private readonly int _local;
    private readonly TextBuffer _text = new();
    private readonly SmallStartList<int> _liveCharsPerChunk = new();
    private readonly ChunkedList<double> _values = new();
    private readonly MapPayloads _mapPayloads = new();
    private readonly MarkerClock _markerClock = new();
    private readonly Branch _textBranch;
    private readonly Branch _arrayBranch;
    private readonly Branch _mapBranch;

    // Transaction state, reused across transactions so a commit allocates nothing.
    private readonly DeleteSet _deleteSet = new();
    private readonly SmallStartList<Item> _mergeStructs = new();
    private bool _inTransaction;
    private uint _beforeClock;

    private long _structsCreated;
    private long _commits;

    public ObjectPerItemStore(ulong client = DefaultClient)
    {
        _local = _clients.SlotOf(client);
        _textBranch = Branch.Sequence(new SearchMarkers(_markerClock));
        _arrayBranch = Branch.Sequence(new SearchMarkers(_markerClock));
        _mapBranch = Branch.Keyed(new MapKeyTable(_mapPayloads));
    }

    // ---- IStoreDriver -------------------------------------------------------------------------------------------------------------------

    public int TextLength => _textBranch.Length;

    public DriverStats Stats => new(_structsCreated, _commits);

    public void InsertChar(int index, char value)
    {
        if ((uint)index > (uint)_textBranch.Length) throw new ArgumentOutOfRangeException(nameof(index));
        Begin();
        var pos = FindPosition(_textBranch, index);
        _textBranch.Markers!.Update(pos.Index, 1);

        Span<char> one = stackalloc char[1];
        one[0] = value;
        var slice = _text.Append(one);
        while (_liveCharsPerChunk.Count <= slice.Chunk) _liveCharsPerChunk.Add(0);
        _liveCharsPerChunk[slice.Chunk]++;

        var item = NewItem(_textBranch, pos.Left, pos.Right, ContentKind.Text, slice.Chunk, slice.Start, 1);
        Integrate(item);
    }

    public void DeleteAt(int index)
    {
        if ((uint)index >= (uint)_textBranch.Length) throw new ArgumentOutOfRangeException(nameof(index));
        Begin();
        var pos = FindPosition(_textBranch, index);

        // Yjs deleteText for one character
        var length = 1;
        while (length > 0 && pos.Right is not null)
        {
            var right = pos.Right;
            if (!right.Deleted)
            {
                if (length < right.Length) GetItemCleanStart(right.Client, right.Clock + (uint)length);
                length -= right.Length;
                Delete(right);
            }

            pos.Left = right;
            pos.Right = right.Right;
        }

        _textBranch.Markers!.Update(pos.Index, -1 + length);
    }

    public void PushValue(double value)
    {
        Begin();

        // Yjs typeListPushGenerics: start from the marker with the highest index, then walk to the end.
        var n = _arrayBranch.Start;
        if (n is not null)
        {
            while (n.Right is not null) n = n.Right;
        }

        var column = _values.Count;
        _values.Add(value);
        Integrate(NewItem(_arrayBranch, n, null, ContentKind.Values, column, 0, 1));
    }

    public void SetKey(string key, double value)
    {
        Begin();
        var map = _mapBranch.Map!;
        var left = map.Get(key.AsSpan());

        // The payload slot belongs to the key. The new item takes over the slot of the item it replaces, which this integration deletes,
        // so overwriting a key releases the old value and adds no payload.
        int payload;
        if (left is null)
        {
            payload = _mapPayloads.Add(key.AsSpan(), value);
        }
        else
        {
            payload = left.ParentSub;
            _mapPayloads.SetValue(payload, value);
        }

        var item = NewItem(_mapBranch, left, null, ContentKind.MapValue, payload, 0, 1);
        item.ParentSub = payload;
        Integrate(item);
    }

    /// <summary>Ends the transaction: the Yjs cleanup order of <c>cleanupTransactions</c>, without observers.</summary>
    public void Commit()
    {
        _commits++;
        if (!_inTransaction) return;

        TryGcDeleteSet();
        TryMergeDeleteSet();

        // Only the local client gains structs in the spike. Remote integration would keep a before-state per client, as Yjs does.
        var local = _clients[_local];
        var structs = local.Structs;
        if (_beforeClock != local.NextClock)
        {
            var firstChangePos = Math.Max(local.FindIndex(_beforeClock), 1);
            for (var i = structs.Count - 1; i >= firstChangePos;) i -= 1 + TryToMergeWithLefts(structs, i);
        }

        for (var i = _mergeStructs.Count - 1; i >= 0; i--)
        {
            var merged = _mergeStructs[i];
            var owner = _clients[merged.Client];
            var replacedPos = owner.FindIndex(merged.Clock);
            if (replacedPos + 1 < owner.Structs.Count && TryToMergeWithLefts(owner.Structs, replacedPos + 1) > 1) continue;
            if (replacedPos > 0) TryToMergeWithLefts(owner.Structs, replacedPos);
        }

        _deleteSet.Clear();
        for (var i = _mergeStructs.Count - 1; i >= 0; i--) _mergeStructs.RemoveAt(i);
        _inTransaction = false;
    }

    // ---- Inspection (tests and the storage ADR) ---------------------------------------------------------------------------------------

    public ulong LocalClient => _clients[_local].Id;

    public ItemRef TextStart => new(_textBranch.Start);

    public ItemRef ArrayStart => new(_arrayBranch.Start);

    /// <summary>The item that holds the current value of <paramref name="key"/>, or a null handle.</summary>
    public ItemRef MapEntry(string key) => new(_mapBranch.Map!.Get(key.AsSpan()));

    public ulong ClientOf(ItemRef item) => _clients[item.Item!.Client].Id;

    public (ulong Client, uint Clock)? OriginOf(ItemRef item) =>
        item.Item!.OriginClient == Item.NoClient ? null : (_clients[item.Item.OriginClient].Id, item.Item.OriginClock);

    public (ulong Client, uint Clock)? RightOriginOf(ItemRef item) =>
        item.Item!.RightOriginClient == Item.NoClient ? null : (_clients[item.Item.RightOriginClient].Id, item.Item.RightOriginClock);

    /// <summary>Number of structs the client table holds for <paramref name="client"/> (after merges).</summary>
    public int StructCountOf(ulong client)
    {
        var slot = _clients.TryGetSlot(client);
        return slot < 0 ? 0 : _clients[slot].Structs.Count;
    }

    /// <summary>The struct at <paramref name="index"/> in <paramref name="client"/>'s clock-ordered list.</summary>
    public ItemRef StructOf(ulong client, int index) => new(_clients[_clients.TryGetSlot(client)].Structs[index]);

    /// <summary>The delete set of the open transaction (empty after a commit).</summary>
    public DeleteSet PendingDeletes => _deleteSet;

    public int SearchMarkerCount => _textBranch.Markers!.Count;

    public int MapKeyCount => _mapBranch.Map!.Count;

    /// <summary>Text-buffer chunks whose every character belongs to released (garbage-collected) content.</summary>
    public int ReleasableTextChunks
    {
        get
        {
            var count = 0;
            for (var i = 0; i < _liveCharsPerChunk.Count - 1; i++) if (_liveCharsPerChunk[i] == 0) count++;
            return count;
        }
    }

    public string Text()
    {
        var sb = new StringBuilder(_textBranch.Length);
        for (var n = _textBranch.Start; n is not null; n = n.Right)
        {
            if (!n.Deleted && n.Kind == ContentKind.Text) sb.Append(_text.Get(new ContentSlice(n.ContentA, n.ContentB, n.Length)));
        }

        return sb.ToString();
    }

    public double[] ArrayValues()
    {
        var list = new List<double>(_arrayBranch.Length);
        for (var n = _arrayBranch.Start; n is not null; n = n.Right)
        {
            if (n.Deleted || n.Kind != ContentKind.Values) continue;
            for (var i = 0; i < n.Length; i++) list.Add(_values[n.ContentA + i]);
        }

        return list.ToArray();
    }

    public double? ValueOfKey(string key)
    {
        var item = _mapBranch.Map!.Get(key.AsSpan());
        return item is null || item.Deleted ? null : _mapPayloads.ValueOf(item.ContentA);
    }

    /// <summary>Largest single block any document-owned structure has allocated, in bytes.</summary>
    public long MaxChunkBytes =>
        Max(_clients.MaxChunkBytes, _text.MaxChunkBytes, _liveCharsPerChunk.MaxBlockBytes, _values.MaxBlockBytes, _mapPayloads.MaxChunkBytes,
            _mapBranch.Map!.MaxChunkBytes, _deleteSet.MaxChunkBytes, _mergeStructs.MaxBlockBytes);

    private static long Max(params ReadOnlySpan<long> values)
    {
        long max = 0;
        foreach (var v in values) max = Math.Max(max, v);
        return max;
    }

    // ---- Transactions ---------------------------------------------------------------------------------------------------------------------

    private void Begin()
    {
        if (_inTransaction) return;
        _inTransaction = true;
        _beforeClock = _clients[_local].NextClock;
    }

    // ---- Items -------------------------------------------------------------------------------------------------------------------------------

    private Item NewItem(Branch parent, Item? left, Item? right, ContentKind kind, int a, int b, int length)
    {
        var item = new Item(parent)
        {
            Left = left,
            Right = right,
            Client = _local,
            Clock = _clients[_local].NextClock,
            OriginClient = left is null ? Item.NoClient : left.Client,
            OriginClock = left is null ? 0 : left.LastClock,
            RightOriginClient = right is null ? Item.NoClient : right.Client,
            RightOriginClock = right is null ? 0 : right.Clock,
            Length = length,
            ContentA = a,
            ContentB = b,
            ParentSub = Item.NoParentSub,
            Kind = kind,
            Flags = ItemFlags.Countable,
        };
        _structsCreated++;
        return item;
    }

    /// <summary>
    /// Yjs <c>Item.integrate</c> for an item whose left and right are already resolved. The conflict-resolution scan between origin and right
    /// origin runs only when another item sits between them; a local insert never meets one. That scan is the remote-integration hook.
    /// </summary>
    private void Integrate(Item item)
    {
        var parent = item.Parent;
        var left = item.Left;
        var right = item.Right;
        if ((left is null && (right is null || right.Left is not null)) || (left is not null && left.Right != right))
        {
            var o = left is not null ? left.Right : FirstOfParent(item);
            if (o != right) throw new NotSupportedException("concurrent insert between origin and right origin: remote integration is not part of the spike");
        }

        // reconnect left and right, update the parent start or key table
        if (left is not null)
        {
            item.Right = left.Right;
            left.Right = item;
        }
        else
        {
            Item? r;
            if (item.ParentSub != Item.NoParentSub)
            {
                r = null; // a new key has no earlier item
            }
            else
            {
                r = parent.Start;
                parent.Start = item;
            }

            item.Right = r;
        }

        if (item.Right is not null)
        {
            item.Right.Left = item;
        }
        else if (item.ParentSub != Item.NoParentSub)
        {
            parent.Map!.Set(item);
            if (left is not null) Delete(left);
        }

        if (item.ParentSub == Item.NoParentSub && item.Countable && !item.Deleted) parent.Length += item.Length;
        _clients[item.Client].Structs.Add(item);
    }

    private static Item? FirstOfParent(Item item) => item.ParentSub == Item.NoParentSub ? item.Parent.Start : null;

    /// <summary>Yjs <c>Item.delete</c>.</summary>
    private void Delete(Item item)
    {
        if (item.Deleted) return;
        if (item.Countable && item.ParentSub == Item.NoParentSub) item.Parent.Length -= item.Length;
        item.Flags |= ItemFlags.Deleted;
        _deleteSet.Add(item.Client, item.Clock, (uint)item.Length);
    }

    /// <summary>
    /// Yjs <c>Item.gc</c> with the parent alive: the content becomes <see cref="ContentKind.Deleted"/> and the item stops referring to its
    /// payload. Text chunks count their live characters, so a chunk none of whose characters are live is known to be releasable. A map
    /// item keeps its payload index as its key (<see cref="Item.ParentSub"/>); the value slot was already taken over by the replacing item.
    /// </summary>
    private void Gc(Item item)
    {
        if (item.Kind == ContentKind.Text) _liveCharsPerChunk[item.ContentA] -= item.Length;
        item.Kind = ContentKind.Deleted;
        item.Flags &= ~ItemFlags.Countable;
        item.ContentA = 0;
        item.ContentB = 0;
    }

    /// <summary>Yjs <c>splitItem</c>: the right part becomes a new item placed after the left one in the client's list.</summary>
    private Item Split(Item left, int diff)
    {
        var right = new Item(left.Parent)
        {
            Left = left,
            Right = left.Right,
            Client = left.Client,
            Clock = left.Clock + (uint)diff,
            OriginClient = left.Client,
            OriginClock = left.Clock + (uint)diff - 1,
            RightOriginClient = left.RightOriginClient,
            RightOriginClock = left.RightOriginClock,
            Length = left.Length - diff,
            ParentSub = left.ParentSub,
            Kind = left.Kind,
            Flags = left.Flags & ~ItemFlags.Marker,
        };
        switch (left.Kind)
        {
            case ContentKind.Text:
                right.ContentA = left.ContentA;
                right.ContentB = left.ContentB + diff;
                break;
            case ContentKind.Values:
                right.ContentA = left.ContentA + diff;
                break;
            case ContentKind.MapValue:
                throw new InvalidOperationException("a map value has length 1 and cannot split");
        }

        left.Right = right;
        if (right.Right is not null) right.Right.Left = right;
        _mergeStructs.Add(right);
        if (right.ParentSub != Item.NoParentSub && right.Right is null) right.Parent.Map!.Set(right);
        left.Length = diff;
        _structsCreated++;
        return right;
    }

    /// <summary>Yjs <c>getItemCleanStart</c>: makes <paramref name="clock"/> the first clock of a struct, splitting if needed.</summary>
    private Item GetItemCleanStart(int client, uint clock)
    {
        var owner = _clients[client];
        var index = owner.FindIndex(clock);
        var item = owner.Structs[index];
        if (item.Clock < clock)
        {
            item = Split(item, (int)(clock - item.Clock));
            owner.Structs.Insert(index + 1, item);
        }

        return item;
    }

    // ---- Position lookup ----------------------------------------------------------------------------------------------------------------------

    private struct Position
    {
        public Item? Left;
        public Item? Right;
        public int Index;
    }

    /// <summary>Yjs <c>findPosition</c> with search markers, then <c>findNextPosition</c>.</summary>
    private Position FindPosition(Branch parent, int index)
    {
        var marker = parent.Markers!.Find(parent, index);
        var pos = marker is { } m
            ? new Position { Left = m.Item.Left, Right = m.Item, Index = m.Index }
            : new Position { Left = null, Right = parent.Start, Index = 0 };
        var count = index - pos.Index;
        while (pos.Right is not null && count > 0)
        {
            var right = pos.Right;
            if (!right.Deleted)
            {
                if (count < right.Length) GetItemCleanStart(right.Client, right.Clock + (uint)count);
                pos.Index += right.Length;
                count -= right.Length;
            }

            pos.Left = right;
            pos.Right = right.Right;
        }

        return pos;
    }

    // ---- Commit cleanup ----------------------------------------------------------------------------------------------------------------------

    /// <summary>Yjs <c>tryGcDeleteSet</c>.</summary>
    private void TryGcDeleteSet()
    {
        for (var di = _deleteSet.Count - 1; di >= 0; di--)
        {
            var range = _deleteSet[di];
            var owner = _clients[range.Client];
            for (var si = owner.FindIndex(range.Clock); si < owner.Structs.Count; si++)
            {
                var item = owner.Structs[si];
                if (item.Clock >= range.End) break;
                if (item.Deleted && (item.Flags & ItemFlags.Keep) == 0) Gc(item);
            }
        }
    }

    /// <summary>Yjs <c>tryMergeDeleteSet</c>: merge deleted structs from right to left.</summary>
    private void TryMergeDeleteSet()
    {
        for (var di = _deleteSet.Count - 1; di >= 0; di--)
        {
            var range = _deleteSet[di];
            var owner = _clients[range.Client];
            var structs = owner.Structs;
            var si = Math.Min(structs.Count - 1, 1 + owner.FindIndex(range.End - 1));
            while (si > 0 && structs[si].Clock >= range.Clock) si -= 1 + TryToMergeWithLefts(structs, si);
        }
    }

    /// <summary>Yjs <c>tryToMergeWithLefts</c>: returns how many structs merged into their left neighbours.</summary>
    private int TryToMergeWithLefts(SmallStartList<Item> structs, int pos)
    {
        var i = pos;
        for (; i > 0; i--)
        {
            var right = structs[i];
            var left = structs[i - 1];
            if (left.Deleted != right.Deleted || !MergeWith(left, right)) break;
            if (right.ParentSub != Item.NoParentSub && ReferenceEquals(right.Parent.Map!.Get(_mapPayloads.KeyOf(right.ParentSub)), right))
            {
                right.Parent.Map.Set(left);
            }
        }

        var merged = pos - i;
        structs.RemoveRange(pos + 1 - merged, merged);
        return merged;
    }

    /// <summary>Yjs <c>Item.mergeWith</c>, with content merges for slices (contiguous in one chunk) and value runs (contiguous in the column).</summary>
    private static bool MergeWith(Item left, Item right)
    {
        if (right.OriginClient != left.Client || right.OriginClock != left.LastClock) return false;
        if (!ReferenceEquals(left.Right, right)) return false;
        if (left.RightOriginClient != right.RightOriginClient ||
            (left.RightOriginClient != Item.NoClient && left.RightOriginClock != right.RightOriginClock)) return false;
        if (left.Client != right.Client || left.Clock + (uint)left.Length != right.Clock) return false;
        if (left.Deleted != right.Deleted || left.Kind != right.Kind) return false;

        switch (left.Kind)
        {
            case ContentKind.Text when left.ContentA != right.ContentA || left.ContentB + left.Length != right.ContentB:
            case ContentKind.Values when left.ContentA + left.Length != right.ContentA:
            case ContentKind.MapValue:
                return false;
        }

        left.Parent.Markers?.Repoint(right, left);
        if ((right.Flags & ItemFlags.Keep) != 0) left.Flags |= ItemFlags.Keep;
        left.Right = right.Right;
        if (left.Right is not null) left.Right.Left = left;
        left.Length += right.Length;
        return true;
    }
}
