#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using YSharp.Store;
using YSharp.Transactions;

namespace YSharp;

// ---------------------------------------------------------------- transactions (ADR 0006)

/// <summary>
/// One read or write transaction (Yjs <c>Transaction</c>). A small object made for each transaction over state the document pools: once the
/// transaction ends <see cref="IsValid"/> turns false for good, and every other member, and every collection it returned, throws
/// <see cref="YLifetimeException"/> (CC-9), even while a later transaction runs on the same pooled state.
/// </summary>
public sealed class Transaction
{
    private TxState? _state;   // the only field: null once the transaction ended

    internal Transaction(TxState state) => _state = state;

    public Doc Doc => State.Doc;

    public object? Origin => State.Origin;

    /// <summary>True for changes made on this document, false for a remote update being applied (Yjs <c>local</c>).</summary>
    public bool IsLocal => State.IsLocal;

    public bool IsWrite => State.IsWrite;

    /// <summary>The document's state vector when the transaction started.</summary>
    public StateVector BeforeState => State.BeforeState;

    /// <summary>The state vector when cleanup started; empty until then, as in Yjs.</summary>
    public StateVector AfterState => State.AfterState;

    /// <summary>A copy of what this transaction deleted so far.</summary>
    public DeleteSet DeleteSet
    {
        get
        {
            var ranges = State.DeleteRanges;
            var copy = new DeleteRanges();
            for (var i = 0; i < ranges.Count; i++)
            {
                var r = ranges[i];
                copy.Add(r.Client, r.Clock, r.Length);
            }

            return new DeleteSet(copy);
        }
    }

    public IReadOnlyCollection<YType> ChangedTypes
    {
        get
        {
            _ = State;
            return new LiveKeys(this, changedParents: false);
        }
    }

    public IReadOnlyCollection<YType> ChangedParentTypes
    {
        get
        {
            _ = State;
            return new LiveKeys(this, changedParents: true);
        }
    }

    /// <summary>False once the transaction ended (CC-9). The only member that never throws.</summary>
    public bool IsValid => _state != null;

    // ------------------------------------------------------------------ internal surface for the later modules

    /// <summary>The pooled state of this transaction; throws once it ended.</summary>
    internal TxState State => _state ?? throw new YLifetimeException();

    internal void Invalidate() => _state = null;

    /// <summary>What this transaction deleted (Yjs <c>transaction.deleteSet</c>).</summary>
    internal DeleteRanges DeleteRanges => State.DeleteRanges;

    /// <summary>The changed types and the keys changed on each, null standing for a list change (Yjs <c>transaction.changed</c>).</summary>
    internal Dictionary<YType, HashSet<string?>> Changed => State.Changed;

    /// <summary>Events per parent type for deep observers (Yjs <c>transaction.changedParentTypes</c>).</summary>
    internal Dictionary<YType, List<YEvent>> ChangedParentTypesMap => State.ChangedParentTypes;

    /// <summary>The clock <paramref name="client"/> had when the transaction started (Yjs <c>beforeState.get(client) || 0</c>).</summary>
    internal long BeforeClock(ulong client) => State.BeforeClock(client);

    /// <summary>The clock <paramref name="client"/> had when cleanup started, 0 before that.</summary>
    internal long AfterClock(ulong client) => State.AfterClock(client);

    /// <summary>A live view of the changed types that throws once the transaction that produced it ended (CC-9).</summary>
    private sealed class LiveKeys : IReadOnlyCollection<YType>
    {
        private readonly Transaction _tx;
        private readonly bool _changedParents;

        public LiveKeys(Transaction tx, bool changedParents)
        {
            _tx = tx;
            _changedParents = changedParents;
        }

        private ICollection<YType>? Keys
        {
            get
            {
                var state = _tx.State;
                return _changedParents ? state.ChangedParentTypesOrNull?.Keys : state.ChangedOrNull?.Keys;
            }
        }

        public int Count => Keys?.Count ?? 0;

        public IEnumerator<YType> GetEnumerator()
        {
            var keys = Keys;
            if (keys == null) yield break;
            foreach (var key in keys)
            {
                yield return key;
                _ = _tx.State;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
