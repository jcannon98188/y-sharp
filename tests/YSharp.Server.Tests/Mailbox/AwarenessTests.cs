using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Time;
using YSharp.TestKit.Traceability;

namespace YSharp.Server.Tests.Mailbox;

/// <summary>
/// A document's presence, kept as y-websocket bin/utils.js keeps it (SRV-22; TASK-17.3): the server applies each awareness update to an
/// awareness of its own and sends its encoding of what changed to every connection, the sender included; a connection that joins gets every
/// known state after the server's Step1; the states a connection set are removed, and the removal sent, when it closes or goes quiet.
/// </summary>
[Requires(Feature.Sync, Feature.Awareness, Feature.Text)]
public sealed class AwarenessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A peer's own awareness: the state it sets goes out as y-websocket's provider sends it.</summary>
    private static Awareness Presence(Peer peer, string name, TimeProvider? time = null)
    {
        var awareness = new Awareness(peer.Doc, time);
        awareness.SetLocalState(name);
        return awareness;
    }

    private static Task SendAsync(Peer peer, Awareness awareness, params ulong[] clients)
    {
        var update = new ArrayBufferWriter<byte>();
        Assert.True(awareness.TryEncodeUpdate(update, clients.Length == 0 ? [awareness.ClientId] : clients).Ok);
        return peer.SendAsync(Frame(update.WrittenSpan));
    }

    private static byte[] Frame(ReadOnlySpan<byte> update)
    {
        var bytes = new List<byte> { 1 };
        for (var v = (ulong)update.Length; ; v >>= 7)
        {
            bytes.Add((byte)(v < 0x80 ? v : (v & 0x7f) | 0x80));
            if (v < 0x80) break;
        }

        bytes.AddRange(update.ToArray());
        return bytes.ToArray();
    }

    /// <summary>What a peer's awareness holds after applying every awareness message the server sent it.</summary>
    private static Dictionary<ulong, string> Seen(Peer peer)
    {
        using var view = new Awareness(new Doc(new DocOptions { ClientId = 999 }));
        view.SetLocalState(YValue.Null);
        foreach (var message in peer.Awareness)
        {
            ReadOnlySpan<byte> input = message.AsSpan(1);
            var at = 0;
            while ((input[at++] & 0x80) != 0) { }
            Assert.True(view.TryApplyUpdate(input[at..], null).Ok);
        }

        return view.GetStates().ToDictionary(s => s.Key, s => s.Value.AsString());
    }

    [Fact, Covers("SRV-22"), Covers("SRV-17")]
    public async Task AnAwarenessUpdate_IsSentToEveryConnection_TheSenderIncluded_AndAReadOnlyPeerMayShareIt()
    {
        await using var server = new YServer();
        var a = await Peer.JoinAsync(server, "doc", 1);
        var b = await Peer.JoinAsync(server, "doc", 2, new ConnectionContext { IsReadOnly = true });
        await a.FlushAsync();
        await b.FlushAsync();

        using var presence = Presence(b, "bee");
        await SendAsync(b, presence);
        await a.FlushAsync();
        await b.FlushAsync();
        Assert.Equal(new Dictionary<ulong, string> { [2] = "bee" }, Seen(a));
        Assert.Equal(new Dictionary<ulong, string> { [2] = "bee" }, Seen(b));
        Assert.Single(a.Awareness);
        Assert.Single(b.Awareness);
    }

    [Fact, Covers("SRV-22")]
    public async Task AnUpdateThatChangesNothing_IsNotSent()
    {
        await using var server = new YServer();
        var a = await Peer.JoinAsync(server, "doc", 1);
        var b = await Peer.JoinAsync(server, "doc", 2);
        await a.FlushAsync();
        await b.FlushAsync();

        // client 2, clock 0, state null: a client the awareness never saw, removed (y-protocols ignores it)
        await b.SendAsync(new byte[] { 1, 8, 1, 2, 0, 4, (byte)'n', (byte)'u', (byte)'l', (byte)'l' });
        await a.FlushAsync();
        await b.FlushAsync();
        Assert.Empty(a.Awareness);
        Assert.Empty(b.Awareness);
    }

    [Fact, Covers("SRV-22")]
    public async Task APeerThatJoinsLate_GetsEveryKnownState_AfterTheServersStep1()
    {
        await using var server = new YServer();
        var a = await Peer.JoinAsync(server, "doc", 1);
        var b = await Peer.JoinAsync(server, "doc", 2);
        await a.FlushAsync();
        await b.FlushAsync();
        using var pa = Presence(a, "ay");
        using var pb = Presence(b, "bee");
        await SendAsync(a, pa);
        await SendAsync(b, pb);
        await a.FlushAsync();
        await b.FlushAsync();

        var c = await Peer.JoinAsync(server, "doc", 3);
        await c.FlushAsync();
        Assert.Equal(new Dictionary<ulong, string> { [1] = "ay", [2] = "bee" }, Seen(c));
        var first = c.Received.FindIndex(m => m.Span[0] == 1);
        Assert.Equal(0, c.Received[0].Span[0]);   // the server's Step1 comes first
        Assert.True(first > 0);
    }

    [Fact, Covers("SRV-22")]
    public async Task APeerThatJoinsWhenNoOneHasAState_GetsNoAwarenessMessage()
    {
        await using var server = new YServer();
        var a = await Peer.JoinAsync(server, "doc", 1);
        await a.FlushAsync();
        Assert.Empty(a.Awareness);
    }

    [Fact, Covers("SRV-22")]
    public async Task WhenAConnectionCloses_TheStatesItSet_AreRemovedAndTheRemovalIsSent()
    {
        await using var server = new YServer();
        var a = await Peer.JoinAsync(server, "doc", 1);
        var b = await Peer.JoinAsync(server, "doc", 2);
        await a.FlushAsync();
        await b.FlushAsync();
        using var pa = Presence(a, "ay");
        using var pb = Presence(b, "bee");
        await SendAsync(a, pa);
        await SendAsync(b, pb);
        await a.FlushAsync();
        Assert.Equal(new Dictionary<ulong, string> { [1] = "ay", [2] = "bee" }, Seen(a));

        await b.DisposeAsync();   // a crashed tab: no null state was sent
        await a.FlushAsync();
        Assert.Equal(new Dictionary<ulong, string> { [1] = "ay" }, Seen(a));

        var c = await Peer.JoinAsync(server, "doc", 3);
        await c.FlushAsync();
        Assert.Equal(new Dictionary<ulong, string> { [1] = "ay" }, Seen(c));
    }

    [Fact, Covers("SRV-22")]
    public async Task AConnectionClosedForAProtocolError_HasItsStatesRemovedToo()
    {
        await using var server = new YServer();
        var a = await Peer.JoinAsync(server, "doc", 1);
        var b = await Peer.JoinAsync(server, "doc", 2);
        await a.FlushAsync();
        await b.FlushAsync();
        using var pb = Presence(b, "bee");
        await SendAsync(b, pb);
        await a.FlushAsync();
        Assert.Equal(new Dictionary<ulong, string> { [2] = "bee" }, Seen(a));

        await b.SendAsync(new byte[] { 0, 7, 0 });   // no such sync message
        await a.FlushAsync();
        Assert.Equal(ConnectionCloseReason.ProtocolError, b.Connection.CloseReason);
        Assert.Empty(Seen(a));
    }

    [Fact, Covers("SRV-22")]
    public async Task AStateThatIsNotRenewed_IsRemovedAfterTheOutdatedTimeout_AndTheRemovalIsSent()
    {
        var time = new VirtualTimeProvider();
        await using var server = new YServer(new YServerOptions { TimeProvider = time });
        var a = await Peer.JoinAsync(server, "doc", 1);
        var b = await Peer.JoinAsync(server, "doc", 2);
        await a.FlushAsync();
        await b.FlushAsync();
        using var pb = Presence(b, "bee", time);
        await SendAsync(b, pb);
        await a.FlushAsync();
        Assert.Equal(new Dictionary<ulong, string> { [2] = "bee" }, Seen(a));

        // b stays connected but stops renewing (its own awareness's timer is on the same virtual clock and is disposed here)
        pb.Dispose();
        for (var i = 0; i < 100 && Seen(a).Count > 0; i++)
        {
            time.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(2, Ct);
            await a.FlushAsync();
        }

        Assert.Empty(Seen(a));
        Assert.Equal(ConnectionCloseReason.None, b.Connection.CloseReason);
    }

    [Theory, Covers("SRV-22")]
    [InlineData(new byte[] { 1 })]                                   // no payload
    [InlineData(new byte[] { 1, 9, 1, 2, 0 })]                       // the payload runs past the message
    [InlineData(new byte[] { 1, 5, 1, 2, 0, 1, 0 })]                 // the state is not JSON
    [InlineData(new byte[] { 1, 4, 1, 2, 0, 9 })]                    // the state's length runs past the payload
    [InlineData(new byte[] { 1, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 })]   // a length past a JS safe integer
    public async Task MalformedAwareness_ClosesTheSender_AndReachesNoOne(byte[] message)
    {
        await using var server = new YServer();
        var a = await Peer.JoinAsync(server, "doc", 1);
        var b = await Peer.JoinAsync(server, "doc", 2);
        await a.FlushAsync();
        await b.FlushAsync();

        Assert.True(await b.Connection.ReceiveAsync(message, Ct));
        await a.FlushAsync();
        Assert.Empty(a.Awareness);
        await foreach (var _ in b.Connection.Outgoing.ReadAllAsync(Ct)) { }
        Assert.Equal(ConnectionCloseReason.ProtocolError, b.Connection.CloseReason);
    }
}
