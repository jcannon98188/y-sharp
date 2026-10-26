using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Relay;
using YSharp.TestKit.Traceability;

namespace YSharp.Server.Tests.Mailbox;

/// <summary>
/// Validation and the hot document (ADR 0024 SRV-13..SRV-19, SRV-21; TASK-5.6): the hook runs on the mailbox before persist and broadcast, a
/// rejected update is dropped and its sender told and resynced, a hook that throws or overruns is a reject, a read-only connection's writes are
/// dropped with no Doc, and a document with no hook never materializes one.
/// </summary>
[Requires(Feature.UpdateFunctions, Feature.V2, Feature.V2Functions, Feature.Sync, Feature.Awareness, Feature.Text, Feature.TextFormat, Feature.Array, Feature.Map, Feature.Events)]
public sealed class ValidationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<int> RecordCount(IUpdateStore store, string docId)
    {
        var n = 0;
        await foreach (var _ in store.ReadSinceAsync(docId, 0, Ct)) n++;
        return n;
    }

    private static ValidationHook NoBadWords => (in ValidationRequest r) =>
        r.Decoded.Structs.Any(s => s.Text?.Contains("bad", StringComparison.Ordinal) == true) ? ValidationResult.Reject("no bad words") : ValidationResult.Accept;

    private static async Task<(YServer Server, InMemoryUpdateStore Store, Peer Writer, Peer Watcher)> Open(ValidationHook? hook, YServerOptions? overrides = null)
    {
        var store = new InMemoryUpdateStore();
        var options = overrides ?? new YServerOptions();
        var server = new YServer(new YServerOptions
        {
            Store = store,
            Validation = hook is null ? null : _ => hook,
            ValidationDeadline = options.ValidationDeadline,
            MaxRejectsBeforeClose = options.MaxRejectsBeforeClose,
        });
        var writer = await Peer.JoinAsync(server, "doc", 1, new ConnectionContext { Identity = "writer" });
        var watcher = await Peer.JoinAsync(server, "doc", 2);
        await writer.FlushAsync();
        await watcher.FlushAsync();
        return (server, store, writer, watcher);
    }

    [Fact, Covers("SRV-14"), Covers("SRV-21")]
    public async Task AnAcceptedUpdate_IsSeenByTheHook_ThenStored_AppliedAndBroadcast()
    {
        var seen = new List<(string Doc, string? Identity, string Before, string? Text, bool Deadline)>();
        var threads = new HashSet<int>();
        ValidationHook hook = (in ValidationRequest r) =>
        {
            var text = r.Decoded.Structs.FirstOrDefault(s => s.Content == UpdateContentKind.String);
            seen.Add((r.DocumentId, r.Connection.Identity, r.Document.GetText("text").ToString(), text.Text, r.Deadline.CanBeCanceled));
            return ValidationResult.Accept;
        };
        var (server, store, writer, watcher) = await Open(hook);
        await using var _ = server;

        await writer.InsertAsync("one");
        await writer.InsertAsync("two");
        await writer.FlushAsync();
        await watcher.DrainAsync();

        Assert.Equal(new[] { ("doc", (string?)"writer", "", (string?)"one", true), ("doc", "writer", "one", "two", true) }, seen);
        Assert.Equal("onetwo", watcher.Text);
        Assert.Equal(2, await RecordCount(store, "doc"));

        // SRV-21: the hot Doc and the relay hold the same content
        var mailbox = server.Mailbox("doc")!;
        var fromRelay = new Doc();
        fromRelay.ApplyUpdate(mailbox.Relay.EncodeStateAsUpdate());
        Assert.Equal(fromRelay.EncodeStateAsUpdate(), mailbox.HotDocument!.EncodeStateAsUpdate());
        Assert.Equal("onetwo", mailbox.HotDocument.GetText("text").ToString());
    }

    [Fact, Covers("SRV-15"), Covers("SRV-16")]
    public async Task ARejectedUpdate_IsDropped_TheSenderIsToldAndResynced_AndRepeatedRejectsClose()
    {
        var (server, store, writer, watcher) = await Open(NoBadWords, new YServerOptions { MaxRejectsBeforeClose = 3 });
        await using var _ = server;
        await writer.InsertAsync("good ");
        await writer.FlushAsync();
        var before = writer.Received.Count;

        await writer.InsertAsync("bad");
        await writer.FlushAsync();
        await writer.FlushAsync();   // the client answered the resync's Step1 with a Step2 holding the edit again; that is rejected too

        var answer = writer.Received.Skip(before).Select(m => (m.Span[0], m.Span[1])).ToArray();
        Assert.Equal(new[] { ((byte)2, (byte)0), ((byte)0, (byte)0), ((byte)0, (byte)1) }, answer.Take(3));   // permission-denied, Step1, Step2
        Assert.Equal(new[] { "no bad words", "no bad words" }, writer.Denied);
        Assert.Equal(ConnectionCloseReason.None, writer.Connection.CloseReason);

        await watcher.DrainAsync();
        Assert.Equal("good ", watcher.Text);
        Assert.Single(watcher.Broadcasts);
        Assert.Equal(1, await RecordCount(store, "doc"));
        Assert.Equal("good ", server.Mailbox("doc")!.HotDocument!.GetText("text").ToString());

        // the edit stays in the client's document (SRV-16): it is not converged until it discards it, and the next reject closes it
        Assert.Equal("good bad", writer.Text);
        await writer.InsertAsync(" bad again");
        await watcher.FlushAsync();
        Assert.Equal(ConnectionCloseReason.TooManyRejects, writer.Connection.CloseReason);
        Assert.Equal(1, await RecordCount(store, "doc"));
    }

    [Fact, Covers("SRV-16")]
    public async Task WithRejectsNeverClosing_ARejectedEditDoesNotBounceForever()
    {
        var (server, store, writer, _) = await Open(NoBadWords, new YServerOptions { MaxRejectsBeforeClose = 0 });
        await using var __ = server;
        await writer.InsertAsync("bad");
        for (var i = 0; i < 4; i++) await writer.FlushAsync();
        Assert.Equal(2, writer.Denied.Count);   // the update, then the Step2 answering the resync; the barriers' Step1s bring nothing new
        Assert.Equal(ConnectionCloseReason.None, writer.Connection.CloseReason);
        Assert.Equal(0, await RecordCount(store, "doc"));
    }

    public static IEnumerable<object[]> FaultingHooks() => new[]
    {
        new object[] { "throws", "validation failed" },
        new object[] { "sleeps past the deadline", "validation timed out" },
        new object[] { "honours the deadline", "validation timed out" },
        new object[] { "writes to the document", "validation failed" },
    };

    [Theory, Covers("SRV-14")]
    [MemberData(nameof(FaultingHooks))]
    public async Task AHookThatFaults_RejectsTheUpdate(string fault, string reason)
    {
        ValidationHook hook = (in ValidationRequest r) =>
        {
            switch (fault)
            {
                case "throws":
                    throw new InvalidOperationException("schema check crashed");
                case "sleeps past the deadline":
                    Thread.Sleep(300);
                    return ValidationResult.Accept;
                case "honours the deadline":
                    r.Deadline.WaitHandle.WaitOne();
                    r.Deadline.ThrowIfCancellationRequested();
                    return ValidationResult.Accept;
                default:
                    r.Document.GetText("text").Insert(0, "sneaky");
                    return ValidationResult.Accept;
            }
        };
        var (server, store, writer, watcher) = await Open(hook, new YServerOptions { ValidationDeadline = TimeSpan.FromMilliseconds(50) });
        await using var _ = server;

        await writer.InsertAsync("x");
        await writer.FlushAsync();
        Assert.NotEmpty(writer.Denied);
        Assert.All(writer.Denied, d => Assert.Equal(reason, d));
        Assert.Equal(0, await RecordCount(store, "doc"));
        await watcher.DrainAsync();
        Assert.Empty(watcher.Broadcasts);
        Assert.Equal("", server.Mailbox("doc")!.HotDocument!.GetText("text").ToString());
    }

    [Fact, Covers("SRV-17"), Covers("SRV-13")]
    public async Task AReadOnlyConnection_CanSyncAndShowPresence_ButItsUpdatesAreDroppedWithNoDoc()
    {
        var store = new InMemoryUpdateStore();
        await using var server = new YServer(new YServerOptions { Store = store });
        var reader = await Peer.JoinAsync(server, "doc", 1, new ConnectionContext { IsReadOnly = true }, d => d.GetText("text").Insert(0, "local"));
        var writer = await Peer.JoinAsync(server, "doc", 2);
        await writer.FlushAsync();
        await reader.FlushAsync();   // its Step2 answer to the server's Step1 is dropped, and its own Step1 is answered

        await writer.InsertAsync("shared");
        await writer.FlushAsync();
        await reader.InsertAsync(" mine");
        await reader.FlushAsync();
        await writer.FlushAsync();

        Assert.Equal("shared", writer.Text);
        Assert.Contains("shared", reader.Text);
        Assert.Equal(1, await RecordCount(store, "doc"));
        Assert.Equal(ConnectionCloseReason.None, reader.Connection.CloseReason);
        Assert.Null(server.Mailbox("doc")!.HotDocument);
    }

    [Fact, Covers("SRV-17")]
    public async Task AReadOnlyConnectionsUpdates_NeverReachTheHook()
    {
        var calls = 0;
        ValidationHook hook = (in ValidationRequest _) =>
        {
            calls++;
            return ValidationResult.Accept;
        };
        var (server, store, writer, _) = await Open(hook);
        await using var __ = server;
        var reader = await Peer.JoinAsync(server, "doc", 3, new ConnectionContext { IsReadOnly = true });
        await reader.InsertAsync("ignored");
        await reader.FlushAsync();
        await writer.InsertAsync("kept");
        await writer.FlushAsync();
        Assert.Equal(1, calls);
        Assert.Equal(1, await RecordCount(store, "doc"));
    }

    [Fact, Covers("SRV-13"), Covers("SRV-18")]
    public async Task ADocumentWithNoHook_NeverMaterializesADoc()
    {
        var (server, store, writer, watcher) = await Open(null);
        await using var _ = server;
        for (var i = 0; i < 10; i++) await writer.InsertAsync(i.ToString());
        await writer.FlushAsync();
        await watcher.DrainAsync();
        Assert.Equal(writer.Text, watcher.Text);
        Assert.Equal(10, await RecordCount(store, "doc"));
        Assert.Null(server.Mailbox("doc")!.HotDocument);
    }

    [Fact, Covers("SRV-18"), Covers("SRV-19"), Covers("SRV-21")]
    public async Task AHookedDocument_IsRehydratedFromTheStore_BeforeItsFirstMessage()
    {
        var updates = RelayWorkload.Updates(11, 60, UpdateFormat.V1);
        var store = new InMemoryUpdateStore();
        var log = new UpdateLog(store);
        foreach (var u in updates.Take(updates.Count / 2)) Assert.True((await log.AppendAsync("doc", u, cancellationToken: Ct)).Ok);
        await log.CompactAsync("doc", Ct);
        foreach (var u in updates.Skip(updates.Count / 2)) Assert.True((await log.AppendAsync("doc", u, cancellationToken: Ct)).Ok);
        var expected = new Doc();
        foreach (var u in updates) expected.ApplyUpdate(u);

        string? firstSeen = null;
        ValidationHook hook = (in ValidationRequest r) =>
        {
            firstSeen ??= r.Document.GetText("text").ToString();
            return ValidationResult.Accept;
        };
        await using var server = new YServer(new YServerOptions { Store = store, Validation = _ => hook });
        var peer = await Peer.JoinAsync(server, "doc", 99);
        await peer.FlushAsync();
        await peer.InsertAsync("!");
        await peer.FlushAsync();

        Assert.Equal(expected.GetText("text").ToString(), firstSeen);
        var hot = server.Mailbox("doc")!.HotDocument!;
        Assert.Equal(peer.Doc.EncodeStateAsUpdate(), hot.EncodeStateAsUpdate());
        var fromRelay = new Doc();
        fromRelay.ApplyUpdate(server.Mailbox("doc")!.Relay.EncodeStateAsUpdate());
        Assert.Equal(fromRelay.EncodeStateAsUpdate(), hot.EncodeStateAsUpdate());
    }
}
