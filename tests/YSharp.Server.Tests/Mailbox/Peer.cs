using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace YSharp.Server.Tests.Mailbox;

/// <summary>
/// A client as a y-websocket provider runs one, over a <see cref="YConnection"/> instead of a socket: it answers the server's Step1, sends its
/// local edits as sync updates, and applies what the server sends. <see cref="FlushAsync"/> is a barrier: it sends a Step1 and reads until the
/// Step2 answer, so every message posted before it has been handled by the document's mailbox.
/// </summary>
internal sealed class Peer : IAsyncDisposable
{
    private static readonly object Remote = new();
    private readonly List<byte[]> _localUpdates = new();

    private string _documentId = "";

    private Peer(Doc doc, YConnection connection)
    {
        Doc = doc;
        Connection = connection;
        doc.Update += (u, origin, _, _) =>
        {
            if (!ReferenceEquals(origin, Remote)) _localUpdates.Add(u.ToArray());
        };
    }

    public Doc Doc { get; }

    public YConnection Connection { get; private set; }

    /// <summary>Every message the server sent, in order, as the same memory it handed over.</summary>
    public List<ReadOnlyMemory<byte>> Received { get; } = new();

    /// <summary>The updates the server broadcast to this peer.</summary>
    public List<byte[]> Broadcasts { get; } = new();

    /// <summary>The updates this peer sent from local edits, in order.</summary>
    public List<byte[]> Sent { get; } = new();

    public List<string> Denied { get; } = new();

    public List<byte[]> Awareness { get; } = new();

    public int Step2Count { get; private set; }

    public string Text => Doc.GetText("text").ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Peer> JoinAsync(YServer server, string documentId, ulong clientId, ConnectionContext? context = null, Action<Doc>? before = null)
    {
        var doc = new Doc(new DocOptions { ClientId = clientId });
        before?.Invoke(doc);
        var peer = new Peer(doc, await server.ConnectAsync(documentId, context, Ct)) { _documentId = documentId };
        peer._localUpdates.Clear();
        return peer;
    }

    /// <summary>Joins again on a new connection, as a provider does after a drop; the server's Step1 brings the peer's offline edits up.</summary>
    public async Task ReconnectAsync(YServer server)
    {
        if (!Connection.IsClosed) await Connection.DisposeAsync();
        _localUpdates.Clear();
        Connection = await server.ConnectAsync(_documentId, Connection.Context, Ct);
    }

    /// <summary>A local edit made while offline: it is not sent now, and reaches the server in the Step2 of the next sync.</summary>
    public void EditOffline(Action<Doc> edit)
    {
        edit(Doc);
        _localUpdates.Clear();
    }

    /// <summary>Makes a local edit and sends the updates it produced.</summary>
    public async Task EditAsync(Action<Doc> edit)
    {
        edit(Doc);
        var updates = _localUpdates.ToArray();
        _localUpdates.Clear();
        foreach (var u in updates)
        {
            Sent.Add(u);
            await SendAsync(Frame.Update(u));
        }
    }

    public Task InsertAsync(string text) => EditAsync(d => d.GetText("text").Insert(d.GetText("text").Length, text));

    public async Task SendAsync(byte[] message) => Assert.True(await Connection.ReceiveAsync(message, Ct), "the message was not accepted");

    /// <summary>Handles what is queued without waiting; answers the server's Step1 with a Step2.</summary>
    public async Task DrainAsync()
    {
        while (Connection.Outgoing.TryRead(out var message)) await HandleAsync(message);
    }

    /// <summary>Sends a Step1 and handles messages until its Step2 answer: everything posted earlier has been handled by the mailbox.</summary>
    public async Task FlushAsync()
    {
        var before = Step2Count;
        var sv = new ArrayBufferWriter<byte>();
        Doc.EncodeStateVector(sv);
        await SendAsync(Frame.Sync(w => YSync.WriteStep1(w, sv.WrittenSpan)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (Step2Count == before)
        {
            if (!await Connection.Outgoing.WaitToReadAsync(timeout.Token)) throw new InvalidOperationException($"closed: {Connection.CloseReason} {Connection.CloseDetail}");
            await DrainAsync();
        }
    }

    private async Task HandleAsync(ReadOnlyMemory<byte> message)
    {
        Received.Add(message);
        var bytes = message.ToArray();
        switch (bytes[0])
        {
            case 0:
            {
                ReadOnlySpan<byte> frame = bytes.AsSpan(1);
                ReadOnlySpan<byte> peek = frame;
                Assert.True(YSync.TryReadFrame(ref peek, out var type, out var payload).Ok);
                if (type == SyncMessageType.Update) Broadcasts.Add(payload.ToArray());
                if (type == SyncMessageType.Step2) Step2Count++;
                var reply = new ArrayBufferWriter<byte>();
                Assert.True(YSync.TryReadMessage(ref frame, reply, Doc, Remote, out var result).Ok);
                if (result.ReplyWritten && !Connection.IsClosed)
                    await Connection.ReceiveAsync(Frame.Sync(w => w.Write(reply.WrittenSpan)), Ct);
                break;
            }
            case 1:
                Awareness.Add(bytes);
                break;
            case 2:
            {
                ReadOnlySpan<byte> auth = bytes.AsSpan(1);
                Assert.True(YAuth.TryReadMessage(ref auth, Doc, (_, reason) => Denied.Add(reason)).Ok);
                break;
            }
        }
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

internal static class Frame
{
    public static byte[] Sync(Action<IBufferWriter<byte>> write)
    {
        var w = new ArrayBufferWriter<byte>();
        w.Write(new byte[] { 0 });
        write(w);
        return w.WrittenSpan.ToArray();
    }

    public static byte[] Update(byte[] update) => Sync(w => YSync.WriteUpdate(w, update));
}
