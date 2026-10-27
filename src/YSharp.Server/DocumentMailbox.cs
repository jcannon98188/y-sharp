using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace YSharp.Server;

internal enum EnvelopeKind : byte { Connect, Message, Disconnect, Work }

internal readonly record struct Envelope(EnvelopeKind Kind, YConnection? Connection, byte[]? Message, MailboxWork? Work = null);

/// <summary>
/// Work a host posts to a document's mailbox: a server-side edit or read, or a subscriber joining or leaving (ADR 0024 SRV-5, SRV-18; TASK-5.8).
/// It runs on the worker between messages. <see cref="Attached"/> work was posted through <see cref="YServer"/>'s open path and keeps the
/// document from being evicted until it has run.
/// </summary>
internal abstract class MailboxWork
{
    public bool Attached { get; init; } = true;

    public abstract ValueTask RunAsync(DocumentMailbox mailbox);

    public abstract void Fail(Exception error);
}

internal sealed class MailboxWork<T>(Func<DocumentMailbox, ValueTask<T>> run) : MailboxWork
{
    private readonly TaskCompletionSource<T> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<T> Task => _done.Task;

    public override async ValueTask RunAsync(DocumentMailbox mailbox)
    {
        try
        {
            _done.TrySetResult(await run(mailbox).ConfigureAwait(false));
        }
        catch (Exception e)
        {
            _done.TrySetException(e);
        }
    }

    public override void Fail(Exception error) => _done.TrySetException(error);
}

/// <summary>A host's subscription to a document's changes (SRV-18): it holds the hot Doc, and the document open, until it is disposed.</summary>
internal sealed class DocumentSubscription(DocumentMailbox mailbox, Action<Doc> changed) : IAsyncDisposable
{
    private int _disposed;

    public Action<Doc> Changed => changed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var leave = new MailboxWork<bool>(m => ValueTask.FromResult(m.Unsubscribe(this))) { Attached = false };
        await mailbox.PostAsync(new Envelope(EnvelopeKind.Work, null, null, leave), CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>
/// One document's mailbox (ADR 0024 SRV-4..SRV-9, SRV-13..SRV-19; TASK-5.6): a bounded single-reader channel drained by one worker loop, which
/// alone touches the document's relay, its hot <see cref="Doc"/> and its connections. Every message is handled in arrival order; an incoming
/// update goes through structural validation, the hook (when one is registered), the relay and hot Doc checks, persist, apply and broadcast,
/// in that order. A host's edits, reads and subscriptions run on the same worker (SRV-5, SRV-18), and an idle document drops its hot Doc and
/// then itself (SRV-20; TASK-5.8). The document's presence lives in an <see cref="Awareness"/> of its own, kept as y-websocket keeps it
/// (TASK-17.3).
/// </summary>
internal sealed class DocumentMailbox
{
    private const byte MessageSync = 0;
    private const byte MessageAwareness = 1;
    private const byte MessageAuth = 2;

    private static readonly byte[] EmptyUpdateV1 = { 0, 0 };

    private readonly YServer _server;
    private readonly YServerOptions _options;
    private readonly ValidationHook? _hook;
    private readonly Channel<Envelope> _inbox;
    private readonly List<YConnection> _connections = new();
    private readonly List<DocumentSubscription> _subscribers = new();
    private readonly TimeProvider _time;
    private readonly Awareness _awareness;
    private readonly Dictionary<YConnection, HashSet<ulong>> _controlled = new();   // the awareness clients each connection set
    private readonly List<ulong> _departed = new();                                  // clients of closed connections, removed after the message
    private YRelay _relay = null!;
    private YLimits? _peerLimits;
    private Exception? _loadFailure;
    private long _lastActivity;
    private bool _closed;

    public DocumentMailbox(YServer server, string documentId, YServerOptions options)
    {
        _server = server;
        _options = options;
        DocumentId = documentId;
        _hook = options.Validation?.Invoke(documentId);
        _time = options.TimeProvider;
        _inbox = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(options.MailboxCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        // the server holds no presence of its own (y-websocket: setLocalState(null)), so the awareness only keeps the peers' states
        _awareness = new Awareness(new Doc(new DocOptions { Limits = options.Limits }), _time);
        _awareness.SetLocalState(YValue.Null);
        _awareness.Update += OnAwarenessUpdate;
        Worker = Task.Run(RunAsync);
    }

    public string DocumentId { get; }

    public Task Worker { get; }

    /// <summary>
    /// The hot document, held while a hook, a host's edit or read, or a subscriber needs it, until the document is idle (SRV-13, SRV-18, SRV-20).
    /// Read on the worker, or after it stops.
    /// </summary>
    internal Doc? HotDocument { get; private set; }

    /// <summary>Connections, posted work and subscriptions that hold the document open; guarded by the server's lock (SRV-20).</summary>
    internal int Attached;

    /// <summary>Why the remaining connections close once the mailbox is complete and drained: set before <see cref="Complete"/>.</summary>
    internal ConnectionCloseReason CloseReason = ConnectionCloseReason.ServerShutdown;

    /// <summary>The relay state. Read on the worker, or after it stops.</summary>
    internal YRelay Relay => _relay;

    /// <summary>Enqueues a message; false when the mailbox is closed for shutdown. Waits while it is full (inbound backpressure).</summary>
    public async ValueTask<bool> PostAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            await _inbox.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    /// <summary>Stops taking messages; the worker handles every message already accepted, then closes the remaining connections.</summary>
    public void Complete() => _inbox.Writer.TryComplete();

    private async Task RunAsync()
    {
        try
        {
            await LoadAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // the document cannot be served from this mailbox; the server forgets it so the next connection loads it again
            _loadFailure = e;
            _closed = true;
            _server.Forget(this);
        }

        var reader = _inbox.Reader;
        _lastActivity = _time.GetTimestamp();
        while (true)
        {
            while (reader.TryRead(out var envelope))
            {
                try
                {
                    await HandleAsync(envelope).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // a bug or a fault outside the message's control: the sender is closed, and the document goes on for everyone else
                    if (envelope.Connection is { } sender) Close(sender, ConnectionCloseReason.ProtocolError, e.Message);
                }

                RemoveDepartedAwareness();
                _lastActivity = _time.GetTimestamp();
            }

            var idle = IdleWait();
            var more = idle is null ? await reader.WaitToReadAsync().ConfigureAwait(false) : await WaitToReadAsync(reader, idle.Value).ConfigureAwait(false);
            if (more is false) break;
            if (more is null) await EvictAsync().ConfigureAwait(false);
        }

        foreach (var connection in _connections) connection.Close(CloseReason);
        _connections.Clear();
        _awareness.Update -= OnAwarenessUpdate;
        _awareness.Dispose();
    }

    /// <summary>True when there is something to read, false when the mailbox is complete and drained, null when <paramref name="timeout"/> passed.</summary>
    private async ValueTask<bool?> WaitToReadAsync(ChannelReader<Envelope> reader, TimeSpan timeout)
    {
        using var stop = new CancellationTokenSource();
        var read = reader.WaitToReadAsync(stop.Token).AsTask();
        var first = await Task.WhenAny(read, Task.Delay(timeout, _time, stop.Token)).ConfigureAwait(false);
        stop.Cancel();
        return ReferenceEquals(first, read) ? await read.ConfigureAwait(false) : null;
    }

    /// <summary>
    /// How long until the next idle eviction is due, or null when none can be (SRV-20): the document is idle while it has no connections and no
    /// subscribers, and the time counts from the last message or work handled.
    /// </summary>
    private TimeSpan? IdleWait()
    {
        if (_closed || _connections.Count > 0 || _subscribers.Count > 0) return null;
        var idle = _time.GetElapsedTime(_lastActivity);
        TimeSpan? due = null;
        if (HotDocument is not null && _options.HotDocumentIdleTimeout != Timeout.InfiniteTimeSpan) due = _options.HotDocumentIdleTimeout - idle;
        if (_options.DocumentIdleTimeout != Timeout.InfiniteTimeSpan)
        {
            var close = _options.DocumentIdleTimeout - idle;
            if (due is null || close < due) due = close;
        }

        return due < TimeSpan.Zero ? TimeSpan.Zero : due;
    }

    /// <summary>
    /// SRV-20: drops the hot Doc, then the whole document, once each has been idle long enough. The relay and the log stay authoritative, so the
    /// Doc is rebuilt from the relay when next needed, and the document from the store when next opened. The server refuses to evict a document
    /// that something attached to meanwhile; the idle time then starts again. The host's unloading hook runs first (SRV-23).
    /// </summary>
    private async ValueTask EvictAsync()
    {
        var idle = _time.GetElapsedTime(_lastActivity);
        if (HotDocument is not null && _options.HotDocumentIdleTimeout != Timeout.InfiniteTimeSpan && idle >= _options.HotDocumentIdleTimeout)
            HotDocument = null;
        if (_options.DocumentIdleTimeout == Timeout.InfiniteTimeSpan || idle < _options.DocumentIdleTimeout) return;
        if (_options.DocumentUnloading is { } unloading) await UnloadingAsync(unloading).ConfigureAwait(false);
        if (_server.TryEvict(this)) _closed = true;
        else _lastActivity = _time.GetTimestamp();
    }

    /// <summary>
    /// SRV-23: the host's hook gets the document's state before it is unloaded. It is awaited no longer than its deadline, and a hook that fails
    /// or overruns does not keep the document loaded: the store holds every accepted update.
    /// </summary>
    private async ValueTask UnloadingAsync(Func<string, ReadOnlyMemory<byte>, CancellationToken, ValueTask> hook)
    {
        var limit = _options.DocumentUnloadingDeadline;
        using var deadline = new CancellationTokenSource(limit, _time);
        try
        {
            await hook(DocumentId, _relay.EncodeStateAsUpdate(), deadline.Token).AsTask().WaitAsync(limit, _time).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // failed, cancelled or late: the document is unloaded all the same, and a hook still running is left to finish on its own
        }
    }

    /// <summary>
    /// Rehydration (SRV-19): the snapshot, then the records after it, in sequence order, into the relay and, when a hook is registered, a hot
    /// <see cref="Doc"/>. Messages that arrive meanwhile wait in the mailbox.
    /// </summary>
    private async Task LoadAsync()
    {
        var log = _server.Log;
        _relay = new YRelay(log.StoredLimits);
        var doc = _hook is null ? null : new Doc(new DocOptions { Limits = log.StoredLimits });
        await log.FoldAsync(DocumentId, 0, _relay, doc, this, CancellationToken.None).ConfigureAwait(false);
        HotDocument = doc;
    }

    private async ValueTask HandleAsync(Envelope envelope)
    {
        if (envelope.Kind == EnvelopeKind.Work)
        {
            var work = envelope.Work!;
            try
            {
                if (_loadFailure is not null) work.Fail(_loadFailure);
                else await work.RunAsync(this).ConfigureAwait(false);
            }
            finally
            {
                if (work.Attached) _server.Detach(this);
            }

            return;
        }

        var connection = envelope.Connection!;
        switch (envelope.Kind)
        {
            case EnvelopeKind.Connect:
                if (_loadFailure is not null)
                {
                    connection.Close(ConnectionCloseReason.StoreFailure, _loadFailure.Message);
                    _server.Detach(this);
                    return;
                }

                _connections.Add(connection);
                _controlled[connection] = new HashSet<ulong>();
                if (!Send(connection, Step1())) return;
                // y-websocket: every known state after the sync step 1, so a peer that joins late sees who is there
                var states = _awareness.GetStates();
                if (states.Count > 0) Send(connection, AwarenessMessage([.. states.Keys]));
                return;
            case EnvelopeKind.Disconnect:
                Remove(connection);
                return;
        }

        // a connection the server closed sends nothing more; one the host disposed still has the messages it posted before handled (TASK-5.8)
        if ((connection.IsClosed && connection.CloseReason != ConnectionCloseReason.Disposed) || envelope.Message is not { Length: > 0 } message) return;
        switch (message[0])
        {
            case MessageSync:
                await HandleSyncAsync(connection, message).ConfigureAwait(false);
                return;
            case MessageAwareness:
            {
                // applied to the document's awareness, whose update handler sends what changed to every connection (TASK-17.3). Awareness needs
                // no document, and a read-only connection may still share its presence (SRV-17)
                var error = TryReadAwareness(message, out var start, out var length)
                    ? _awareness.TryApplyUpdate(message.AsSpan(start, length), connection).Error
                    : YError.Malformed;
                if (error != YError.None) Close(connection, ConnectionCloseReason.ProtocolError, $"awareness: {error}");
                return;
            }
            default:
                return;   // auth and query-awareness from a peer, and unknown types, are ignored
        }
    }

    private async ValueTask HandleSyncAsync(YConnection connection, byte[] message)
    {
        ReadOnlySpan<byte> input = message.AsSpan(1);
        var status = YSync.TryReadFrame(ref input, out var type, out var payload);
        if (!status.Ok)
        {
            Close(connection, ConnectionCloseReason.ProtocolError, $"sync message: {status.Error}");
            return;
        }

        if (type == SyncMessageType.Step1)
        {
            var sv = payload.ToArray();
            var update = new ArrayBufferWriter<byte>();
            status = StateVector.TryDecode(sv, out _);
            if (status.Ok) status = _relay.TryEncodeStateAsUpdate(update, sv);
            if (!status.Ok)
            {
                Close(connection, ConnectionCloseReason.ProtocolError, $"state vector: {status.Error}");
                return;
            }

            Send(connection, SyncMessage(w => YSync.WriteStep2(w, update.WrittenSpan)));
            return;
        }

        // Step2 and Update carry an update; a read-only connection's are dropped here, with no Doc (SRV-17)
        if (connection.Context.IsReadOnly) return;
        var start = message.Length - input.Length - payload.Length;
        await AcceptAsync(connection, type, message.AsMemory(start, payload.Length)).ConfigureAwait(false);
    }

    /// <summary>
    /// What a peer's update is decoded under: the server's limits, with the string and nesting bounds of the relay and the hot Doc where those
    /// are tighter, so the one decode checks what decoding it for each of them checked.
    /// </summary>
    private YLimits PeerLimits => _peerLimits ??= _options.Limits with
    {
        MaxStringBytes = Math.Min(_options.Limits.MaxStringBytes, _relay.Limits.MaxStringBytes),
        MaxAnyDepth = Math.Min(_options.Limits.MaxAnyDepth, _relay.Limits.MaxAnyDepth),
    };

    /// <summary>
    /// SRV-8: structural validation, the hook, then the relay and the hot Doc are checked before the append, so an update that either refuses
    /// is never persisted (RLY-7, TASK-5.3): the relay prepares the merged state without changing, and the hot Doc applies. Persist, then the
    /// relay commits and the update is broadcast. When the hot Doc changes and the update does not go on (a partly rejected update, or a failed
    /// append), the hot Doc is rebuilt from the relay, which still holds the state before the update.
    /// </summary>
    private async ValueTask AcceptAsync(YConnection connection, SyncMessageType type, ReadOnlyMemory<byte> update)
    {
        if (update.Span.SequenceEqual(EmptyUpdateV1)) return;   // an up-to-date peer's Step2: nothing to store or send

        // decoded once (TASK-14): the hook, the relay and the hot Doc take the decoded update, and the stored record is written from it
        var status = YUpdate.TryDecode(update.Span, out var decoded, UpdateFormat.V1, PeerLimits);
        if (!status.Ok)
        {
            Close(connection, ConnectionCloseReason.ProtocolError, $"update: {status.Error}");
            return;
        }

        if (_hook is not null)
        {
            var verdict = Validate(connection, update, decoded);
            if (!verdict.Accepted)
            {
                Reject(connection, verdict.Reason!, resync: type == SyncMessageType.Update);
                return;
            }
        }

        var v2 = new ArrayBufferWriter<byte>(update.Length + 16);
        decoded.Encode(v2, UpdateFormat.V2);
        if (!SurvivesV2(decoded))
        {
            // the relay and the hot Doc take what is stored, as a restart reads it back
            status = YUpdate.TryDecode(v2.WrittenSpan, out decoded, UpdateFormat.V2, PeerLimits);
            if (!status.Ok)
            {
                Close(connection, ConnectionCloseReason.ProtocolError, $"update: {status.Error}");
                return;
            }
        }

        // the relay's pending bound (RLY-7) and the hot Doc's (ADR 0005) are checked here, before anything is stored
        status = _relay.TryPrepareUpdate(decoded, out var prepared);
        if (!status.Ok)
        {
            Close(connection, ConnectionCloseReason.ProtocolError, $"update: {status.Error}");
            return;
        }

        if (HotDocument is { } doc)
        {
            var applied = doc.TryApplyUpdate(decoded, connection);
            if (!applied.Ok)
            {
                // Malformed and LimitExceeded change nothing; Rejected applied the structs that could be integrated
                if (applied.Error == YError.Rejected) RebuildHotDocument();
                Close(connection, ConnectionCloseReason.ProtocolError, $"update: {applied.Error}");
                return;
            }
        }

        try
        {
            await _server.Log.Store.AppendAsync(DocumentId, v2.WrittenMemory).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // not acknowledged: nothing is served or sent, and the peer resends when it reconnects (SRV-8)
            if (HotDocument is not null) RebuildHotDocument();
            Close(connection, ConnectionCloseReason.StoreFailure, e.Message);
            return;
        }

        _relay.Commit(prepared!);
        Broadcast(SyncMessage(w => YSync.WriteUpdate(w, update.Span)), except: connection);
        NotifySubscribers();
    }

    /// <summary>
    /// A host's edit (SRV-5, SRV-18; TASK-5.8): it runs in a write transaction on the hot Doc, materialized from the relay if need be, and the
    /// update it makes goes through the relay check, persist, commit and broadcast as an accepted peer update does. It is the server's own edit,
    /// so the validation hook does not see it. When the edit throws or its update does not go on, the hot Doc is rebuilt from the relay and the
    /// caller gets the error; an edit that changes nothing stores and sends nothing.
    /// </summary>
    internal async ValueTask<bool> EditAsync(Action<Transaction> edit, object? origin)
    {
        var doc = EnsureHotDocument();
        byte[]? update = null;
        UpdateHandler capture = (u, _, _, _) => update = u.ToArray();
        doc.Update += capture;
        try
        {
            doc.Transact(edit, origin ?? this);
        }
        catch (Exception)
        {
            RebuildHotDocument();
            throw;
        }
        finally
        {
            doc.Update -= capture;
        }

        if (update is null) return false;
        // decoded once, under the relay's limits: the relay prepares it and the stored record is written from it (TASK-14)
        var status = YUpdate.TryDecode(update, out var decoded, UpdateFormat.V1, _relay.Limits);
        YRelay.PreparedUpdate? prepared = null;
        if (status.Ok) status = _relay.TryPrepareUpdate(decoded, out prepared);
        if (!status.Ok)
        {
            RebuildHotDocument();
            throw new InvalidOperationException($"Document '{DocumentId}': the edit's update was refused ({status.Error}).");
        }

        var v2 = new ArrayBufferWriter<byte>(update.Length + 16);
        decoded.Encode(v2, UpdateFormat.V2);
        try
        {
            await _server.Log.Store.AppendAsync(DocumentId, v2.WrittenMemory).ConfigureAwait(false);
        }
        catch (Exception)
        {
            RebuildHotDocument();
            throw;
        }

        _relay.Commit(prepared!);
        Broadcast(SyncMessage(w => YSync.WriteUpdate(w, update)), except: null);
        NotifySubscribers();
        return true;
    }

    /// <summary>A host's read (SRV-5, SRV-18): one read transaction on the hot Doc, materialized from the relay if need be, on the worker.</summary>
    internal T Read<T>(Func<Doc, T> read)
    {
        var doc = EnsureHotDocument();
        var result = default(T)!;
        doc.ReadTransact(_ => result = read(doc));
        return result;
    }

    /// <summary>An indexing subscriber joins (SRV-18): it holds the hot Doc and the document, and is called once now and after every accepted update.</summary>
    internal DocumentSubscription Subscribe(Action<Doc> changed)
    {
        EnsureHotDocument();
        var subscription = new DocumentSubscription(this, changed);
        _server.Attach(this);
        _subscribers.Add(subscription);
        Notify(subscription);
        return subscription;
    }

    internal bool Unsubscribe(DocumentSubscription subscription)
    {
        if (!_subscribers.Remove(subscription)) return false;
        _server.Detach(this);
        return true;
    }

    private void NotifySubscribers()
    {
        foreach (var subscription in _subscribers) Notify(subscription);
    }

    /// <summary>
    /// Calls a subscriber in a read transaction on the worker. Like an observer (SRV-7) it must not block, and it must not keep the Doc: a rebuild
    /// replaces it. A throw is the subscriber's own bug and does not stop the document.
    /// </summary>
    private void Notify(DocumentSubscription subscription)
    {
        var doc = HotDocument!;
        try
        {
            doc.ReadTransact(_ => subscription.Changed(doc));
        }
        catch (Exception)
        {
        }
    }

    private Doc EnsureHotDocument()
    {
        if (HotDocument is null) RebuildHotDocument();
        return HotDocument!;
    }

    /// <summary>A fresh hot Doc from the relay's state: on demand, or when the old one took an update that did not go on.</summary>
    private void RebuildHotDocument()
    {
        var doc = new Doc(new DocOptions { Limits = _server.Log.StoredLimits });
        doc.ApplyUpdate(_relay.EncodeStateAsUpdate(format: UpdateFormat.V2), this, UpdateFormat.V2);
        HotDocument = doc;
    }

    /// <summary>
    /// An awareness message's payload: one lib0 <c>varUint8Array</c>, which the awareness then decodes as a whole before it applies anything, so
    /// a malformed one closes the sender and reaches no one (TASK-5.3). Bytes after the payload are ignored, as y-protocols ignores them.
    /// </summary>
    private static bool TryReadAwareness(byte[] message, out int start, out int length)
    {
        start = length = 0;
        ulong value = 0;
        var shift = 0;
        var at = 1;
        while (true)
        {
            if (at >= message.Length || shift > 49) return false;   // truncated, or longer than a JS safe integer
            var b = message[at++];
            value |= (ulong)(b & 0x7f) << shift;
            shift += 7;
            if ((b & 0x80) == 0) break;
        }

        if (value > (ulong)(message.Length - at)) return false;
        start = at;
        length = (int)value;
        return true;
    }

    /// <summary>
    /// y-websocket's awareness update handler: a connection's update marks the clients it added as its own and forgets the ones it removed, and
    /// the server's own encoding of every changed client goes to every connection, the sender included. It runs on the worker, except for the
    /// removals of the awareness's outdated check, which come from its timer and are posted to the worker.
    /// </summary>
    private void OnAwarenessUpdate(AwarenessChange change, object? origin)
    {
        if (origin is YConnection connection && _controlled.TryGetValue(connection, out var ids))
        {
            foreach (var id in change.Added) ids.Add(id);
            foreach (var id in change.Updated) ids.Add(id);   // a client that removed its state and set it again is updated, not added
            foreach (var id in change.Removed) ids.Remove(id);
        }

        var message = AwarenessMessage([.. change.Added, .. change.Updated, .. change.Removed]);
        if (message.IsEmpty) return;
        if (origin is not "timeout")
        {
            Broadcast(message, except: null);
            return;
        }

        var send = new MailboxWork<bool>(m =>
        {
            m.Broadcast(message, except: null);
            return ValueTask.FromResult(true);
        }) { Attached = false };
        _ = PostAsync(new Envelope(EnvelopeKind.Work, null, null, send), CancellationToken.None);
    }

    /// <summary>The states of connections that closed during the last message, removed and the removal sent to the rest (y-websocket closeConn).</summary>
    private void RemoveDepartedAwareness()
    {
        while (_departed.Count > 0)
        {
            ulong[] clients = [.. _departed];
            _departed.Clear();
            _awareness.RemoveStates(clients, null);   // closing a slow peer while sending the removal departs it in turn
        }
    }

    /// <summary>An awareness message of the given clients' states, or empty when they cannot be encoded.</summary>
    private ReadOnlyMemory<byte> AwarenessMessage(ReadOnlySpan<ulong> clients)
    {
        var update = new ArrayBufferWriter<byte>();
        if (!_awareness.TryEncodeUpdate(update, clients).Ok) return ReadOnlyMemory<byte>.Empty;
        var w = new ArrayBufferWriter<byte>(update.WrittenCount + 6);
        Span<byte> header = stackalloc byte[11];
        header[0] = MessageAwareness;
        var n = 1;
        for (var v = (ulong)update.WrittenCount; ; v >>= 7)
        {
            header[n++] = (byte)(v < 0x80 ? v : (v & 0x7f) | 0x80);
            if (v < 0x80) break;
        }

        w.Write(header[..n]);
        w.Write(update.WrittenSpan);
        return w.WrittenMemory;
    }

    /// <summary>SRV-14: the hook in one read transaction, with a deadline; a throw or an overrun is a reject.</summary>
    /// <summary>
    /// Whether the update reads back from its V2 form as it was decoded. The writer groups a client's structs in read order and writes the
    /// first one's clock and then lengths, so a struct not starting where its client's previous one ends (a client listed twice, a zero-length
    /// GC going back) comes back at another clock: the relay skipped it live and a reload integrated it (the nightly HOS-MUT find of
    /// 2026-10-27, TASK-18). V2 writes a client's delete ranges as steps from the end of the one before, so ranges that overlap or go back
    /// within a client come back as other ranges (in Yjs as here). Both are checked per client in read order.
    /// </summary>
    private static bool SurvivesV2(DecodedUpdate decoded)
    {
        var ends = new Dictionary<ulong, long>();
        foreach (var s in decoded.Structs)
        {
            if (ends.TryGetValue(s.Id.Client, out var end) && s.Id.Clock != end) return false;
            ends[s.Id.Client] = s.Id.Clock + s.Length;
        }

        var deletes = decoded.Deletes;
        if (deletes.Count < 2) return true;
        ends.Clear();
        foreach (var d in deletes)
        {
            if (ends.TryGetValue(d.Client, out var end) && d.Clock < end) return false;
            ends[d.Client] = d.Clock + d.Length;
        }

        return true;
    }

    private ValidationResult Validate(YConnection connection, ReadOnlyMemory<byte> update, DecodedUpdate decoded)
    {
        var doc = EnsureHotDocument();
        using var deadline = new CancellationTokenSource(_options.ValidationDeadline);
        var request = new ValidationRequest(DocumentId, doc, update, decoded, connection.Context, deadline.Token);
        var verdict = default(ValidationResult);
        try
        {
            doc.ReadTransact(_ => verdict = _hook!(in request));
        }
        catch (Exception) when (!deadline.IsCancellationRequested)
        {
            return ValidationResult.Reject("validation failed");
        }
        catch (Exception)
        {
            // an OperationCanceledException from a hook that honoured its deadline lands here
        }

        if (deadline.IsCancellationRequested) return ValidationResult.Reject("validation timed out");
        return verdict.Accepted ? verdict : ValidationResult.Reject(verdict.Reason ?? "rejected");
    }

    /// <summary>
    /// SRV-16: drop the update, tell the sender why, and resync it from the server's state. A client answers a Step1 with a Step2 that carries
    /// the rejected edit again, so only a rejected <c>Update</c> is answered with Step1 and Step2; a rejected Step2, which is such an answer, gets
    /// the server's Step2 alone. Otherwise one rejected edit would bounce between the two forever when rejects never close the connection.
    /// </summary>
    private void Reject(YConnection connection, string reason, bool resync)
    {
        var denied = new ArrayBufferWriter<byte>();
        denied.Write(stackalloc byte[] { MessageAuth });
        YAuth.WritePermissionDenied(denied, reason);
        if (!Send(connection, denied.WrittenMemory)) return;
        if (resync && !Send(connection, Step1())) return;
        if (!Send(connection, SyncMessage(w => YSync.WriteStep2(w, _relay.EncodeStateAsUpdate())))) return;
        connection.Rejects++;
        if (_options.MaxRejectsBeforeClose > 0 && connection.Rejects >= _options.MaxRejectsBeforeClose)
            Close(connection, ConnectionCloseReason.TooManyRejects, reason);
    }

    /// <summary>The server's Step1: its relay's state vector.</summary>
    private ReadOnlyMemory<byte> Step1()
    {
        var sv = new ArrayBufferWriter<byte>();
        _relay.EncodeStateVector(sv);
        return SyncMessage(w => YSync.WriteStep1(w, sv.WrittenSpan));
    }

    /// <summary>One sync message, framed for y-websocket, in a buffer of its own.</summary>
    private static ReadOnlyMemory<byte> SyncMessage(Action<IBufferWriter<byte>> write)
    {
        var w = new ArrayBufferWriter<byte>();
        w.Write(stackalloc byte[] { MessageSync });
        write(w);
        return w.WrittenMemory;
    }

    /// <summary>
    /// Hands the same buffer to every other connection (TASK-5.6 #2). It never waits: a peer whose queue is full is closed (#3), so a slow
    /// subscriber cannot hold up the mailbox.
    /// </summary>
    private void Broadcast(ReadOnlyMemory<byte> message, YConnection? except)
    {
        for (var i = _connections.Count - 1; i >= 0; i--)
        {
            var c = _connections[i];
            if (!ReferenceEquals(c, except)) Send(c, message);
        }
    }

    private bool Send(YConnection connection, ReadOnlyMemory<byte> message)
    {
        if (connection.TrySend(message)) return true;
        Close(connection, ConnectionCloseReason.SlowSubscriber);
        return false;
    }

    private void Close(YConnection connection, ConnectionCloseReason reason, string? detail = null)
    {
        connection.Close(reason, detail);
        Remove(connection);
    }

    /// <summary>
    /// A connection leaves the document: it no longer holds it open (SRV-20), and the awareness states it set are removed once the message in
    /// hand is done, so the removal is not sent from inside another send.
    /// </summary>
    private void Remove(YConnection connection)
    {
        if (_controlled.Remove(connection, out var ids)) _departed.AddRange(ids);
        if (_connections.Remove(connection)) _server.Detach(this);
    }
}
