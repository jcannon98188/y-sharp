using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace YSharp.Server;

/// <summary>
/// The server tier's entry point (ADR 0024; TASK-5.6): one mailbox per open document, created on the first connection and drained on shutdown.
/// It is transport-neutral: the host accepts sockets, authenticates them, and calls <see cref="ConnectAsync"/> for each, then moves bytes between
/// its socket and the returned <see cref="YConnection"/>.
/// </summary>
public sealed class YServer : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DocumentMailbox> _documents = new(StringComparer.Ordinal);
    private bool _disposed;

    public YServer(YServerOptions? options = null)
    {
        Options = options ?? new YServerOptions();
        Log = new UpdateLog(Options.Store, Options.Limits);
    }

    public YServerOptions Options { get; }

    internal UpdateLog Log { get; }

    /// <summary>
    /// Joins a peer to <paramref name="documentId"/>, loading the document from the store if no one has it open. The peer is sent the server's
    /// Step1 first, as y-websocket does.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The server is shut down.</exception>
    public async ValueTask<YConnection> ConnectAsync(string documentId, ConnectionContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        while (true)
        {
            // a mailbox whose document failed to load stops taking messages and is forgotten: the next Open loads the document again
            var mailbox = Open(documentId);
            var connection = new YConnection(mailbox, context ?? ConnectionContext.Anonymous, Options.SubscriberQueueCapacity);
            if (await mailbox.PostAsync(new Envelope(EnvelopeKind.Connect, connection, null), cancellationToken).ConfigureAwait(false))
                return connection;
        }
    }

    private DocumentMailbox Open(string documentId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_documents.TryGetValue(documentId, out var mailbox))
                _documents[documentId] = mailbox = new DocumentMailbox(this, documentId, Options);
            return mailbox;
        }
    }

    /// <summary>Drops a mailbox whose document failed to load, so the next connection tries again.</summary>
    internal void Forget(DocumentMailbox mailbox)
    {
        lock (_gate)
        {
            if (_documents.TryGetValue(mailbox.DocumentId, out var current) && ReferenceEquals(current, mailbox)) _documents.Remove(mailbox.DocumentId);
        }

        mailbox.Complete();
    }

    /// <summary>The mailbox of an open document, for tests.</summary>
    internal DocumentMailbox? Mailbox(string documentId)
    {
        lock (_gate) return _documents.TryGetValue(documentId, out var mailbox) ? mailbox : null;
    }

    /// <summary>
    /// Shuts down: stops taking messages, lets every mailbox handle the messages it already accepted (persisting and broadcasting their updates),
    /// then closes every connection with <see cref="ConnectionCloseReason.ServerShutdown"/> (SRV-9).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        DocumentMailbox[] mailboxes;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            mailboxes = new DocumentMailbox[_documents.Count];
            _documents.Values.CopyTo(mailboxes, 0);
            _documents.Clear();
        }

        var workers = new Task[mailboxes.Length];
        for (var i = 0; i < mailboxes.Length; i++)
        {
            mailboxes[i].Complete();
            workers[i] = mailboxes[i].Worker;
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }
}
