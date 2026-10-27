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

    /// <summary>The text position a plain insert or delete reuses (<see cref="TypeText.Position.Rent"/>); only the lock holder touches it.</summary>
    internal TypeText.Position? SpareTextPosition;

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
    /// <summary>
    /// True once a violated internal invariant faulted the document, for good (ADR 0005, ERR-20). A faulted document stays readable and refuses
    /// changes: mutators and ApplyUpdate throw <see cref="YInternalException"/>, TryApplyUpdate returns Rejected. Recover by applying its
    /// EncodeStateAsUpdate to a fresh document.
    /// </summary>
    public bool IsFaulted => Volatile.Read(ref _faulted);

    /// <summary>A new client id: from the injected generator (fixtures pin ids), else a random 32-bit id as in Yjs (<c>random.uint32()</c>).</summary>
    internal ulong GenerateClientId() => _options.ClientIdGenerator?.Next() ?? (ulong)Random.Shared.NextInt64(0, 1L << 32);
}
