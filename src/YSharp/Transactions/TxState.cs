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
    private List<YType>? _changedParentTypes;
    private readonly Stack<HashSet<string?>> _keySets = new();   // the cleared key sets of earlier lives (BUD-04, BUD-05)

    /// <summary>
    /// The keys of <see cref="Changed"/> in the order they were first added (a JS Map's order, which the observers run in). A type removed
    /// from the map stays here; the dispatch skips it.
    /// </summary>
    internal readonly List<YType> ChangedOrder = new();

    /// <summary>Set when the observers of this life start (Yjs fills <c>changedParentTypes</c> while it calls them).</summary>
    internal bool ObserversCalled;

    /// <summary>The snapshots whose structs this life has split (Yjs <c>transaction.meta.get(splitSnapshotAffectedStructs)</c>).</summary>
    internal HashSet<YSnapshot>? SplitSnapshots;

    internal TxState(Doc doc, bool write)
    {
        Doc = doc;
        IsWrite = write;
    }

    /// <summary>What this transaction deleted (Yjs <c>transaction.deleteSet</c>).</summary>
    internal readonly DeleteRanges DeleteRanges = new();

    internal Dictionary<YType, HashSet<string?>>? ChangedOrNull => _changed;

    internal Dictionary<YType, HashSet<string?>> Changed => _changed ??= new Dictionary<YType, HashSet<string?>>(ReferenceEqualityComparer.Instance);

    /// <summary>An empty key set for <see cref="Changed"/>, reused from an earlier life: an event of that life holds its set but is stale (M-E5).</summary>
    internal HashSet<string?> RentKeySet() => _keySets.TryPop(out var set) ? set : new HashSet<string?>();

    /// <summary>
    /// Yjs <c>transaction.changedParentTypes</c>: every changed type whose observers ran and each of its ancestors, in the order JS adds them.
    /// Built on first use from the changed types, so a commit nobody asks this of allocates nothing; empty before the observers run.
    /// </summary>
    internal List<YType>? ChangedParentTypesOrNull
    {
        get
        {
            if (!ObserversCalled || _changed is not { Count: > 0 } changed) return null;
            if (_changedParentTypes is { } built) return built;
            var list = new List<YType>();
            var seen = new HashSet<YType>(ReferenceEqualityComparer.Instance);
            foreach (var type in ChangedOrder)
            {
                // Yjs AbstractType._callObserver of the root placeholder adds nothing; a deleted type's observers do not run
                if (!changed.ContainsKey(type) || type is YUntyped || type.Branch.Item is { Deleted: true }) continue;
                for (var branch = type.Branch; ; branch = branch.Item!.Parent!)
                {
                    var t = Doc.TypeOf(branch);
                    if (seen.Add(t)) list.Add(t);
                    if (branch.Item is null) break;
                }
            }

            return _changedParentTypes = list;
        }
    }

    /// <summary>
    /// Yjs <c>writeUpdateMessageFromTransaction</c>'s test: something was deleted, or a client's clock moved between the start of the
    /// transaction and the start of its cleanup.
    /// </summary>
    internal bool HasContent
    {
        get
        {
            if (DeleteRanges.Count > 0) return true;
            for (var slot = 0; slot < _afterCount; slot++)
                if (_after[slot] != (slot < _beforeCount ? _before[slot] : 0)) return true;
            return false;
        }
    }

    /// <summary>Starts a life: takes the before state and hands out a new façade, the one allocation of an uncontended transaction (BUD-01, BUD-02).</summary>
    internal Transaction Begin(object? origin, bool local)
    {
        BeginRead();
        Origin = origin;
        IsLocal = local;
        return Public = new Transaction(this);
    }

    /// <summary>Starts a read life without a façade; <see cref="Facade"/> makes it on first use (BUD-06: the library's own reads allocate nothing).</summary>
    internal void BeginRead()
    {
        Origin = null;
        IsLocal = true;
        _beforeCount = Snapshot(ref _before);
        _afterCount = -1;
        Public = null;
    }

    /// <summary>The façade of the current life, created on first use.</summary>
    internal Transaction Facade => Public ??= new Transaction(this);

    /// <summary>Takes the after state at the start of cleanup (Yjs <c>transaction.afterState = getStateVector(store)</c>).</summary>
    internal void TakeAfterState() => _afterCount = Snapshot(ref _after);

    /// <summary>Ends the life: its façade and every collection it returned go stale, and the state is cleared for reuse.</summary>
    internal void End()
    {
        Public?.Invalidate();
        Public = null;
        Origin = null;
        DeleteRanges.Clear();
        if (_changed is { Count: > 0 } changed)
        {
            foreach (var set in changed.Values)
            {
                set.Clear();
                _keySets.Push(set);
            }

            changed.Clear();
        }

        _changedParentTypes = null;
        ChangedOrder.Clear();
        ObserversCalled = false;
        SplitSnapshots = null;
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
