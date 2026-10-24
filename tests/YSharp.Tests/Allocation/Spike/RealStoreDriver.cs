using System;
using System.Collections.Generic;
using System.Text;
using YSharp.Store;

namespace YSharp.StoreHarness;

/// <summary>
/// The third driver adapter of the spike harness (ADR 0018 spike amendment, ADR 0023): the workloads run on the real <c>Store/</c> module. It plays
/// the parts the later M2 modules will own, using only the store's layout operations: local insert and delete (Yjs <c>insertText</c>,
/// <c>deleteText</c>, <c>typeListPushGenerics</c>, <c>typeMapSet</c> with no concurrent items, so no conflict scan), and the commit cleanup of
/// <c>cleanupTransactions</c> without observers (garbage-collect the delete set, merge it, merge the new structs and the split ones).
/// One document holds a text "t", an array "a" and a map "m".
/// </summary>
public sealed class RealStoreDriver : IStoreDriver
{
    public const ulong DefaultClient = 0x1F_2345_6789_ABCD; // a 53-bit id above 2^32, so 32-bit truncation would show

    private readonly ulong _client;
    private readonly StructStore _store = new();
    private readonly Branch _text = new(useMarkers: true);
    private readonly Branch _array = new(useMarkers: true);
    private readonly Branch _map = new();

    // Transaction state, reused across transactions so a commit allocates nothing.
    private readonly DeleteRanges _deleteSet = new();
    private bool _inTransaction;
    private long _beforeClock;

    private long _structsCreated;
    private long _commits;

    public RealStoreDriver(ulong client = DefaultClient)
    {
        if (client > ClientTable.MaxClientId) throw new ArgumentOutOfRangeException(nameof(client), "Client ids are 53-bit.");
        _client = client;
        _store.SlotOf(client);
    }

    // ---- IStoreDriver -------------------------------------------------------------------------------------------------------------------

    public int TextLength => _text.Length;

    public DriverStats Stats => new(_structsCreated, _commits);

    public void InsertChar(int index, char value)
    {
        if ((uint)index > (uint)_text.Length) throw new ArgumentOutOfRangeException(nameof(index));
        Begin();
        var pos = FindPosition(_text, index);
        _text.MarkersFor(_store)!.Update(pos.Index, 1);

        Span<char> one = stackalloc char[1];
        one[0] = value;
        var address = _store.AppendText(one);
        var item = _store.CreateLocalItem(_client, _text, pos.Left, pos.Right, default, false, ContentKind.String, address, 1);
        _store.Link(item, pos.Left);
        _structsCreated++;
    }

    public void DeleteAt(int index)
    {
        if ((uint)index >= (uint)_text.Length) throw new ArgumentOutOfRangeException(nameof(index));
        Begin();
        var pos = FindPosition(_text, index);

        // Yjs deleteText for one character
        long length = 1;
        while (length > 0 && !pos.Right.IsNull)
        {
            var right = pos.Right;
            if (!right.IsDeleted)
            {
                if (length < right.Length) _store.GetItemCleanStart(_store.ClientOf(right), right.Clock + length);
                length -= right.Length;
                _store.Delete(right, _deleteSet);
            }

            pos.Left = right;
            pos.Right = right.Right;
        }

        _text.MarkersFor(_store)!.Update(pos.Index, -1 + (int)length);
    }

    public void PushValue(double value)
    {
        Begin();
        // The branch keeps its last item, which is where Yjs typeListPushGenerics' walk ends, so a push is O(1) (ADR 0023).
        var left = new ItemRef(_array.End);
        var column = _store.AppendValue(value);
        var item = _store.CreateLocalItem(_client, _array, left, ItemRef.Null, default, false, ContentKind.Any, column, 1);
        _store.Link(item, left);
        _structsCreated++;
    }

    public void SetKey(string key, double value)
    {
        Begin();
        var left = new ItemRef(_map.Map?.Get(key));
        var column = _store.AppendValue(value);
        var item = _store.CreateLocalItem(_client, _map, left, ItemRef.Null, key, true, ContentKind.Any, column, 1);
        _store.Link(item, left);
        if (!left.IsNull) _store.Delete(left, _deleteSet); // the replaced value (Yjs Item.integrate, the parentSub branch)
        _structsCreated++;
    }

    /// <summary>Ends the transaction: the Yjs cleanup order of <c>cleanupTransactions</c>, without observers.</summary>
    public void Commit()
    {
        _commits++;
        if (!_inTransaction) return;

        TryGcDeleteSet();
        TryMergeDeleteSet();

        // Only the local client gains structs here. The transaction module keeps a before-state per client, as Yjs does.
        var local = Local;
        if (_beforeClock != local.NextClock)
        {
            var firstChangePos = Math.Max(local.FindIndex(_beforeClock), 1);
            for (var i = local.Structs.Count - 1; i >= firstChangePos;) i -= 1 + _store.TryToMergeWithLefts(local, i);
        }

        var merges = _store.MergeStructs;
        for (var i = merges.Count - 1; i >= 0; i--)
        {
            var merged = merges[i];
            var owner = _store.Clients[merged.Client];
            var replacedPos = owner.FindIndex(merged.Clock);
            if (replacedPos + 1 < owner.Structs.Count && _store.TryToMergeWithLefts(owner, replacedPos + 1) > 1) continue;
            if (replacedPos > 0) _store.TryToMergeWithLefts(owner, replacedPos);
        }

        _deleteSet.Clear();
        merges.Clear();
        _inTransaction = false;
    }

    // ---- Inspection, for tests -------------------------------------------------------------------------------------------------------

    internal StructStore Store => _store;

    public ulong LocalClient => _client;

    internal ItemRef TextStart => new(_text.Start);

    /// <summary>The item that holds the current value of <paramref name="key"/>, or a null handle.</summary>
    internal ItemRef MapEntry(string key) => new(_map.Map?.Get(key));

    public int StructCountOf(ulong client) => _store.StructCount(client);

    /// <summary>The delete set of the open transaction (empty after a commit).</summary>
    internal DeleteRanges PendingDeletes => _deleteSet;

    public int SearchMarkerCount => _text.Markers?.Count ?? 0;

    public int MapKeyCount => _map.Map?.Count ?? 0;

    /// <summary>Text chunks released because none of their characters is live.</summary>
    public int ReleasedTextChunks => _store.Text.ReleasedChunks;

    public string Text()
    {
        var sb = new StringBuilder(_text.Length);
        for (var n = new ItemRef(_text.Start); !n.IsNull; n = n.Right)
        {
            if (!n.IsDeleted && n.Kind == ContentKind.String) _store.AppendString(sb, n);
        }

        return sb.ToString();
    }

    public double[] ArrayValues()
    {
        var list = new List<double>(_array.Length);
        for (var n = new ItemRef(_array.Start); !n.IsNull; n = n.Right)
        {
            if (n.IsDeleted || n.Kind != ContentKind.Any) continue;
            for (var i = 0; i < n.Length; i++) list.Add(_store.GetValue(n, i).AsNumber());
        }

        return list.ToArray();
    }

    public double? ValueOfKey(string key)
    {
        var item = MapEntry(key);
        return item.IsNull || item.IsDeleted ? null : _store.GetValue(item, 0).AsNumber();
    }

    /// <summary>Largest single block any document-owned structure has allocated, in bytes.</summary>
    public long MaxChunkBytes => Math.Max(_store.MaxChunkBytes, Math.Max(_map.Map?.MaxChunkBytes ?? 0, _deleteSet.MaxChunkBytes));

    // ---- Transactions -------------------------------------------------------------------------------------------------------------------

    private ClientStructs Local => _store.Clients[_store.Clients.TryGetSlot(_client)];

    private void Begin()
    {
        if (_inTransaction) return;
        _inTransaction = true;
        _beforeClock = Local.NextClock;
    }

    // ---- Position lookup ----------------------------------------------------------------------------------------------------------------------

    private struct Position
    {
        public ItemRef Left;
        public ItemRef Right;
        public int Index;
    }

    /// <summary>Yjs <c>findPosition</c> with search markers, then <c>findNextPosition</c>.</summary>
    private Position FindPosition(Branch parent, int index)
    {
        var marker = parent.MarkersFor(_store)!.Find(parent, index);
        var pos = marker is { } m
            ? new Position { Left = new ItemRef(m.Item.Left), Right = new ItemRef(m.Item), Index = m.Index }
            : new Position { Left = ItemRef.Null, Right = new ItemRef(parent.Start), Index = 0 };
        var count = index - pos.Index;
        while (!pos.Right.IsNull && count > 0)
        {
            var right = pos.Right;
            if (!right.IsDeleted)
            {
                if (count < right.Length) _store.GetItemCleanStart(_store.ClientOf(right), right.Clock + count);
                pos.Index += (int)right.Length;
                count -= (int)right.Length;
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
            var owner = _store.Clients[_store.Clients.TryGetSlot(range.Client)];
            for (var si = owner.FindIndex(range.Clock); si < owner.Structs.Count; si++)
            {
                var item = new ItemRef(owner.Structs[si]);
                if (item.Clock >= range.End) break;
                if (!item.IsGc && item.IsDeleted && !item.IsKeep) _store.GcContent(item);
            }
        }
    }

    /// <summary>Yjs <c>tryMergeDeleteSet</c>: merge deleted structs from right to left.</summary>
    private void TryMergeDeleteSet()
    {
        for (var di = _deleteSet.Count - 1; di >= 0; di--)
        {
            var range = _deleteSet[di];
            var owner = _store.Clients[_store.Clients.TryGetSlot(range.Client)];
            var structs = owner.Structs;
            var si = Math.Min(structs.Count - 1, 1 + owner.FindIndex(range.End - 1));
            while (si > 0 && structs[si].Clock >= range.Clock) si -= 1 + _store.TryToMergeWithLefts(owner, si);
        }
    }
}
