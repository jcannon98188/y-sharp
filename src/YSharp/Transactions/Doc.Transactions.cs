#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed partial class Doc
{
    // ---- transactions (CC-1..CC-8, CC-15, CC-16)
    public void Transact(Action<Transaction> body, object? origin = null) => throw Sketch.NI;
    public void Transact<TState>(TState state, Action<TState, Transaction> body, object? origin = null) => throw Sketch.NI;   // pass a static lambda: no closure allocation
    public TResult Transact<TResult>(Func<Transaction, TResult> body, object? origin = null) => throw Sketch.NI;
    public TResult Transact<TState, TResult>(TState state, Func<TState, Transaction, TResult> body, object? origin = null) => throw Sketch.NI;   // state-passing, no closure allocation   // JS transact returns the callback value (A4)
    public void TransactWithTimeout(Action<Transaction> body, object? origin, TimeSpan timeout, CancellationToken ct = default) => throw Sketch.NI;   // not an overload of Transact: a null origin would bind to the state-passing form
    /// Never blocks. Called by a thread that holds a read transaction it returns UpgradeRefused immediately (CC-3); Transact and BeginTransaction throw YConcurrencyException there.
    public TryTransactResult TryTransact(Action<Transaction> body, object? origin = null) => throw Sketch.NI;
    public void ReadTransact(Action<Transaction> body) => throw Sketch.NI;
    public TransactionScope BeginTransaction(object? origin = null) => throw Sketch.NI;
    public TransactionScope BeginReadTransaction() => throw Sketch.NI;

}
