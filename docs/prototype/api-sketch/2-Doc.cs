#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public delegate void UpdateHandler(ReadOnlySpan<byte> update, object? origin, Doc doc, Transaction tx);   // span: zero-alloc, valid during the call only
public delegate void TransactionHandler(Transaction tx, Doc doc);
public delegate void SubdocsHandler(SubdocsChange change, Doc doc, Transaction tx);
public delegate void ObserverFaultHandler(Exception fault, object handlerOwner);

public readonly struct SubdocsChange { public IReadOnlyList<Doc> Added { get; } public IReadOnlyList<Doc> Removed { get; } public IReadOnlyList<Doc> Loaded { get; } }

public interface IClientIdGenerator { uint Next(); }   // seam: fixtures pin client IDs

public sealed record DocOptions
{
    public bool Gc { get; init; } = true;
    public Func<ItemView, bool>? GcFilter { get; init; }
    public string? Guid { get; init; }
    public string? CollectionId { get; init; }
    public YValue Meta { get; init; }
    public bool AutoLoad { get; init; }
    public bool ShouldLoad { get; init; } = true;
    public uint? ClientId { get; init; }
    public IClientIdGenerator? ClientIdGenerator { get; init; }
    public YLimits Limits { get; init; } = YLimits.Default;
    public TimeProvider? TimeProvider { get; init; }
}

public readonly struct ItemView { public ItemId Id => throw Sketch.NI; public bool IsDeleted => throw Sketch.NI; }

public sealed class Doc : IDisposable
{
    public Doc(DocOptions? options = null) { }

    public uint ClientId { get; set; }
    public string Guid => throw Sketch.NI;
    public bool Gc { get; set; }
    public YLimits Limits => throw Sketch.NI;
    public bool IsLoaded => throw Sketch.NI;
    public bool IsSynced => throw Sketch.NI;
    public bool IsDestroyed => throw Sketch.NI;
    public Doc? ParentDoc => throw Sketch.NI;
    public IReadOnlyCollection<Doc> Subdocs => throw Sketch.NI;
    public bool IsFaulted => throw Sketch.NI;   // ADR 0005: readable, refuses changes

    // ---- root types. Get(name) returns an untyped placeholder that a later typed Get upgrades.
    public YArray GetArray(string name = "") => throw Sketch.NI;
    public YMap GetMap(string name = "") => throw Sketch.NI;
    public YText GetText(string name = "") => throw Sketch.NI;
    public YXmlFragment GetXmlFragment(string name = "") => throw Sketch.NI;
    public YXmlElement GetXmlElement(string name = "") => throw Sketch.NI;   // nodeName "UNDEFINED" (X-E8)
    public YType Get(string name) => throw Sketch.NI;
    public IReadOnlyDictionary<string, YType> Share => throw Sketch.NI;       // snapshot
    public YValue ToJson() => throw Sketch.NI;                                // deprecated in JS, kept for DOC-07

    // ---- transactions (CC-1..CC-8, CC-15, CC-16)
    public void Transact(Action<Transaction> body, object? origin = null) => throw Sketch.NI;
    public void Transact<TState>(TState state, Action<TState, Transaction> body, object? origin = null) => throw Sketch.NI;   // pass a static lambda: no closure allocation
    public void Transact(Action<Transaction> body, object? origin, TimeSpan timeout, CancellationToken ct = default) => throw Sketch.NI;
    public TryTransactResult TryTransact(Action<Transaction> body, object? origin = null) => throw Sketch.NI;
    public void ReadTransact(Action<Transaction> body) => throw Sketch.NI;
    public TransactionScope BeginTransaction(object? origin = null) => throw Sketch.NI;
    public TransactionScope BeginReadTransaction() => throw Sketch.NI;

    // ---- updates (ADR 0005 pair: Try* never throws on bad bytes)
    public ApplyResult TryApplyUpdate(ReadOnlySpan<byte> update, object? origin = null, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
    public void ApplyUpdate(ReadOnlySpan<byte> update, object? origin = null, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;   // throws YDecodeException
    public void EncodeStateAsUpdate(IBufferWriter<byte> writer, ReadOnlySpan<byte> targetStateVector = default, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
    public byte[] EncodeStateAsUpdate(ReadOnlySpan<byte> targetStateVector = default, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
    public void EncodeStateVector(IBufferWriter<byte> writer) => throw Sketch.NI;
    public StateVector GetStateVector() => throw Sketch.NI;

    // ---- events: registration order, snapshot dispatch (CC-12, CC-13)
    public event TransactionHandler? BeforeAllTransactions, BeforeTransaction, BeforeObserverCalls, AfterTransaction, AfterTransactionCleanup, AfterAllTransactions;
    public event UpdateHandler? Update, UpdateV2;            // encoded only while subscribed and content changed (DOC-10)
    public event SubdocsHandler? SubdocsChanged;
    public event Action<Doc>? Loaded, Synced, Destroying, Destroyed;
    public event ObserverFaultHandler? ObserverFaulted;      // ADR 0005: handler exceptions are reported here, not thrown

    public void Load() => throw Sketch.NI;
    public void Destroy() => throw Sketch.NI;                 // a write transaction (CC-17)
    public void Dispose() => Destroy();
}

/// Stateless update tools (no Doc needed).
public static class YUpdate
{
    public static YStatus TryMerge(IReadOnlyList<ReadOnlyMemory<byte>> updates, IBufferWriter<byte> output, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
    public static YStatus TryDiff(ReadOnlySpan<byte> update, ReadOnlySpan<byte> stateVector, IBufferWriter<byte> output, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
    public static YStatus TryStateVectorFromUpdate(ReadOnlySpan<byte> update, IBufferWriter<byte> output, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
    public static YStatus TryConvert(ReadOnlySpan<byte> update, UpdateFormat from, UpdateFormat to, IBufferWriter<byte> output) => throw Sketch.NI;
    public static YStatus TryValidate(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1, YLimits? limits = null) => throw Sketch.NI;
}
