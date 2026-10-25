#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using YSharp.Transactions;

namespace YSharp;

/// <summary>A thread's entry into a transaction: what to undo when it leaves (<c>Doc.Leave</c>).</summary>
internal readonly struct TxHandle
{
    internal TxHandle(TxState state, LockEntry entry, bool initial)
    {
        State = state;
        Entry = entry;
        Initial = initial;
    }

    /// <summary>The pooled state of the transaction the caller runs in: its own, or the one it joined.</summary>
    internal TxState State { get; }

    /// <summary>
    /// The transaction's public object. A read made by the library itself (encoding, reading the state vector) never asks for it, so it allocates
    /// nothing (BUD-06); a public read hands it to the caller, and its first use creates it (BUD-02).
    /// </summary>
    internal Transaction Tx => State.Facade;

    internal LockEntry Entry { get; }

    /// <summary>True when this entry created <see cref="Tx"/> (Yjs <c>initialCall</c>), so leaving ends it.</summary>
    internal bool Initial { get; }
}

public sealed partial class Doc
{
    // ---- transactions (CC-1..CC-8, CC-15, CC-16), Yjs transact and cleanupTransactions

    private Transaction? _tx;                       // the open write transaction (Yjs doc._transaction), null during cleanup
    private Transaction? _cleaning;                 // the transaction whose cleanup is running
    private List<Transaction> _cleanups = new();    // Yjs doc._transactionCleanups
    private List<Transaction>? _spareCleanups = new();
    private TxState[] _writePool = new TxState[2];
    private int _writePoolCount;

    public void Transact(Action<Transaction> body, object? origin = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        TransactCore(body, static (b, tx) =>
        {
            b(tx);
            return 0;
        }, origin, Timeout.Infinite, default);
    }

    public void Transact<TState>(TState state, Action<TState, Transaction> body, object? origin = null)   // pass a static lambda: no closure allocation
    {
        ArgumentNullException.ThrowIfNull(body);
        var h = EnterWrite(origin, local: true);
        try
        {
            body(state, h.Tx);
        }
        finally
        {
            Leave(h);
        }
    }

    public TResult Transact<TResult>(Func<Transaction, TResult> body, object? origin = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        return TransactCore(body, static (b, tx) => b(tx), origin, Timeout.Infinite, default);
    }

    public TResult Transact<TState, TResult>(TState state, Func<TState, Transaction, TResult> body, object? origin = null)   // state-passing, no closure allocation   // JS transact returns the callback value (A4)
    {
        ArgumentNullException.ThrowIfNull(body);
        return TransactCore(state, body, origin, Timeout.Infinite, default);
    }

    public void TransactWithTimeout(Action<Transaction> body, object? origin, TimeSpan timeout, CancellationToken ct = default)   // not an overload of Transact: a null origin would bind to the state-passing form
    {
        ArgumentNullException.ThrowIfNull(body);
        TransactCore(body, static (b, tx) =>
        {
            b(tx);
            return 0;
        }, origin, ToMilliseconds(timeout), ct);
    }

    /// Never blocks. Called by a thread that holds a read transaction it returns UpgradeRefused immediately (CC-3); Transact and BeginTransaction throw YConcurrencyException there.
    public TryTransactResult TryTransact(Action<Transaction> body, object? origin = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        var status = _lock.TryEnterWrite(out var entry);
        if (status != TryTransactStatus.Acquired) return new TryTransactResult(status);
        var h = BeginWrite(entry, origin, local: true);
        try
        {
            body(h.Tx);
        }
        finally
        {
            Leave(h);
        }

        return new TryTransactResult(TryTransactStatus.Acquired);
    }

    public void ReadTransact(Action<Transaction> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var h = EnterRead();
        try
        {
            body(h.Tx);
        }
        finally
        {
            Leave(h);
        }
    }

    public TransactionScope BeginTransaction(object? origin = null)
    {
        var h = EnterWrite(origin, local: true);
        return new TransactionScope(this, h.Tx, h.Entry, h.Initial);
    }

    public TransactionScope BeginReadTransaction()
    {
        var h = EnterRead();
        return new TransactionScope(this, h.Tx, h.Entry, h.Initial);
    }

    // ------------------------------------------------------------------ the internal surface the mutators and readers use (CC-8)

    /// <summary>
    /// The calling thread's open transaction: its write transaction (the one being cleaned up while observers run), else its read
    /// transaction, else null. Another thread's transaction is never returned (CC-2).
    /// </summary>
    internal Transaction? CurrentTransaction => _lock.IsWriteOwner ? _tx ?? _cleaning : _lock.CurrentRead;

    /// <summary>
    /// Enters a write transaction the way every mutator does (CC-8): joins the calling thread's open write transaction, opens a follow-up
    /// transaction when called from cleanup (CC-2), or takes the lock and opens one. Leave with <c>Leave</c> in a finally block.
    /// <paramref name="local"/> is false for a remote update (Yjs <c>transact(doc, f, origin, false)</c>).
    /// </summary>
    internal TxHandle EnterWrite(object? origin, bool local, int timeoutMs = Timeout.Infinite, CancellationToken ct = default)
    {
        var entry = _lock.EnterWrite(timeoutMs, ct);
        return BeginWrite(entry, origin, local);
    }

    /// <summary>Enters a read transaction the way every reader does (CC-8): joins the calling thread's read or write transaction, or takes a shared read lock.</summary>
    internal TxHandle EnterRead()
    {
        var entry = _lock.EnterRead(out var state);
        if (entry == LockEntry.ReadInWrite)
            state = (_tx ?? _cleaning ?? throw new YInternalException("The write owner has no transaction to read through.")).State;
        return new TxHandle(state!, entry, false);
    }

    internal void Leave(in TxHandle handle)
    {
        // a read entry ends without touching the transaction's public object, so a read nobody looked at stays allocation-free
        if (handle.Entry is LockEntry.ReadInWrite or LockEntry.ReadFresh or LockEntry.ReadJoined) Leave(null!, handle.Entry, handle.Initial);
        else Leave(handle.Tx, handle.Entry, handle.Initial);
    }

    internal void Leave(Transaction tx, LockEntry entry, bool initial)
    {
        switch (entry)
        {
            case LockEntry.ReadInWrite:
                return;
            case LockEntry.ReadFresh:
            case LockEntry.ReadJoined:
                _lock.ExitRead();
                return;
        }

        if (!_lock.IsWriteOwner) throw new YConcurrencyException("A write transaction was ended on a thread that does not hold it (CC-15).");
        try
        {
            if (initial)
            {
                var finishCleanup = _cleanups.Count > 0 && ReferenceEquals(_cleanups[0], tx);
                _tx = null;
                if (finishCleanup) Cleanup();
            }
        }
        finally
        {
            _lock.ExitWrite();
        }
    }

    // ------------------------------------------------------------------ lifecycle

    private TResult TransactCore<TState, TResult>(TState state, Func<TState, Transaction, TResult> body, object? origin, int timeoutMs, CancellationToken ct)
    {
        var h = EnterWrite(origin, local: true, timeoutMs, ct);
        try
        {
            return body(state, h.Tx);
        }
        finally
        {
            Leave(h);
        }
    }

    private TxHandle BeginWrite(LockEntry entry, object? origin, bool local)
    {
        if (_tx != null) return new TxHandle(_tx.State, entry, false);

        // the lock was free, or the owner writes from cleanup (an observer): a new transaction, cleaned up after the current one (CC-2)
        var tx = RentWrite().Begin(origin, local);
        _tx = tx;
        _cleanups.Add(tx);
        try
        {
            if (_cleanups.Count == 1) HookBeforeAllTransactions();
            HookBeforeTransaction(tx);
        }
        catch
        {
            Leave(tx, entry, initial: true);
            throw;
        }

        return new TxHandle(tx.State, entry, true);
    }

    /// <summary>
    /// Yjs <c>cleanupTransactions</c>: cleans up every transaction of the round in order, including follow-up transactions that observers open
    /// on the way, then raises the after-all hook. Every transaction is cleaned up even when one throws; the first exception is rethrown
    /// afterwards (Yjs <c>callAll</c> and its <c>finally</c> blocks).
    /// </summary>
    private void Cleanup()
    {
        var cleanups = _cleanups;
        var swapped = false;
        ExceptionDispatchInfo? first = null;
        try
        {
            for (var i = 0; i < cleanups.Count; i++)
            {
                _cleaning = cleanups[i];
                try
                {
                    CleanupOne(cleanups[i]);
                }
                catch (Exception e)
                {
                    first ??= ExceptionDispatchInfo.Capture(e);
                }
            }

            // a transaction opened from the after-all hook starts a new round, as in Yjs (doc._transactionCleanups = [] before the emit)
            _cleanups = _spareCleanups ?? new List<Transaction>();
            _spareCleanups = null;
            swapped = true;
            _cleaning = cleanups[^1];
            try
            {
                HookAfterAllTransactions(cleanups);
            }
            catch (Exception e)
            {
                first ??= ExceptionDispatchInfo.Capture(e);
            }
        }
        finally
        {
            _cleaning = null;
            foreach (var tx in cleanups)
            {
                var state = tx.State;
                state.End();
                ReturnWrite(state);
            }

            cleanups.Clear();
            if (swapped) _spareCleanups ??= cleanups;
        }

        first?.Throw();
    }

    /// <summary>
    /// Yjs <c>AbstractType._callObserver</c>: a remote change integrates items without moving the search markers, so each changed type of a
    /// non-local transaction forgets them (an update applied inside a local transaction makes it non-local, as Yjs <c>readUpdateV2</c> does).
    /// Done before any hook, so a beforeObserverCalls handler already reads through fresh markers.
    /// </summary>
    private static void ClearStaleMarkers(TxState state)
    {
        if (state.IsLocal || state.ChangedOrNull is not { } changed) return;
        foreach (var type in changed.Keys) type.Branch.Markers?.Clear();
    }

    private void CleanupOne(Transaction tx)
    {
        try
        {
            tx.State.TakeAfterState();
            ClearStaleMarkers(tx.State);
            HookBeforeObservers(tx);
            HookObservers(tx);
            HookAfterTransaction(tx);
        }
        finally
        {
            HookGarbageCollection(tx);
            // a remote update that wrote with our client id: someone else uses it, so pick a new one (Yjs cleanupTransactions)
            var client = ClientId;
            if (!tx.IsLocal && tx.AfterClock(client) != tx.BeforeClock(client)) ClientId = GenerateClientId();
            HookAfterTransactionCleanup(tx);
            HookSubdocs(tx);
        }
    }

    private TxState RentWrite() => _writePoolCount > 0 ? _writePool[--_writePoolCount] : new TxState(this, write: true);

    private void ReturnWrite(TxState state)
    {
        if (_writePoolCount == _writePool.Length) Array.Resize(ref _writePool, _writePool.Length * 2);
        _writePool[_writePoolCount++] = state;
    }

    private static int ToMilliseconds(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan) return Timeout.Infinite;
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be non-negative or infinite.");
        return (int)Math.Min(Math.Ceiling(timeout.TotalMilliseconds), int.MaxValue - 1);
    }
}
