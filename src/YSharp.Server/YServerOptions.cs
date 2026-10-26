using System;
using System.Threading;

namespace YSharp.Server;

/// <summary>
/// Settings for the server tier. <see cref="Store"/> is the one place a host registers its storage backend (ADR 0024 SRV-12).
/// </summary>
public sealed class YServerOptions
{
    private IUpdateStore? _store;

    /// <summary>
    /// The storage backend. When the host sets none, the server uses a new <see cref="InMemoryUpdateStore"/>.
    /// <para>
    /// <b>The in-memory default is not durable: a restart loses every document.</b> A host that must keep documents across restarts sets a
    /// durable <see cref="IUpdateStore"/> of its own here, and can check it with the <c>YSharp.Server.Testing</c> conformance suite.
    /// </para>
    /// </summary>
    public IUpdateStore Store
    {
        get
        {
            if (_store is null) Interlocked.CompareExchange(ref _store, new InMemoryUpdateStore(), null);
            return _store;
        }

        init => _store = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>True when the host registered no store and <see cref="Store"/> is the non-durable in-memory default.</summary>
    public bool UsesDefaultStore => _store is null || _store is InMemoryUpdateStore;

    /// <summary>The limits every incoming update is validated against.</summary>
    public YLimits Limits { get; init; } = YLimits.Default;
}
