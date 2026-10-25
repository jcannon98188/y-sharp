#nullable enable
using System;
using System.Threading;
using YSharp.Store;
using YSharp.Transactions;

namespace YSharp;

public sealed partial class Doc : IDisposable
{
    private readonly DocOptions _options;     // immutable: options are read lock-free from it (CC-10)
    private readonly DocLock _lock;
    private ulong _clientId;
    private bool _gc;

    public Doc(DocOptions? options = null)
    {
        _options = options ?? new DocOptions();
        _lock = new DocLock(this);
        _gc = _options.Gc;
        _clientId = _options.ClientId ?? GenerateClientId();
    }

    /// <summary>The document's structs (spec 11). Read and written only under the document lock.</summary>
    internal StructStore Store { get; } = new();

    internal DocOptions Options => _options;

    public ulong ClientId
    {
        get => Volatile.Read(ref _clientId);
        set => Volatile.Write(ref _clientId, value);
    }

    public bool Gc
    {
        get => Volatile.Read(ref _gc);
        set => Volatile.Write(ref _gc, value);
    }

    public YLimits Limits => _options.Limits;
    public bool IsFaulted => throw Sketch.NI;   // ADR 0005: readable, refuses changes. Mutators and ApplyUpdate throw YInternalException, TryApplyUpdate returns Rejected

    /// <summary>A new client id: from the injected generator (fixtures pin ids), else a random 32-bit id as in Yjs (<c>random.uint32()</c>).</summary>
    internal ulong GenerateClientId() => _options.ClientIdGenerator?.Next() ?? (ulong)Random.Shared.NextInt64(0, 1L << 32);
}
