using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Server.Tests.Mailbox;

/// <summary>
/// The per-document mailbox and shared broadcast (ADR 0024 SRV-4, SRV-7..SRV-9; TASK-5.6): one document's messages are handled in order while
/// documents run apart, an accepted update is persisted before anyone sees it and is handed to every peer as one buffer, a slow peer is closed
/// instead of holding the document up, and shutdown handles every accepted message first.
/// </summary>
[Requires(Feature.UpdateFunctions, Feature.V2, Feature.V2Functions, Feature.Sync, Feature.Awareness, Feature.Text, Feature.Map, Feature.Events)]
public sealed class MailboxTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<UpdateRecord>> Records(IUpdateStore store, string docId)
    {
        var list = new List<UpdateRecord>();
        await foreach (var r in store.ReadSinceAsync(docId, 0, Ct)) list.Add(r);
        return list;
    }

    private static byte[] ToV1(ReadOnlyMemory<byte> v2)
    {
        var w = new ArrayBufferWriter<byte>();
        Assert.True(YUpdate.TryConvert(v2.Span, UpdateFormat.V2, UpdateFormat.V1, w).Ok);
        return w.WrittenSpan.ToArray();
    }

    [Fact, Covers("SRV-4"), Covers("SRV-8")]
    public async Task OneDocumentsUpdates_AreStoredAndBroadcastInArrivalOrder()
    {
        var store = new InMemoryUpdateStore();
        await using var server = new YServer(new YServerOptions { Store = store });
        var a = await Peer.JoinAsync(server, "doc", 1);
        var b = await Peer.JoinAsync(server, "doc", 2);
        await a.FlushAsync();
        await b.FlushAsync();
        for (var i = 0; i < 40; i++) await a.InsertAsync(((char)('a' + i % 26)).ToString());
        await a.FlushAsync();
        await b.DrainAsync();

        Assert.Equal(a.Sent, b.Broadcasts);
        Assert.Equal(a.Sent, (await Records(store, "doc")).Select(r => ToV1(r.UpdateV2)));
        Assert.Equal(a.Text, b.Text);
        Assert.Empty(a.Broadcasts);   // the sender is not sent its own update back
    }

    [Fact, Covers("SRV-4"), Covers("SRV-9")]
    public async Task ADocumentWaitingOnTheStore_DoesNotHoldUpAnother()
    {
        var store = new GatedStore();
        await using var server = new YServer(new YServerOptions { Store = store });
        var a1 = await Peer.JoinAsync(server, "a", 1);
        var a2 = await Peer.JoinAsync(server, "a", 2);
        var b1 = await Peer.JoinAsync(server, "b", 3);
        var b2 = await Peer.JoinAsync(server, "b", 4);
        foreach (var p in new[] { a1, a2, b1, b2 }) await p.FlushAsync();

        store.Close("a");
        await a1.InsertAsync("first");
        await store.Waiting.WaitAsync(Ct);
        await a1.InsertAsync("second");   // accepted, and queued behind the stalled append

        for (var i = 0; i < 5; i++) await b1.InsertAsync("b" + i);
        await b1.FlushAsync();
        await b2.DrainAsync();
        Assert.Equal("b0b1b2b3b4", b2.Text);
        await a2.DrainAsync();
        Assert.Empty(a2.Broadcasts);   // durable before visible: nothing of "a" went out while its append waited

        store.Open("a");
        await a1.FlushAsync();
        await a2.DrainAsync();
        Assert.Equal(a1.Sent, a2.Broadcasts);
        Assert.Equal("firstsecond", a2.Text);
    }

    [Fact, Covers("SRV-7")]
    public async Task AnAcceptedUpdate_IsOneBufferHandedToEveryPeer()
    {
        await using var server = new YServer();
        var sender = await Peer.JoinAsync(server, "doc", 1);
        var peers = new List<Peer>();
        for (var i = 0; i < 4; i++) peers.Add(await Peer.JoinAsync(server, "doc", (ulong)(10 + i)));
        foreach (var p in peers.Append(sender)) await p.FlushAsync();

        await sender.InsertAsync("shared");
        await sender.FlushAsync();
        var segments = new List<ArraySegment<byte>>();
        foreach (var p in peers)
        {
            await p.DrainAsync();
            Assert.True(MemoryMarshal.TryGetArray(p.Received[^1], out var segment));
            segments.Add(segment);
            Assert.Equal("shared", p.Text);
        }

        Assert.All(segments, s => Assert.Same(segments[0].Array, s.Array));
        Assert.All(segments, s => Assert.Equal((segments[0].Offset, segments[0].Count), (s.Offset, s.Count)));
    }

    [Fact, Covers("SRV-9")]
    public async Task APeerThatDoesNotRead_IsClosed_AndTheOthersGetEveryUpdate()
    {
        await using var server = new YServer(new YServerOptions { SubscriberQueueCapacity = 4 });
        var sender = await Peer.JoinAsync(server, "doc", 1);
        var reader = await Peer.JoinAsync(server, "doc", 2);
        var slow = await Peer.JoinAsync(server, "doc", 3);   // never reads
        await sender.FlushAsync();
        await reader.FlushAsync();

        for (var i = 0; i < 12; i++)
        {
            await sender.InsertAsync(i.ToString());
            await sender.FlushAsync();
            await reader.DrainAsync();
        }

        Assert.Equal(ConnectionCloseReason.SlowSubscriber, slow.Connection.CloseReason);
        Assert.Equal(sender.Sent, reader.Broadcasts);
        Assert.Equal(sender.Text, reader.Text);
        Assert.False(await slow.Connection.ReceiveAsync(new byte[] { 0, 0, 1, 0 }, Ct));

        // the peer can still read what was queued before it was closed, then sees the end of the stream
        var queued = 0;
        await foreach (var _ in slow.Connection.Outgoing.ReadAllAsync(Ct)) queued++;
        Assert.Equal(4, queued);
    }

    [Fact, Covers("SRV-9")]
    public async Task AFullMailbox_MakesTheSenderWait_AndACancelledPostIsNotAccepted()
    {
        var store = new GatedStore();
        await using var server = new YServer(new YServerOptions { Store = store, MailboxCapacity = 1 });
        var a = await Peer.JoinAsync(server, "doc", 1);
        var b = await Peer.JoinAsync(server, "doc", 2);
        await a.FlushAsync();
        await b.FlushAsync();

        store.Close("doc");
        await a.InsertAsync("x");
        await store.Waiting.WaitAsync(Ct);
        await a.InsertAsync("y");   // fills the mailbox
        a.Doc.GetText("text").Insert(0, "z");
        var dropped = Frame.Update(a.Doc.EncodeStateAsUpdate());
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var post = a.Connection.ReceiveAsync(dropped, cancel.Token);
        Assert.False(post.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await post);

        store.Open("doc");
        await b.FlushAsync();
        await b.DrainAsync();
        Assert.Equal("xy", b.Text);
        Assert.Equal(2, (await Records(store.Inner, "doc")).Count);
    }

    [Fact, Covers("SRV-9")]
    public async Task Shutdown_HandlesEveryAcceptedUpdate_ThenClosesTheConnections()
    {
        var store = new GatedStore();
        var server = new YServer(new YServerOptions { Store = store });
        var a = await Peer.JoinAsync(server, "doc", 1);
        var b = await Peer.JoinAsync(server, "doc", 2);
        await a.FlushAsync();
        await b.FlushAsync();

        store.Close("doc");
        for (var i = 0; i < 30; i++) await a.InsertAsync(i.ToString());
        await store.Waiting.WaitAsync(Ct);
        var shutdown = server.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);   // it waits for the stalled append and everything behind it
        Assert.False(await a.Connection.ReceiveAsync(Frame.Update(new byte[] { 0, 0 }), Ct));
        store.Open("doc");
        await shutdown;

        Assert.Equal(30, (await Records(store.Inner, "doc")).Count);
        await b.DrainAsync();
        Assert.Equal(a.Sent, b.Broadcasts);
        Assert.Equal(ConnectionCloseReason.ServerShutdown, a.Connection.CloseReason);
        Assert.Equal(ConnectionCloseReason.ServerShutdown, b.Connection.CloseReason);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await server.ConnectAsync("doc", cancellationToken: Ct));

        // a new server over the same store has every accepted update
        await using var restarted = new YServer(new YServerOptions { Store = store });
        var c = await Peer.JoinAsync(restarted, "doc", 3);
        await c.FlushAsync();
        Assert.Equal(a.Text, c.Text);
    }

    [Fact, Covers("SRV-8")]
    public async Task AFailedPersist_IsNotAppliedOrBroadcast_AndClosesOnlyTheSender()
    {
        var store = new GatedStore();
        await using var server = new YServer(new YServerOptions { Store = store });
        var a = await Peer.JoinAsync(server, "doc", 1);
        var b = await Peer.JoinAsync(server, "doc", 2);
        await a.FlushAsync();
        await b.FlushAsync();

        store.Fail("doc");
        await a.InsertAsync("lost");
        await b.FlushAsync();
        store.Fail("doc", false);
        await b.InsertAsync("kept");
        await b.FlushAsync();

        Assert.Equal(ConnectionCloseReason.StoreFailure, a.Connection.CloseReason);
        Assert.Equal("disk full", a.Connection.CloseDetail);
        var c = await Peer.JoinAsync(server, "doc", 3);
        await c.FlushAsync();
        Assert.Equal("kept", c.Text);
        Assert.Single(await Records(store.Inner, "doc"));
    }

    [Theory, Covers("SRV-8")]
    [InlineData(new byte[] { 0, 2, 3, 1, 2, 3 })]   // an update that does not decode
    [InlineData(new byte[] { 0, 2, 9, 1 })]         // a payload cut short
    [InlineData(new byte[] { 0, 7, 0 })]            // no such sync message
    [InlineData(new byte[] { 0, 0, 2, 1, 1 })]      // a malformed state vector
    public async Task AMessageThatDoesNotParse_ClosesTheSender_AndChangesNothing(byte[] message)
    {
        var store = new InMemoryUpdateStore();
        await using var server = new YServer(new YServerOptions { Store = store });
        var a = await Peer.JoinAsync(server, "doc", 1);
        var b = await Peer.JoinAsync(server, "doc", 2);
        await a.FlushAsync();
        await b.FlushAsync();

        await a.SendAsync(message);
        await b.FlushAsync();
        Assert.Equal(ConnectionCloseReason.ProtocolError, a.Connection.CloseReason);
        Assert.Empty(await Records(store, "doc"));
        Assert.Empty(b.Broadcasts);
    }

    [Fact, Covers("SRV-19")]
    public async Task PeersWithContent_ConvergeThroughTheServer_AndANewPeerLoadsItFromTheStore()
    {
        var store = new InMemoryUpdateStore();
        await using (var server = new YServer(new YServerOptions { Store = store }))
        {
            var a = await Peer.JoinAsync(server, "doc", 1, before: d => d.GetText("text").Insert(0, "hello "));
            var b = await Peer.JoinAsync(server, "doc", 2, before: d => d.GetMap("map").Set("k", "v"));
            await a.FlushAsync();
            await b.FlushAsync();
            await a.FlushAsync();
            Assert.Equal(a.Doc.EncodeStateAsUpdate(), b.Doc.EncodeStateAsUpdate());
            await b.InsertAsync("world");
            await b.FlushAsync();
        }

        await using var restarted = new YServer(new YServerOptions { Store = store });
        var c = await Peer.JoinAsync(restarted, "doc", 3);
        await c.FlushAsync();
        Assert.Equal("hello world", c.Text);
        Assert.True(c.Doc.GetMap("map").TryGet("k", out var v));
        Assert.Equal("v", v.Value.AsString());
    }

    [Fact, Covers("SRV-19")]
    public async Task ADocumentThatFailsToLoad_ClosesItsConnections_AndIsTriedAgainNextTime()
    {
        var store = new InMemoryUpdateStore();
        await store.AppendAsync("doc", new byte[] { 1, 2, 3 }, Ct);
        await using var server = new YServer(new YServerOptions { Store = store });
        var broken = await server.ConnectAsync("doc", cancellationToken: Ct);
        await foreach (var _ in broken.Outgoing.ReadAllAsync(Ct)) { }
        Assert.Equal(ConnectionCloseReason.StoreFailure, broken.CloseReason);

        await store.DeleteAsync("doc", Ct);
        var a = await Peer.JoinAsync(server, "doc", 1);
        await a.InsertAsync("fresh");
        await a.FlushAsync();
        Assert.Equal(ConnectionCloseReason.None, a.Connection.CloseReason);
    }

    [Fact, Covers("SRV-17")]
    public async Task AwarenessIsRelayedToTheOtherPeers_AsIs()
    {
        await using var server = new YServer();
        var a = await Peer.JoinAsync(server, "doc", 1);
        var b = await Peer.JoinAsync(server, "doc", 2, new ConnectionContext { IsReadOnly = true });
        await a.FlushAsync();
        await b.FlushAsync();

        var presence = new byte[] { 1, 5, 1, 2, 0, 1, 0 };
        await b.SendAsync(presence);
        await a.FlushAsync();
        Assert.Equal(presence, Assert.Single(a.Awareness));
        await b.DrainAsync();
        Assert.Empty(b.Awareness);
    }
}
