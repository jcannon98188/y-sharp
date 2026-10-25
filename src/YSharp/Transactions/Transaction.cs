#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using YSharp.Store;

namespace YSharp;

// ---------------------------------------------------------------- transactions (ADR 0006)

/// <summary>
/// One read or write transaction (Yjs <c>Transaction</c>). The object is pooled by its document: once the transaction ends <see cref="IsValid"/>
/// turns false and every other member, and every collection it returned, throws <see cref="YLifetimeException"/> (CC-9).
/// </summary>
public sealed class Transaction
{
    private readonly Doc _doc;
    private readonly bool _write;
    private object? _origin;
    private bool _local;
    private bool _live;
    private int _generation;

    // clocks by client slot, taken from the store (Yjs beforeState and afterState); arrays are reused across the pooled object's lives
    private long[] _before = new long[8];
    private int _beforeCount;
    private long[] _after = new long[8];
    private int _afterCount = -1;   // -1: not taken yet (Yjs afterState is empty until cleanup)

    private Dictionary<YType, HashSet<string?>>? _changed;
    private Dictionary<YType, List<YEvent>>? _changedParentTypes;

    internal Transaction(Doc doc, bool write)
    {
        _doc = doc;
        _write = write;
    }

    public Doc Doc => Live()._doc;

    public object? Origin => Live()._origin;

    /// <summary>True for changes made on this document, false for a remote update being applied (Yjs <c>local</c>).</summary>
    public bool IsLocal => Live()._local;

    public bool IsWrite => Live()._write;

    /// <summary>The document's state vector when the transaction started.</summary>
    public StateVector BeforeState => ToStateVector(Live()._before, _beforeCount);

    /// <summary>The state vector when cleanup started; empty until then, as in Yjs.</summary>
    public StateVector AfterState => ToStateVector(Live()._after, Math.Max(0, _afterCount));

    /// <summary>A copy of what this transaction deleted so far.</summary>
    public DeleteSet DeleteSet
    {
        get
        {
            Live();
            var copy = new DeleteRanges();
            for (var i = 0; i < DeleteRanges.Count; i++)
            {
                var r = DeleteRanges[i];
                copy.Add(r.Client, r.Clock, r.Length);
            }

            return new DeleteSet(copy);
        }
    }

    public IReadOnlyCollection<YType> ChangedTypes => new LiveKeys<HashSet<string?>>(Live(), _generation, () => _changed);

    public IReadOnlyCollection<YType> ChangedParentTypes => new LiveKeys<List<YEvent>>(Live(), _generation, () => _changedParentTypes);

    /// <summary>False once the transaction ended (CC-9). The only member that never throws.</summary>
    public bool IsValid => _live;

    // ------------------------------------------------------------------ internal state for the later modules

    /// <summary>What this transaction deleted (Yjs <c>transaction.deleteSet</c>).</summary>
    internal readonly DeleteRanges DeleteRanges = new();

    /// <summary>The changed types and the keys changed on each, null standing for a list change (Yjs <c>transaction.changed</c>).</summary>
    internal Dictionary<YType, HashSet<string?>> Changed => _changed ??= new Dictionary<YType, HashSet<string?>>(ReferenceEqualityComparer.Instance);

    /// <summary>Events per parent type for deep observers (Yjs <c>transaction.changedParentTypes</c>).</summary>
    internal Dictionary<YType, List<YEvent>> ChangedParentTypesMap =>
        _changedParentTypes ??= new Dictionary<YType, List<YEvent>>(ReferenceEqualityComparer.Instance);

    /// <summary>The clock <paramref name="client"/> had when the transaction started (Yjs <c>beforeState.get(client) || 0</c>).</summary>
    internal long BeforeClock(ulong client)
    {
        var slot = _doc.Store.Clients.TryGetSlot(client);
        return slot >= 0 && slot < _beforeCount ? _before[slot] : 0;
    }

    /// <summary>The clock <paramref name="client"/> had when cleanup started, 0 before that.</summary>
    internal long AfterClock(ulong client)
    {
        var slot = _doc.Store.Clients.TryGetSlot(client);
        return slot >= 0 && slot < _afterCount ? _after[slot] : 0;
    }

    /// <summary>Starts a life of the pooled object: takes the before state. Allocates only when the client table outgrew the reused array.</summary>
    internal void Begin(object? origin, bool local)
    {
        _origin = origin;
        _local = local;
        _beforeCount = Snapshot(ref _before);
        _afterCount = -1;
        _live = true;
    }

    /// <summary>Takes the after state at the start of cleanup (Yjs <c>transaction.afterState = getStateVector(store)</c>).</summary>
    internal void TakeAfterState() => _afterCount = Snapshot(ref _after);

    /// <summary>Ends this life: the object and every collection it returned go stale, and the per-transaction state is cleared for reuse.</summary>
    internal void End()
    {
        _live = false;
        _generation++;
        _origin = null;
        DeleteRanges.Clear();
        _changed?.Clear();
        _changedParentTypes?.Clear();
    }

    internal bool IsLiveGeneration(int generation) => _live && _generation == generation;

    private Transaction Live() => _live ? this : throw new YLifetimeException();

    private int Snapshot(ref long[] into)
    {
        var clients = _doc.Store.Clients;
        var count = clients.Count;
        if (into.Length < count) into = new long[Math.Max(count, into.Length * 2)];
        for (var slot = 0; slot < count; slot++) into[slot] = clients[slot].NextClock;
        return count;
    }

    private StateVector ToStateVector(long[] clocks, int count)
    {
        var clients = _doc.Store.Clients;
        var entries = new List<KeyValuePair<ulong, long>>(count);
        for (var slot = 0; slot < count; slot++)
            if (clocks[slot] > 0) entries.Add(new KeyValuePair<ulong, long>(clients[slot].Id, clocks[slot]));
        return StateVector.Create(entries.ToArray());
    }

    /// <summary>A live view of a dictionary's keys that throws once the transaction that produced it ended (CC-9).</summary>
    private sealed class LiveKeys<TValue> : IReadOnlyCollection<YType>
    {
        private readonly Transaction _tx;
        private readonly int _generation;
        private readonly Func<Dictionary<YType, TValue>?> _source;

        public LiveKeys(Transaction tx, int generation, Func<Dictionary<YType, TValue>?> source)
        {
            _tx = tx;
            _generation = generation;
            _source = source;
        }

        private Dictionary<YType, TValue>? Source => _tx.IsLiveGeneration(_generation) ? _source() : throw new YLifetimeException();

        public int Count => Source?.Count ?? 0;

        public IEnumerator<YType> GetEnumerator()
        {
            var source = Source;
            if (source == null) yield break;
            foreach (var key in source.Keys)
            {
                yield return key;
                if (!_tx.IsLiveGeneration(_generation)) throw new YLifetimeException();
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
