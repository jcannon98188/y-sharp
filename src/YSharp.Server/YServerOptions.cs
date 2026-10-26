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

    /// <summary>
    /// The semantic validation hook for a document, or null for none (ADR 0024 SRV-13, SRV-14). It is asked once, when the document is opened.
    /// A document with a hook holds a hot <see cref="Doc"/> while it is open; one without is served from the relay state alone and never
    /// materializes a <see cref="Doc"/>.
    /// </summary>
    public Func<string, ValidationHook?>? Validation { get; init; }

    /// <summary>How long a hook may run before its update is rejected. Default one second.</summary>
    public TimeSpan ValidationDeadline { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// After this many rejected updates a connection is closed with <see cref="ConnectionCloseReason.TooManyRejects"/>; 0 never closes it.
    /// A rejected edit stays in the client's document and is resent on every sync, so a client that does not discard it is rejected again
    /// (SRV-16). Default 3.
    /// </summary>
    public int MaxRejectsBeforeClose { get; init; } = 3;

    /// <summary>Messages a document's mailbox holds before <see cref="YConnection.ReceiveAsync"/> waits for room. Default 1024.</summary>
    public int MailboxCapacity { get; init; } = 1024;

    /// <summary>
    /// Messages queued for one peer before it counts as slow and is closed with <see cref="ConnectionCloseReason.SlowSubscriber"/>. The mailbox
    /// never waits for a peer. Default 256.
    /// </summary>
    public int SubscriberQueueCapacity { get; init; } = 256;
}
