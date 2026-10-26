using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace YSharp.Server;

internal enum EnvelopeKind : byte { Connect, Message, Disconnect }

internal readonly record struct Envelope(EnvelopeKind Kind, YConnection Connection, byte[]? Message);

/// <summary>
/// One document's mailbox (ADR 0024 SRV-4..SRV-9, SRV-13..SRV-19; TASK-5.6): a bounded single-reader channel drained by one worker loop, which
/// alone touches the document's relay, its hot <see cref="Doc"/> and its connections. Every message is handled in arrival order; an incoming
/// update goes through structural validation, the hook (when one is registered), persist, apply and broadcast, in that order.
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
    private YRelay _relay = null!;
    private Exception? _loadFailure;

    public DocumentMailbox(YServer server, string documentId, YServerOptions options)
    {
        _server = server;
        _options = options;
        DocumentId = documentId;
        _hook = options.Validation?.Invoke(documentId);
        _inbox = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(options.MailboxCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        Worker = Task.Run(RunAsync);
    }

    public string DocumentId { get; }

    public Task Worker { get; }

    /// <summary>The hot document, held only while a hook is registered (SRV-13, SRV-18). Read on the worker, or after it stops.</summary>
    internal Doc? HotDocument { get; private set; }

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
            _server.Forget(this);
        }

        await foreach (var envelope in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await HandleAsync(envelope).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // a bug or a fault outside the message's control: the sender is closed, and the document goes on for everyone else
                Close(envelope.Connection, ConnectionCloseReason.ProtocolError, e.Message);
            }
        }

        foreach (var connection in _connections) connection.Close(ConnectionCloseReason.ServerShutdown);
        _connections.Clear();
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
        await foreach (var record in log.Store.ReadSinceAsync(DocumentId, 0).ConfigureAwait(false))
        {
            var status = _relay.TryApplyUpdate(record.UpdateV2.Span, UpdateFormat.V2);
            if (!status.Ok) throw new InvalidDataException($"Document '{DocumentId}': record {record.Sequence} is not a valid update ({status.Error}).");
            doc?.ApplyUpdate(record.UpdateV2.Span, this, UpdateFormat.V2);
        }

        HotDocument = doc;
    }

    private async ValueTask HandleAsync(Envelope envelope)
    {
        var connection = envelope.Connection;
        switch (envelope.Kind)
        {
            case EnvelopeKind.Connect:
                if (_loadFailure is not null)
                {
                    connection.Close(ConnectionCloseReason.StoreFailure, _loadFailure.Message);
                    return;
                }

                _connections.Add(connection);
                Send(connection, Step1());
                return;
            case EnvelopeKind.Disconnect:
                _connections.Remove(connection);
                return;
        }

        if (connection.IsClosed || envelope.Message is not { Length: > 0 } message) return;
        switch (message[0])
        {
            case MessageSync:
                await HandleSyncAsync(connection, message).ConfigureAwait(false);
                return;
            case MessageAwareness:
                // relayed as is: awareness needs no document, and a read-only connection may still share its presence (SRV-17)
                Broadcast(message, except: connection);
                return;
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

    /// <summary>SRV-8: structural validation, the hook, persist, apply to the hot Doc, broadcast.</summary>
    private async ValueTask AcceptAsync(YConnection connection, SyncMessageType type, ReadOnlyMemory<byte> update)
    {
        if (update.Span.SequenceEqual(EmptyUpdateV1)) return;   // an up-to-date peer's Step2: nothing to store or send

        var v2 = new ArrayBufferWriter<byte>(update.Length + 16);
        var status = YUpdate.TryConvert(update.Span, UpdateFormat.V1, UpdateFormat.V2, v2, _options.Limits);
        if (!status.Ok)
        {
            Close(connection, ConnectionCloseReason.ProtocolError, $"update: {status.Error}");
            return;
        }

        if (_hook is not null)
        {
            var verdict = Validate(connection, update);
            if (!verdict.Accepted)
            {
                Reject(connection, verdict.Reason!, resync: type == SyncMessageType.Update);
                return;
            }
        }

        try
        {
            await _server.Log.Store.AppendAsync(DocumentId, v2.WrittenMemory).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // not acknowledged: nothing was applied or sent, and the peer resends when it reconnects (SRV-8)
            Close(connection, ConnectionCloseReason.StoreFailure, e.Message);
            return;
        }

        status = _relay.TryApplyUpdate(v2.WrittenSpan, UpdateFormat.V2);
        if (!status.Ok) throw new InvalidOperationException($"A validated update did not fold into the relay ({status.Error}).");
        HotDocument?.ApplyUpdate(update.Span, connection);
        Broadcast(SyncMessage(w => YSync.WriteUpdate(w, update.Span)), except: connection);
    }

    /// <summary>SRV-14: the hook in one read transaction, with a deadline; a throw or an overrun is a reject.</summary>
    private ValidationResult Validate(YConnection connection, ReadOnlyMemory<byte> update)
    {
        var doc = HotDocument!;
        YUpdate.TryDecode(update.Span, out var decoded, UpdateFormat.V1, _options.Limits);
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
    private void Broadcast(ReadOnlyMemory<byte> message, YConnection except)
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
        _connections.Remove(connection);
    }
}
