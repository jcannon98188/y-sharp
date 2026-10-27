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
/// The hot-Doc lifecycle (ADR 0024 SRV-5, SRV-18, SRV-20; TASK-5.8): a host's edits, reads and subscribers run on the document's mailbox and
/// materialize a <see cref="Doc"/> only when they ask, an edit is persisted and broadcast like a peer's update, and an idle document drops its
/// hot Doc and then itself, to be loaded from the store again with nothing lost. A deleted document closes its peers and is gone from the store.
/// </summary>
[Requires(Feature.UpdateFunctions, Feature.V2, Feature.V2Functions, Feature.Sync, Feature.Awareness, Feature.Text, Feature.Map, Feature.Events)]
public sealed class LifecycleTests
{
    private static readonly TimeSpan Hot = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Close = TimeSpan.FromMinutes(5);

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

    private static void Append(Transaction tx, string text)
    {
        var t = tx.Doc.GetText("text");
        t.Insert(t.Length, text);
    }

    private static Task<string> TextAsync(YServer server, string docId) => server.ReadAsync(docId, d => d.GetText("text").ToString(), Ct).AsTask();

    private static YServerOptions Options(IUpdateStore store, VirtualTimeProvider? time = null, ValidationHook? hook = null, TimeSpan? hot = null,
        TimeSpan? close = null) => new()
    {
        Store = store,
        Validation = hook is null ? null : _ => hook,
        TimeProvider = time ?? TimeProvider.System,
        HotDocumentIdleTimeout = hot ?? Hot,
        DocumentIdleTimeout = close ?? Close,
    };

    /// <summary>Moves virtual time on in steps until <paramref name="condition"/> holds; the mailbox worker reacts on its own thread.</summary>
    private static async Task AdvanceUntil(VirtualTimeProvider time, Func<bool> condition, string what)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(2, Ct);
        }

        Assert.True(condition(), what);
    }

    // ------------------------------------------------------------------ SRV-5, SRV-18: edits, reads, subscribers

    [Fact, Covers("SRV-5"), Covers("SRV-18"), Covers("SRV-8")]
    public async Task AHostEdit_IsPersistedAndBroadcastLikeAPeerUpdate_AndMaterializesTheDocOnlyThen()
    {
        var store = new InMemoryUpdateStore();
        await using var server = new YServer(Options(store));
        var a = await Peer.JoinAsync(server, "doc", 1);
        await a.FlushAsync();   // synced first, so its answer to the server's Step1 carries no edit of its own
        var b = await Peer.JoinAsync(server, "doc", 2);
        await a.InsertAsync("peer ");
        await a.FlushAsync();
        await b.FlushAsync();
        Assert.Null(server.Mailbox("doc")!.HotDocument);   // no hook: the relay alone served everything so far

        Assert.True(await server.EditAsync("doc", tx => Append(tx, "server"), cancellationToken: Ct));
        Assert.NotNull(server.Mailbox("doc")!.HotDocument);
        await a.FlushAsync();
        await b.FlushAsync();
        Assert.Equal("peer server", a.Text);
        Assert.Equal("peer server", b.Text);
        Assert.Single(a.Broadcasts);   // the server's own edit reaches every peer, the first peer's sender included
        Assert.Equal(2, b.Broadcasts.Count);

        var records = await Records(store, "doc");
        Assert.Equal(2, records.Count);
        Assert.Equal(a.Broadcasts[0], ToV1(records[1].UpdateV2));

        Assert.False(await server.EditAsync("doc", _ => { }, cancellationToken: Ct));   // an edit that changes nothing stores and sends nothing
        Assert.Equal(2, (await Records(store, "doc")).Count);
        await a.FlushAsync();
        Assert.Single(a.Broadcasts);
    }

    [Fact, Covers("SRV-5"), Covers("SRV-18")]
    public async Task AHostEditOrRead_OnADocumentNoOneHasOpen_LoadsItFromTheStore()
    {
        var store = new InMemoryUpdateStore();
        await using (var first = new YServer(Options(store)))
            Assert.True(await first.EditAsync("doc", tx => Append(tx, "stored"), cancellationToken: Ct));

        await using var server = new YServer(Options(store));
        Assert.Equal("stored", await TextAsync(server, "doc"));
        Assert.True(await server.EditAsync("doc", tx => Append(tx, "!"), cancellationToken: Ct));
        var peer = await Peer.JoinAsync(server, "doc", 3);
        await peer.FlushAsync();
        Assert.Equal("stored!", peer.Text);
    }

    [Fact, Covers("SRV-5")]
    public async Task AHostRead_SeesEveryMessageAcceptedBeforeIt_InAReadTransaction()
    {
        await using var server = new YServer(Options(new InMemoryUpdateStore()));
        var a = await Peer.JoinAsync(server, "doc", 1);
        await a.FlushAsync();   // synced first, so its answer to the server's Step1 carries no edit of its own
        for (var i = 0; i < 20; i++) await a.InsertAsync(i.ToString());

        // posted after the inserts, so handled after them, with no barrier in between
        Assert.Equal(a.Text, await TextAsync(server, "doc"));
        await Assert.ThrowsAnyAsync<Exception>(async () => await server.ReadAsync("doc", d =>
        {
            d.GetText("text").Insert(0, "no");   // a read cannot write
            return 0;
        }, Ct));
        Assert.Equal(a.Text, await TextAsync(server, "doc"));
    }

    [Fact, Covers("SRV-5"), Covers("SRV-8")]
    public async Task AFailingEdit_StoresAndSendsNothing_AndLeavesTheDocAsItWas()
    {
        var store = new GatedStore();
        await using var server = new YServer(Options(store));
        var a = await Peer.JoinAsync(server, "doc", 1);
        await a.FlushAsync();   // synced first, so its answer to the server's Step1 carries no edit of its own
        await a.InsertAsync("kept");
        await a.FlushAsync();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await server.EditAsync("doc", tx =>
        {
            Append(tx, " lost");
            throw new InvalidOperationException("edit failed");
        }, cancellationToken: Ct));
        Assert.Equal("edit failed", thrown.Message);

        store.Fail("doc");
        await Assert.ThrowsAnyAsync<Exception>(async () => await server.EditAsync("doc", tx => Append(tx, " unstored"), cancellationToken: Ct));
        store.Fail("doc", false);

        Assert.Equal("kept", await TextAsync(server, "doc"));
        Assert.Single(await Records(store.Inner, "doc"));
        await a.FlushAsync();
        Assert.Empty(a.Broadcasts);

        // the document goes on: the next edit and the next peer update both land
        Assert.True(await server.EditAsync("doc", tx => Append(tx, "!"), cancellationToken: Ct));
        await a.FlushAsync();
        await a.InsertAsync("?");
        await a.FlushAsync();
        Assert.Equal("kept!?", a.Text);
        Assert.Equal("kept!?", await TextAsync(server, "doc"));
        var relay = new Doc();
        relay.ApplyUpdate(server.Mailbox("doc")!.Relay.EncodeStateAsUpdate());
        Assert.Equal("kept!?", relay.GetText("text").ToString());
    }

    [Fact, Covers("SRV-18"), Covers("SRV-7")]
    public async Task ASubscriber_IsCalledNowAndAfterEveryAcceptedUpdate_UntilDisposed()
    {
        var store = new InMemoryUpdateStore();
        ValidationHook hook = (in ValidationRequest r) => r.Connection.Identity != "rejected" ? ValidationResult.Accept : ValidationResult.Reject("no");
        await using var server = new YServer(Options(store, hook: hook));
        var seen = new List<string>();
        var subscription = await server.SubscribeAsync("doc", d =>
        {
            seen.Add(d.GetText("text").ToString());
            if (seen.Count == 3) throw new InvalidOperationException("an indexer bug");   // ignored: the document goes on
        }, Ct);

        var a = await Peer.JoinAsync(server, "doc", 1);
        await a.FlushAsync();   // synced first, so its answer to the server's Step1 carries no edit of its own
        await a.InsertAsync("a");
        await server.EditAsync("doc", tx => Append(tx, "b"), cancellationToken: Ct);
        await a.FlushAsync();
        await a.InsertAsync("c");
        var rejected = await Peer.JoinAsync(server, "doc", 2, new ConnectionContext { Identity = "rejected" });
        await rejected.InsertAsync("x");   // rejected by the hook: not an accepted update
        await rejected.FlushAsync();
        await a.FlushAsync();
        Assert.Equal(new[] { "", "a", "ab", "abc" }, seen);

        await subscription.DisposeAsync();
        await subscription.DisposeAsync();
        await a.InsertAsync("d");
        await a.FlushAsync();
        Assert.Equal(4, seen.Count);
    }

    [Fact, Covers("SRV-4"), Covers("SRV-9")]
    public async Task AnUpdatePostedJustBeforeTheHostDisposesItsConnection_IsStillStoredAndSent()
    {
        var store = new InMemoryUpdateStore();
        await using var server = new YServer(Options(store));
        var a = await Peer.JoinAsync(server, "doc", 1);
        var b = await Peer.JoinAsync(server, "doc", 2);
        await a.FlushAsync();
        await b.FlushAsync();
        await a.InsertAsync("last words");
        await a.DisposeAsync();   // no barrier: the update is still queued when the connection closes

        await b.FlushAsync();
        Assert.Equal("last words", b.Text);
        Assert.Single(await Records(store, "doc"));
    }

    // ------------------------------------------------------------------ deletion (docs/api-decisions.md E3)

    [Fact, Covers("SRV-10"), Covers("SRV-9")]
    public async Task DeletingAnOpenDocument_ClosesItsPeers_EmptiesTheStore_AndTheNextPeerFindsNothing()
    {
        var store = new InMemoryUpdateStore();
        await using var server = new YServer(Options(store));
        var a = await Peer.JoinAsync(server, "doc", 1);
        var other = await Peer.JoinAsync(server, "other", 2);
        await a.InsertAsync("gone");
        await other.InsertAsync("kept");
        await a.FlushAsync();
        await other.FlushAsync();

        await server.DeleteAsync("doc", Ct);

        Assert.True(a.Connection.IsClosed);
        Assert.Equal(ConnectionCloseReason.DocumentDeleted, a.Connection.CloseReason);
        Assert.Null(server.Mailbox("doc"));
        Assert.Empty(await Records(store, "doc"));
        Assert.False(other.Connection.IsClosed);
        Assert.NotEmpty(await Records(store, "other"));

        var b = await Peer.JoinAsync(server, "doc", 3);
        await b.FlushAsync();
        Assert.Equal("", b.Text);
        Assert.Equal("", await TextAsync(server, "doc"));
    }

    [Fact, Covers("SRV-10")]
    public async Task WhileADocumentIsBeingDeleted_ANewPeerAndAHostReadWait_ThenFindItEmpty()
    {
        var store = new GatedStore();
        await using var server = new YServer(Options(store));
        Assert.True(await server.EditAsync("doc", tx => Append(tx, "old"), cancellationToken: Ct));
        store.DeleteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var deleting = server.DeleteAsync("doc", Ct).AsTask();
        Assert.True(await store.Deleting.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        var joining = Peer.JoinAsync(server, "doc", 1);
        var reading = TextAsync(server, "doc");
        await Task.Delay(50, Ct);
        Assert.False(joining.IsCompleted);
        Assert.False(reading.IsCompleted);

        store.DeleteGate.SetResult();
        await deleting;
        Assert.Equal("", await reading);
        var peer = await joining;
        await peer.FlushAsync();
        Assert.Equal("", peer.Text);
        Assert.Empty(await Records(store.Inner, "doc"));
    }

    [Fact, Covers("SRV-10")]
    public async Task DeletingADocumentNoOneHasOpen_RemovesItFromTheStore()
    {
        var store = new InMemoryUpdateStore();
        await using (var first = new YServer(Options(store)))
            Assert.True(await first.EditAsync("doc", tx => Append(tx, "stored"), cancellationToken: Ct));
        Assert.Single(await Records(store, "doc"));

        await using var server = new YServer(Options(store));
        await server.DeleteAsync("doc", Ct);
        Assert.Empty(await Records(store, "doc"));
        Assert.Equal("", await TextAsync(server, "doc"));
    }

    [Fact]
    public async Task DeleteAsync_OnAShutDownServer_Throws()
    {
        var server = new YServer(Options(new InMemoryUpdateStore()));
        await server.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => server.DeleteAsync("doc", Ct).AsTask());
    }

    // ------------------------------------------------------------------ SRV-20: idle eviction

    [Fact, Covers("SRV-20"), Covers("SRV-19")]
    public async Task AnIdleDocument_DropsItsHotDoc_ThenItself_AndReloadsWithNothingLost()
    {
        var time = new VirtualTimeProvider();
        var store = new InMemoryUpdateStore();
        await using var server = new YServer(Options(store, time));
        var a = await Peer.JoinAsync(server, "doc", 1);
        await a.FlushAsync();   // synced first, so its answer to the server's Step1 carries no edit of its own
        await a.InsertAsync("before");
        await a.FlushAsync();
        Assert.Equal("before", await TextAsync(server, "doc"));
        var mailbox = server.Mailbox("doc")!;
        Assert.NotNull(mailbox.HotDocument);

        // a connected peer keeps everything, however long it is quiet
        time.Advance(TimeSpan.FromHours(1));
        await a.FlushAsync();
        Assert.Same(mailbox, server.Mailbox("doc"));
        Assert.NotNull(mailbox.HotDocument);

        await a.DisposeAsync();
        await AdvanceUntil(time, () => mailbox.HotDocument is null, "the hot Doc is dropped after the hot timeout");
        Assert.Same(mailbox, server.Mailbox("doc"));   // the relay outlives the hot Doc
        await AdvanceUntil(time, () => server.Mailbox("doc") is null, "the document is closed after the idle timeout");
        await mailbox.Worker.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var b = await Peer.JoinAsync(server, "doc", 2);
        await b.FlushAsync();
        Assert.Equal("before", b.Text);
        Assert.NotSame(mailbox, server.Mailbox("doc"));
        await b.InsertAsync(" after");
        await b.FlushAsync();
        Assert.Equal("before after", await TextAsync(server, "doc"));
        Assert.Equal(2, (await Records(store, "doc")).Count);
    }

    [Fact, Covers("SRV-20"), Covers("SRV-18"), Covers("SRV-21")]
    public async Task AHookedDocument_RebuildsItsEvictedDocFromTheRelay_BeforeTheNextHookCall()
    {
        var time = new VirtualTimeProvider();
        var seen = new List<string>();
        ValidationHook hook = (in ValidationRequest r) =>
        {
            seen.Add(r.Document.GetText("text").ToString());
            return ValidationResult.Accept;
        };
        await using var server = new YServer(Options(new InMemoryUpdateStore(), time, hook, close: Timeout.InfiniteTimeSpan));
        var a = await Peer.JoinAsync(server, "doc", 1);
        await a.FlushAsync();   // synced first, so its answer to the server's Step1 carries no edit of its own
        await a.InsertAsync("one");
        await a.FlushAsync();
        await a.DisposeAsync();
        var mailbox = server.Mailbox("doc")!;
        await AdvanceUntil(time, () => mailbox.HotDocument is null, "the hooked document's Doc is dropped too");

        time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(20, Ct);
        Assert.Same(mailbox, server.Mailbox("doc"));   // an infinite document timeout keeps the relay

        var b = await Peer.JoinAsync(server, "doc", 2);
        await b.FlushAsync();
        await b.InsertAsync(" two");
        await b.FlushAsync();
        Assert.Equal(new[] { "", "one" }, seen);
        var fromRelay = new Doc();
        fromRelay.ApplyUpdate(mailbox.Relay.EncodeStateAsUpdate());
        Assert.Equal(fromRelay.EncodeStateAsUpdate(), mailbox.HotDocument!.EncodeStateAsUpdate());
    }

    [Fact, Covers("SRV-20"), Covers("SRV-18")]
    public async Task ASubscriberKeepsTheDocumentAndItsDoc_UntilItLeaves()
    {
        var time = new VirtualTimeProvider();
        await using var server = new YServer(Options(new InMemoryUpdateStore(), time));
        var calls = 0;
        var subscription = await server.SubscribeAsync("doc", _ => calls++, Ct);
        var mailbox = server.Mailbox("doc")!;
        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(20, Ct);
        Assert.Same(mailbox, server.Mailbox("doc"));
        Assert.NotNull(mailbox.HotDocument);

        await subscription.DisposeAsync();
        await AdvanceUntil(time, () => server.Mailbox("doc") is null, "the document closes once its subscriber leaves");
        Assert.Equal(1, calls);
    }

    [Fact, Covers("SRV-20"), Covers("SRV-4")]
    public async Task EvictionRacingWithTraffic_LosesNoUpdate()
    {
        var time = new VirtualTimeProvider();
        var store = new InMemoryUpdateStore();
        await using var server = new YServer(Options(store, time, hot: TimeSpan.Zero, close: TimeSpan.Zero));
        using var stop = new CancellationTokenSource();
        var clock = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                await Task.Yield();
            }
        }, Ct);

        // every round opens the document again (as a peer, an edit or a read) while the clock keeps trying to evict it
        var expected = "";
        for (var round = 0; round < 60; round++)
        {
            var piece = $"<{round}>";
            expected += piece;
            switch (round % 3)
            {
                case 0:
                {
                    var p = await Peer.JoinAsync(server, "doc", (ulong)(100 + round));
                    await p.FlushAsync();
                    Assert.Equal(expected[..^piece.Length], p.Text);
                    await p.InsertAsync(piece);
                    await p.DisposeAsync();
                    break;
                }

                case 1:
                    Assert.True(await server.EditAsync("doc", tx => Append(tx, piece), cancellationToken: Ct));
                    break;
                default:
                    await server.EditAsync("doc", tx => Append(tx, piece), cancellationToken: Ct);
                    Assert.Equal(expected, await TextAsync(server, "doc"));
                    break;
            }
        }

        stop.Cancel();
        await clock;
        Assert.Equal(expected, await TextAsync(server, "doc"));
        var replayed = new Doc();
        foreach (var r in await Records(store, "doc")) replayed.ApplyUpdate(r.UpdateV2.Span, null, UpdateFormat.V2);
        Assert.Equal(expected, replayed.GetText("text").ToString());
    }
}
