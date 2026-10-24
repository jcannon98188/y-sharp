#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- transactions (ADR 0006)

public sealed class Transaction
{
    public Doc Doc => throw Sketch.NI;
    public object? Origin => throw Sketch.NI;
    public bool IsLocal => throw Sketch.NI;
    public bool IsWrite => throw Sketch.NI;
    public StateVector BeforeState => throw Sketch.NI;
    public StateVector AfterState => throw Sketch.NI;
    public DeleteSet DeleteSet => throw Sketch.NI;
    public IReadOnlyCollection<YType> ChangedTypes => throw Sketch.NI;
    public IReadOnlyCollection<YType> ChangedParentTypes => throw Sketch.NI;
    public bool IsValid => throw Sketch.NI;       // false once the transaction ended (CC-9): the object is pooled. Every other member, and the collections it returned, throw YLifetimeException then
}

/// using-form of a transaction. ref struct so it cannot cross await; thread-affine (CC-15).
public ref struct TransactionScope
{
    public Transaction Transaction => throw Sketch.NI;
    public void Dispose() => throw Sketch.NI;
}

