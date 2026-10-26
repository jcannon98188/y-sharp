using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace YSharp.Server;

/// <summary>
/// The server tier's entry point (ADR 0024; TASK-5.6): one mailbox per open document, created on the first connection and drained on shutdown.
/// It is transport-neutral: the host accepts sockets, authenticates them, and calls <see cref="ConnectAsync"/> for each, then moves bytes between
/// its socket and the returned <see cref="YConnection"/>. The host edits and reads a document through <see cref="EditAsync"/>,
/// <see cref="ReadAsync{T}"/> and <see cref="SubscribeAsync"/>, which run on the document's mailbox (TASK-5.8). A document with no connections,
/// subscribers or queued work is closed after <see cref="YServerOptions.DocumentIdleTimeout"/> and loaded from the store again on next use.
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
            // a mailbox whose document failed to load, or that was evicted, stops taking messages: the next Open loads the document again
            var mailbox = Open(documentId);
            var connection = new YConnection(mailbox, context ?? ConnectionContext.Anonymous, Options.SubscriberQueueCapacity);
            if (await PostAttachedAsync(mailbox, new Envelope(EnvelopeKind.Connect, connection, null), cancellationToken).ConfigureAwait(false))
                return connection;
        }
    }

    /// <summary>
    /// Runs <paramref name="edit"/> in a write transaction on the document's mailbox, loading the document if no one has it open (ADR 0024 SRV-5,
    /// SRV-18). Its update is persisted and broadcast to every peer as an accepted peer update is, without the validation hook. The task completes
    /// once the update is persisted and sent; it fails with the edit's own exception or the store's, and then nothing is stored or sent. The token
    /// cancels only the wait for room in the mailbox.
    /// </summary>
    /// <returns>False when the edit changed nothing.</returns>
    public async ValueTask<bool> EditAsync(string documentId, Action<Transaction> edit, object? origin = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return await RunAsync(documentId, m => m.EditAsync(edit, origin), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="read"/> in a read transaction on the document's mailbox, after every message accepted before it, loading the document
    /// if no one has it open (SRV-5, SRV-18). The <see cref="Doc"/> must not be kept or written. The token cancels only the wait for room.
    /// </summary>
    public async ValueTask<T> ReadAsync<T>(string documentId, Func<Doc, T> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        return await RunAsync(documentId, m => ValueTask.FromResult(m.Read(read)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Subscribes an indexer to the document (SRV-18): <paramref name="changed"/> runs in a read transaction on the document's mailbox once now
    /// and again after every accepted update, from a peer or from <see cref="EditAsync"/>. Like an observer it must not block, keep the
    /// <see cref="Doc"/> or write to it; a throw is ignored. The subscription keeps the document open and its <see cref="Doc"/> in memory until
    /// it is disposed.
    /// </summary>
    public async ValueTask<IAsyncDisposable> SubscribeAsync(string documentId, Action<Doc> changed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return await RunAsync<IAsyncDisposable>(documentId, m => ValueTask.FromResult<IAsyncDisposable>(m.Subscribe(changed)), cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<T> RunAsync<T>(string documentId, Func<DocumentMailbox, ValueTask<T>> run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        var work = new MailboxWork<T>(run);
        while (!await PostAttachedAsync(Open(documentId), new Envelope(EnvelopeKind.Work, null, null, work), cancellationToken).ConfigureAwait(false))
        {
        }

        return await work.Task.ConfigureAwait(false);
    }

    /// <summary>Opens and attaches to a document (SRV-20): the mailbox is not evicted until what is attached leaves.</summary>
    private DocumentMailbox Open(string documentId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_documents.TryGetValue(documentId, out var mailbox))
                _documents[documentId] = mailbox = new DocumentMailbox(this, documentId, Options);
            mailbox.Attached++;
            return mailbox;
        }
    }

    /// <summary>Posts for an attached caller; when the post is not taken, the caller is detached again.</summary>
    private async ValueTask<bool> PostAttachedAsync(DocumentMailbox mailbox, Envelope envelope, CancellationToken cancellationToken)
    {
        var posted = false;
        try
        {
            posted = await mailbox.PostAsync(envelope, cancellationToken).ConfigureAwait(false);
            return posted;
        }
        finally
        {
            if (!posted) Detach(mailbox);
        }
    }

    internal void Attach(DocumentMailbox mailbox)
    {
        lock (_gate) mailbox.Attached++;
    }

    internal void Detach(DocumentMailbox mailbox)
    {
        lock (_gate) mailbox.Attached--;
    }

    /// <summary>
    /// SRV-20: closes an idle mailbox, unless something attached to it since it went idle. The next use opens a new mailbox, which loads the
    /// document from the store.
    /// </summary>
    internal bool TryEvict(DocumentMailbox mailbox)
    {
        lock (_gate)
        {
            if (_disposed || mailbox.Attached > 0) return false;
            if (_documents.TryGetValue(mailbox.DocumentId, out var current) && ReferenceEquals(current, mailbox)) _documents.Remove(mailbox.DocumentId);
        }

        mailbox.Complete();
        return true;
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
