using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace YSharp.Server;

/// <summary>Why a connection was closed.</summary>
public enum ConnectionCloseReason
{
    None,

    /// <summary>The host disposed the connection.</summary>
    Disposed,

    /// <summary>Its outgoing queue was full: the peer reads too slowly (ADR 0024 SRV-9, TASK-5.6 backpressure).</summary>
    SlowSubscriber,

    /// <summary>It sent a message that does not parse, or an update that fails structural validation.</summary>
    ProtocolError,

    /// <summary>Its updates were rejected <see cref="YServerOptions.MaxRejectsBeforeClose"/> times (SRV-16).</summary>
    TooManyRejects,

    /// <summary>The store failed to load the document or to persist one of the connection's updates; the update was not acknowledged.</summary>
    StoreFailure,

    /// <summary>The server shut down after draining the document's mailbox.</summary>
    ServerShutdown,

    /// <summary>The host deleted the document with <see cref="YServer.DeleteAsync"/>, after its mailbox handled what it had accepted.</summary>
    DocumentDeleted,
}

/// <summary>
/// One peer on one document. The host's transport hands each received message to <see cref="ReceiveAsync"/> and sends what it reads from
/// <see cref="Outgoing"/>; both carry y-websocket messages (<c>varUint(type) + payload</c>: 0 sync, 1 awareness, 2 auth). When
/// <see cref="Outgoing"/> completes, the connection is closed and <see cref="CloseReason"/> says why; the transport then closes its socket.
/// </summary>
public sealed class YConnection : IAsyncDisposable
{
    private readonly Channel<ReadOnlyMemory<byte>> _outgoing;
    private readonly DocumentMailbox _mailbox;
    private int _closeReason;

    internal YConnection(DocumentMailbox mailbox, ConnectionContext context, int queueCapacity)
    {
        _mailbox = mailbox;
        Context = context;
        _outgoing = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,   // the worker writes; a dispose on another thread completes
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public string DocumentId => _mailbox.DocumentId;

    public ConnectionContext Context { get; }

    /// <summary>
    /// Messages for the peer, in order. A broadcast update is the same buffer for every peer: the transport must not change it. The queue is
    /// bounded by <see cref="YServerOptions.SubscriberQueueCapacity"/>; a peer that lets it fill is closed with
    /// <see cref="ConnectionCloseReason.SlowSubscriber"/>, so it never holds up the document.
    /// </summary>
    public ChannelReader<ReadOnlyMemory<byte>> Outgoing => _outgoing.Reader;

    public bool IsClosed => CloseReason != ConnectionCloseReason.None;

    public ConnectionCloseReason CloseReason => (ConnectionCloseReason)Volatile.Read(ref _closeReason);

    /// <summary>What went wrong, for a close that has more to say than its reason.</summary>
    public string? CloseDetail { get; private set; }

    /// <summary>Updates rejected so far (SRV-16). Read on the mailbox worker.</summary>
    internal int Rejects;

    /// <summary>
    /// Posts a message from the peer to the document's mailbox. It waits while the mailbox is full; once it returns true, the message is accepted:
    /// it will be handled, also across shutdown. Returns false when the connection or the server is closed and the message was not taken. The
    /// bytes are copied, so the caller may reuse its buffer at once.
    /// </summary>
    public ValueTask<bool> ReceiveAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        if (IsClosed) return ValueTask.FromResult(false);
        return _mailbox.PostAsync(new Envelope(EnvelopeKind.Message, this, message.ToArray()), cancellationToken);
    }

    /// <summary>Queues a message for the peer without waiting; false when the queue is full or closed. Called on the mailbox worker only.</summary>
    internal bool TrySend(ReadOnlyMemory<byte> message) => _outgoing.Writer.TryWrite(message);

    /// <summary>Closes the connection once, completing <see cref="Outgoing"/>. Returns false when it was already closed.</summary>
    internal bool Close(ConnectionCloseReason reason, string? detail = null)
    {
        if (Interlocked.CompareExchange(ref _closeReason, (int)reason, 0) != 0) return false;
        CloseDetail = detail;
        _outgoing.Writer.TryComplete();
        return true;
    }

    /// <summary>Leaves the document. Messages accepted before this are still handled.</summary>
    public async ValueTask DisposeAsync()
    {
        if (IsClosed) return;
        await _mailbox.PostAsync(new Envelope(EnvelopeKind.Disconnect, this, null), CancellationToken.None).ConfigureAwait(false);
        Close(ConnectionCloseReason.Disposed);
    }
}
