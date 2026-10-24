using System;
using System.Collections.Generic;
using System.Text;
using YSharp.StoreSpike.Content;
using YSharp.StoreSpike.Harness;

namespace YSharp.StoreSpike.Arenas;

/// <summary>A sequence type (the text or the array): its first and last item and its visible length.</summary>
internal sealed class ArenaSequence
{
    public ArenaSequence(int id) => Id = id;

    public int Id { get; }

    public int Start = -1;
    public int Tail = -1;
    public int Length;
    public readonly SearchMarkers Markers = new();
}

/// <summary>
/// Spike candidate B (ADR 0018, M0 spike design): items are structs in chunked arenas, addressed by index. One local client edits a text "t",
/// an array "a" and a map "m" through <see cref="IStoreDriver"/>. Content goes through the shared content layer (text slices, the 8-byte value
/// column, map payloads).
/// </summary>
/// <remarks>
/// <para><b>Operations</b> (the ADR 0018 spike list): a client table with 53-bit ids and per-client sequences that support split; left, right,
/// origin and right-origin links and the parent sub-key, all as integers in the slot; local insert and delete; the Yjs <c>Item.mergeWith</c> rules at
/// commit (new structs, split structs and deleted ranges, as <c>cleanupTransactions</c> does); Yjs search markers; a chunked delete set; a map key
/// table; release of deleted payloads; every chunk at or below 64 KB. Remote integration would slot in where <see cref="CreateItem"/> links the
/// item: origins are kept as ids, not slot indexes, so the conflict loop of <c>Item.integrate</c> can run on them.</para>
/// <para><b>Index reuse: generation check.</b> See <see cref="ItemRef"/>. Only commit merges free slots.</para>
/// <para><b>Deleted-payload release.</b> At commit every newly deleted item becomes <see cref="ContentKind.Deleted"/> (length only, as Yjs
/// <c>Item.gc</c> does with <c>ContentDeleted</c>) and gives up its payload. Text: each text-buffer chunk has a live character count; a chunk whose
/// count reaches zero is released once it is no longer the chunk being appended to. Map: an overwritten key's old item hands its payload slot to the
/// new item, so one payload per key is ever held. Array: the value column is released the same way as text (a live count per column block) once
/// the driver can delete array elements; the spike workloads never do, so the column has no release path yet.</para>
/// </remarks>
public sealed class ArenaStore : IStoreDriver
{
    public const string CandidateId = "arenas";

    private const int TextType = 0;
    private const int ArrayType = 1;
    private const int MapType = 2;

    private readonly StructArena _arena = new();
    private readonly ClientTable _clients = new();
    private readonly ArenaDeleteSet _deleteSet = new();
    private readonly TextBuffer _text = new();
    private readonly ChunkedList<int> _textLive = new();
    private readonly ChunkedList<double> _column = new();
    private readonly MapPayloads _payloads = new();
    private readonly KeyTable _keys;
    private readonly ArenaSequence[] _sequences = { new(TextType), new(ArrayType), new(MapType) };
    private readonly int _local;

    // Transaction state, reused across transactions; it grows only with the edits of one transaction.
    private readonly List<StructId> _txnSplits = new();
    private readonly List<DeletedRange> _txnDeletes = new();
    private int _txnStartClock;

    private long _created;
    private long _commits;
    private int _releasedTextChunks;

    public ArenaStore(ulong clientId = 1)
    {
        _keys = new KeyTable(_arena, _payloads);
        _local = _clients.GetOrAdd(clientId);
    }

    public ulong ClientId => _clients.IdOf(_local);

    public int TextLength => _sequences[TextType].Length;

    public int ArrayLength => _sequences[ArrayType].Length;

    /// <summary>Every item the store created, including the right halves of splits (structs per operation in the harness).</summary>
    public DriverStats Stats => new(_created, _commits);

    /// <summary>Item slots in use now: created items minus those a commit merge folded into their left neighbour.</summary>
    public int LiveStructCount => _arena.LiveCount;

    public int FreeSlotCount => _arena.FreeCount;

    public int ReleasedTextChunks => _releasedTextChunks;

    public int TextMarkerCount => _sequences[TextType].Markers.Count;

    /// <summary>Largest chunk any document-owned structure allocated, in bytes.</summary>
    public long MaxChunkBytes =>
        Max(_arena.MaxChunkBytes, _clients.MaxChunkBytes, _deleteSet.MaxChunkBytes, _keys.MaxChunkBytes, _text.MaxChunkBytes, _textLive.MaxBlockBytes,
            _column.MaxBlockBytes, _payloads.MaxChunkBytes);

    // ---- IStoreDriver ----

    public void InsertChar(int index, char value)
    {
        var text = _sequences[TextType];
        if ((uint)index > (uint)text.Length) throw new ArgumentOutOfRangeException(nameof(index));
        Span<char> one = stackalloc char[1];
        one[0] = value;
        var slice = _text.Append(one);
        OnTextAppended(slice.Chunk);

        FindPosition(text, index, out var left, out var right);

        // YText.insertText updates the markers before it creates the item, so a marker on a deleted item moves left past the insertion point.
        text.Markers.Update(_arena, index, 1);
        CreateItem(text, left, right, ContentKind.Text, slice.Chunk, slice.Start, 1, -1);
        text.Length++;
    }

    public void DeleteAt(int index)
    {
        var text = _sequences[TextType];
        if ((uint)index >= (uint)text.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (!text.Markers.Find(_arena, text, index, out var p, out var pindex))
        {
            p = text.Start;
            pindex = 0;
        }

        for (; ; )
        {
            ref var slot = ref _arena[p];
            if (!slot.IsDeleted && index < pindex + slot.Length) break;
            pindex += slot.CountableLength;
            p = slot.Right;
        }

        if (index > pindex) p = Split(p, index - pindex);
        if (_arena[p].Length > 1) Split(p, 1);
        DeleteItem(p);
        text.Markers.Update(_arena, index, -1);
    }

    public void PushValue(double value)
    {
        var array = _sequences[ArrayType];
        var column = _column.Count;
        _column.Add(value);
        CreateItem(array, array.Tail, -1, ContentKind.Array, column, 0, 1, -1);
        array.Length++;
    }

    public void SetKey(string key, double value)
    {
        var map = _sequences[MapType];
        var previous = _keys.Find(key);
        int payload;
        if (previous >= 0)
        {
            // Yjs integrates the new item to the right of the key's current one and deletes that one. Its payload moves to the new item.
            ref var old = ref _arena[previous];
            payload = old.Payload0;
            _payloads.SetValue(payload, value);
            DeleteItem(previous);
            old.Kind = ContentKind.Deleted;
            old.Payload0 = 0;
        }
        else
        {
            payload = _payloads.Add(key.AsSpan(), value);
        }

        var item = CreateItem(map, previous, -1, ContentKind.Map, payload, 0, 1, payload);
        _keys.Set(key, item);
    }

    /// <summary>Ends the transaction: garbage-collects and merges deleted ranges, then merges new and split structs (Yjs <c>cleanupTransactions</c>).</summary>
    public void Commit()
    {
        foreach (var range in _txnDeletes) CollectRange(range);
        foreach (var range in _txnDeletes) MergeRange(range);

        var structs = _clients.Structs(_local);
        if (_clients.NextClock(_local) > _txnStartClock)
        {
            var first = Math.Max(1, _clients.FindIndex(_arena, _local, _txnStartClock));
            for (var i = structs.Count - 1; i >= first; i--) TryMergeWithLeft(_local, i);
        }

        for (var s = _txnSplits.Count - 1; s >= 0; s--)
        {
            var id = _txnSplits[s];
            var list = _clients.Structs(id.Client);
            var i = _clients.FindIndex(_arena, id.Client, id.Clock);
            if (i + 1 < list.Count && TryMergeWithLeft(id.Client, i + 1)) continue;
            if (i > 0) TryMergeWithLeft(id.Client, i);
        }

        _txnSplits.Clear();
        _txnDeletes.Clear();
        _txnStartClock = _clients.NextClock(_local);
        _commits++;
    }

    // ---- ItemRef accessors (the ADR 0021 seam) ----

    /// <summary>The item that contains <paramref name="clock"/> for <paramref name="client"/> (Yjs <c>getItem</c>).</summary>
    public ItemRef FindItem(ulong client, int clock)
    {
        var c = _clients.IndexOf(client);
        if (c < 0) throw new ArgumentException("unknown client", nameof(client));
        return _arena.RefOf(_clients.Structs(c)[_clients.FindIndex(_arena, c, clock)]);
    }

    public bool IsLive(ItemRef item) => _arena.IsLive(item);

    public ulong ClientOf(ItemRef item) => _clients.IdOf(Slot(item).Client);

    public int ClockOf(ItemRef item) => Slot(item).Clock;

    public int LengthOf(ItemRef item) => Slot(item).Length;

    public bool IsDeleted(ItemRef item) => Slot(item).IsDeleted;

    public ContentKind KindOf(ItemRef item) => Slot(item).Kind;

    public ItemRef LeftOf(ItemRef item) => RefOrNull(Slot(item).Left);

    public ItemRef RightOf(ItemRef item) => RefOrNull(Slot(item).Right);

    /// <summary>The key's current item, or <see cref="ItemRef.Null"/>.</summary>
    public ItemRef CurrentItemOf(string key) => RefOrNull(_keys.Find(key));

    /// <summary>Whether the delete set holds <paramref name="clock"/> of <paramref name="client"/>.</summary>
    public bool DeleteSetContains(ulong client, int clock)
    {
        var c = _clients.IndexOf(client);
        return c >= 0 && _deleteSet.IsDeleted(c, clock);
    }

    public int DeleteRangeCount(ulong client)
    {
        var c = _clients.IndexOf(client);
        return c < 0 ? 0 : _deleteSet.RangeCount(c);
    }

    // ---- Reading content back (self-tests) ----

    public string Text()
    {
        var sb = new StringBuilder(TextLength);
        for (var p = _sequences[TextType].Start; p >= 0; p = _arena[p].Right)
        {
            ref var slot = ref _arena[p];
            if (!slot.IsDeleted) sb.Append(_text.Get(new ContentSlice(slot.Payload0, slot.Payload1, slot.Length)));
        }

        return sb.ToString();
    }

    public double[] ArrayValues()
    {
        var values = new List<double>(ArrayLength);
        for (var p = _sequences[ArrayType].Start; p >= 0; p = _arena[p].Right)
        {
            ref var slot = ref _arena[p];
            if (slot.IsDeleted) continue;
            for (var k = 0; k < slot.Length; k++) values.Add(_column[slot.Payload0 + k]);
        }

        return values.ToArray();
    }

    public bool TryGetKey(string key, out double value)
    {
        var item = _keys.Find(key);
        if (item < 0 || _arena[item].IsDeleted)
        {
            value = 0;
            return false;
        }

        value = _payloads.ValueOf(_arena[item].Payload0);
        return true;
    }

    // ---- Internals ----

    private ref ItemSlot Slot(ItemRef item) => ref _arena[_arena.Resolve(item)];

    private ItemRef RefOrNull(int index) => index < 0 ? ItemRef.Null : _arena.RefOf(index);

    /// <summary>
    /// The neighbours a new item at <paramref name="index"/> goes between, splitting the item that spans the index (Yjs
    /// <c>typeListInsertGenerics</c>: the left neighbour is the item that ends at the index).
    /// </summary>
    private void FindPosition(ArenaSequence sequence, int index, out int left, out int right)
    {
        if (index == 0)
        {
            left = -1;
            right = sequence.Start;
            return;
        }

        if (!sequence.Markers.Find(_arena, sequence, index, out var p, out var pindex))
        {
            p = sequence.Start;
            pindex = 0;
        }

        // A marker exactly at the index: start from the item before it, as Yjs does.
        while (pindex == index && _arena[p].Left >= 0)
        {
            p = _arena[p].Left;
            pindex -= _arena[p].CountableLength;
        }

        for (; p >= 0; p = _arena[p].Right)
        {
            ref var slot = ref _arena[p];
            if (slot.IsDeleted) continue;
            if (index <= pindex + slot.Length)
            {
                if (index < pindex + slot.Length) Split(p, index - pindex);
                left = p;
                right = _arena[p].Right;
                return;
            }

            pindex += slot.Length;
        }

        throw new InvalidOperationException("index past the end of the sequence");
    }

    private int CreateItem(ArenaSequence sequence, int left, int right, ContentKind kind, int payload0, int payload1, int length, int parentSub)
    {
        var index = _arena.Allocate();
        ref var slot = ref _arena[index];
        slot.Client = _local;
        slot.Clock = _clients.NextClock(_local);
        slot.Length = length;
        slot.Left = left;
        slot.Right = right;
        slot.OriginClient = -1;
        slot.OriginClock = -1;
        slot.RightOriginClient = -1;
        slot.RightOriginClock = -1;
        if (left >= 0)
        {
            ref var l = ref _arena[left];
            slot.OriginClient = l.Client;
            slot.OriginClock = l.Clock + l.Length - 1;
            l.Right = index;
        }
        else if (parentSub < 0)
        {
            sequence.Start = index;
        }

        if (right >= 0)
        {
            ref var r = ref _arena[right];
            slot.RightOriginClient = r.Client;
            slot.RightOriginClock = r.Clock;
            r.Left = index;
        }
        else if (parentSub < 0)
        {
            sequence.Tail = index;
        }

        slot.Parent = sequence.Id;
        slot.ParentSub = parentSub;
        slot.Kind = kind;
        slot.Payload0 = payload0;
        slot.Payload1 = payload1;

        _clients.Structs(_local).Add(index);
        _clients.AdvanceClock(_local, length);
        _created++;
        return index;
    }

    /// <summary>Yjs <c>splitItem</c>: the right part becomes a new slot after <paramref name="item"/> in its client's sequence. Returns the right part.</summary>
    private int Split(int item, int diff)
    {
        var index = _arena.Allocate();
        ref var l = ref _arena[item];
        ref var r = ref _arena[index];
        var position = _clients.FindIndex(_arena, l.Client, l.Clock);

        r.Client = l.Client;
        r.Clock = l.Clock + diff;
        r.Length = l.Length - diff;
        r.Left = item;
        r.Right = l.Right;
        r.OriginClient = l.Client;
        r.OriginClock = l.Clock + diff - 1;
        r.RightOriginClient = l.RightOriginClient;
        r.RightOriginClock = l.RightOriginClock;
        r.Parent = l.Parent;
        r.ParentSub = l.ParentSub;
        r.Kind = l.Kind;
        r.Flags = l.Flags & ItemFlags.Deleted;
        switch (l.Kind)
        {
            case ContentKind.Text:
                r.Payload0 = l.Payload0;
                r.Payload1 = l.Payload1 + diff;
                break;
            case ContentKind.Array:
                r.Payload0 = l.Payload0 + diff;
                break;
            case ContentKind.Deleted:
                break;
            default:
                throw new InvalidOperationException("content of kind " + l.Kind + " cannot be split");
        }

        if (r.Right >= 0) _arena[r.Right].Left = index;
        else if (r.ParentSub < 0 && _sequences[r.Parent].Tail == item) _sequences[r.Parent].Tail = index;
        l.Right = index;
        l.Length = diff;

        _clients.Structs(r.Client).Insert(position + 1, index);
        _txnSplits.Add(new StructId(r.Client, r.Clock));
        _created++;
        return index;
    }

    private void DeleteItem(int item)
    {
        ref var slot = ref _arena[item];
        if (slot.IsDeleted) return;
        slot.Flags |= ItemFlags.Deleted;
        if (slot.ParentSub < 0) _sequences[slot.Parent].Length -= slot.Length;
        _deleteSet.Add(slot.Client, slot.Clock, slot.Length);
        _txnDeletes.Add(new DeletedRange(slot.Client, slot.Clock, slot.Length));
    }

    /// <summary>Yjs <c>tryGcDeleteSet</c>: every deleted item in the range keeps its id and length only.</summary>
    private void CollectRange(DeletedRange range)
    {
        var structs = _clients.Structs(range.Client);
        for (var i = _clients.FindIndex(_arena, range.Client, range.Clock); i < structs.Count; i++)
        {
            ref var slot = ref _arena[structs[i]];
            if (slot.Clock >= range.Clock + range.Length) break;
            if (slot.IsDeleted) ReleasePayload(ref slot);
        }
    }

    /// <summary>Yjs <c>tryMergeDeleteSet</c>.</summary>
    private void MergeRange(DeletedRange range)
    {
        var structs = _clients.Structs(range.Client);
        var mostRight = Math.Min(structs.Count - 1, 1 + _clients.FindIndex(_arena, range.Client, range.Clock + range.Length - 1));
        for (var i = mostRight; i > 0 && _arena[structs[i]].Clock >= range.Clock; i--) TryMergeWithLeft(range.Client, i);
    }

    private void ReleasePayload(ref ItemSlot slot)
    {
        if (slot.Kind == ContentKind.Text) OnTextReleased(slot.Payload0, slot.Length);
        slot.Kind = ContentKind.Deleted;
        slot.Payload0 = 0;
        slot.Payload1 = 0;
    }

    /// <summary>Yjs <c>Item.mergeWith</c> on the client's items at <paramref name="i"/> - 1 and <paramref name="i"/>. The right slot is freed.</summary>
    private bool TryMergeWithLeft(int client, int i)
    {
        var structs = _clients.Structs(client);
        var li = structs[i - 1];
        var ri = structs[i];
        ref var l = ref _arena[li];
        ref var r = ref _arena[ri];

        if (l.Kind != r.Kind || l.IsDeleted != r.IsDeleted) return false;
        if (r.OriginClient != l.Client || r.OriginClock != l.Clock + l.Length - 1) return false;
        if (l.Right != ri) return false;
        if (l.RightOriginClient != r.RightOriginClient || l.RightOriginClock != r.RightOriginClock) return false;
        if (l.Clock + l.Length != r.Clock) return false;
        var contentMerges = l.Kind switch
        {
            ContentKind.Text => l.Payload0 == r.Payload0 && l.Payload1 + l.Length == r.Payload1,
            ContentKind.Array => l.Payload0 + l.Length == r.Payload0,
            ContentKind.Deleted => true,
            _ => false,
        };
        if (!contentMerges) return false;

        // Every marker on the right item moves, flagged or not: a slot must not be freed while a marker still points at it.
        _sequences[l.Parent].Markers.Repoint(_arena, ri, li);
        l.Right = r.Right;
        if (l.Right >= 0) _arena[l.Right].Left = li;
        else if (r.ParentSub < 0 && _sequences[r.Parent].Tail == ri) _sequences[r.Parent].Tail = li;
        l.Length += r.Length;

        structs.RemoveAt(i);
        _arena.Free(ri);
        return true;
    }

    private void OnTextAppended(int chunk)
    {
        if (chunk == _textLive.Count)
        {
            _textLive.Add(0);
            if (chunk > 0 && _textLive[chunk - 1] == 0) ReleaseTextChunk(chunk - 1);
        }

        _textLive[chunk]++;
    }

    private void OnTextReleased(int chunk, int chars)
    {
        var live = _textLive[chunk] - chars;
        _textLive[chunk] = live;
        if (live == 0 && chunk != _textLive.Count - 1) ReleaseTextChunk(chunk);
    }

    private void ReleaseTextChunk(int chunk)
    {
        _text.Release(chunk);
        _releasedTextChunks++;
    }

    private static long Max(params ReadOnlySpan<long> values)
    {
        long max = 0;
        foreach (var v in values) max = Math.Max(max, v);
        return max;
    }

    private readonly record struct StructId(int Client, int Clock);

    private readonly record struct DeletedRange(int Client, int Clock, int Length);
}
