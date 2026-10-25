#nullable enable
using System;
using System.Collections.Generic;
using YSharp.Store;

namespace YSharp.Transactions;

/// <summary>
/// The pooled state behind a <see cref="Transaction"/>. A document reuses these across transactions so their arrays and collections are not
/// reallocated; each life gets a fresh <see cref="Transaction"/> façade (<see cref="Public"/>), which goes stale for good when the life ends (CC-9).
/// </summary>
internal sealed class TxState
{
    internal readonly Doc Doc;
    internal readonly bool IsWrite;
    internal object? Origin;
    internal bool IsLocal;

    /// <summary>The façade of the current life; null between lives.</summary>
    internal Transaction? Public;

    // clocks by client slot, taken from the store (Yjs beforeState and afterState); the arrays are reused across lives
    private long[] _before = new long[8];
    private int _beforeCount;
    private long[] _after = new long[8];
    private int _afterCount = -1;   // -1: not taken yet (Yjs afterState is empty until cleanup)

    private Dictionary<YType, HashSet<string?>>? _changed;
    private Dictionary<YType, List<YEvent>>? _changedParentTypes;

    internal TxState(Doc doc, bool write)
    {
        Doc = doc;
        IsWrite = write;
    }

    /// <summary>What this transaction deleted (Yjs <c>transaction.deleteSet</c>).</summary>
    internal readonly DeleteRanges DeleteRanges = new();

    internal Dictionary<YType, HashSet<string?>>? ChangedOrNull => _changed;

    internal Dictionary<YType, List<YEvent>>? ChangedParentTypesOrNull => _changedParentTypes;

    internal Dictionary<YType, HashSet<string?>> Changed => _changed ??= new Dictionary<YType, HashSet<string?>>(ReferenceEqualityComparer.Instance);

    internal Dictionary<YType, List<YEvent>> ChangedParentTypes =>
        _changedParentTypes ??= new Dictionary<YType, List<YEvent>>(ReferenceEqualityComparer.Instance);

    /// <summary>Starts a life: takes the before state and hands out a new façade, the one allocation of an uncontended transaction (BUD-01, BUD-02).</summary>
    internal Transaction Begin(object? origin, bool local)
    {
        Origin = origin;
        IsLocal = local;
        _beforeCount = Snapshot(ref _before);
        _afterCount = -1;
        return Public = new Transaction(this);
    }

    /// <summary>Takes the after state at the start of cleanup (Yjs <c>transaction.afterState = getStateVector(store)</c>).</summary>
    internal void TakeAfterState() => _afterCount = Snapshot(ref _after);

    /// <summary>Ends the life: its façade and every collection it returned go stale, and the state is cleared for reuse.</summary>
    internal void End()
    {
        Public?.Invalidate();
        Public = null;
        Origin = null;
        DeleteRanges.Clear();
        _changed?.Clear();
        _changedParentTypes?.Clear();
    }

    internal long BeforeClock(ulong client)
    {
        var slot = Doc.Store.Clients.TryGetSlot(client);
        return slot >= 0 && slot < _beforeCount ? _before[slot] : 0;
    }

    internal long AfterClock(ulong client)
    {
        var slot = Doc.Store.Clients.TryGetSlot(client);
        return slot >= 0 && slot < _afterCount ? _after[slot] : 0;
    }

    internal StateVector BeforeState => ToStateVector(_before, _beforeCount);

    internal StateVector AfterState => ToStateVector(_after, Math.Max(0, _afterCount));

    private int Snapshot(ref long[] into)
    {
        var clients = Doc.Store.Clients;
        var count = clients.Count;
        if (into.Length < count) into = new long[Math.Max(count, into.Length * 2)];
        for (var slot = 0; slot < count; slot++) into[slot] = clients[slot].NextClock;
        return count;
    }

    private StateVector ToStateVector(long[] clocks, int count)
    {
        var clients = Doc.Store.Clients;
        var entries = new List<KeyValuePair<ulong, long>>(count);
        for (var slot = 0; slot < count; slot++)
            if (clocks[slot] > 0) entries.Add(new KeyValuePair<ulong, long>(clients[slot].Id, clocks[slot]));
        return StateVector.Create(entries.ToArray());
    }
}
