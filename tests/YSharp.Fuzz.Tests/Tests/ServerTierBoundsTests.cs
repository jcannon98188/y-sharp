using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YSharp.Lib0;
using YSharp.Fuzz.Builder;
using YSharp.Fuzz.Generators;
using YSharp.Fuzz.Guards;
using YSharp.Server;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Fuzz.Tests;

/// <summary>
/// Tests that read process-wide counters (<see cref="BufferPool.Outstanding"/>, <see cref="GC.GetTotalMemory"/>), so they run after the
/// parallel ones, one at a time.
/// </summary>
[CollectionDefinition("ServerTierSerial", DisableParallelization = true)]
public sealed class ServerTierSerialCollection { }

/// <summary>
/// TASK-5.3: the relay and the mailbox entry point under streams of adversarial updates. #2: what a relay retains stays bounded by its
/// <see cref="YLimits"/> (huge clocks, missing dependencies, many client ids, size amplification). #1: a rejected update leaves nothing rented
/// and nothing stored, relayed or sent, and a good peer still syncs.
/// </summary>
[Collection("ServerTierSerial")]
[Requires(Feature.Fuzz, Feature.UpdateFunctions, Feature.V2, Feature.V2Functions, Feature.Sync, Feature.Awareness)]
public sealed class ServerTierBoundsTests
{
    /// <summary>Retained managed bytes per struct a relay holds as accepted document content; measured at about a third of this.</summary>
    private const long BytesPerStruct = 512;

    /// <summary>Slack for the measurement itself: the relay, its lists and the collector's own noise.</summary>
    private const long Slack = 2 * 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ #2 relay memory under adversarial streams

    [Theory, Covers("HOS-CLOCK", "RLY-7", Layer = CoverLayer.Unit)]
    [InlineData(1L << 32)]
    [InlineData(1L << 50)]
    [InlineData((1L << 53) - 100_000)]
    public void HugeClocks_EveryUpdateWaitsOnAGap_ThePendingStateStaysUnderTheLimit(long start)
    {
        var limits = Hostile.Tiny;
        var updates = new List<byte[]>();
        for (var i = 0; i < 2000; i++)
            updates.Add(UpdateBuilder.V1(UpdateModel.Single(1 + (ulong)(i % 7), (ulong)(start + i * 13), Struct.InRoot("t", Content.Str("abcdefgh")))));

        var (relay, accepted, rejected) = Stream(limits, updates);
        Assert.True(rejected > 0, "the stream never reached the pending bound");
        Assert.True(accepted > 0);
        // every struct waits on a gap, so the whole state is pending: its V1 encoding is the measured pending state
        Assert.True(relay.EncodeStateAsUpdate().Length <= limits.MaxPendingBytes + 16, $"the state is {relay.EncodeStateAsUpdate().Length} bytes");
        Assert.Empty(relay.GetStateVector().Clients);   // no client starts at 0
        RelayHostile.AssertPostCondition(relay, "huge clocks");
    }

    [Fact, Covers("HOS-REF", "RLY-5", "RLY-7", Layer = CoverLayer.Unit)]
    public void MissingDependencies_ACrossClientOriginIsPassedOn_AGapIsBounded()
    {
        // a struct whose origin is on a client the relay never saw: the relay holds it as document content (RLY-5), one struct per update
        var crossClient = new List<byte[]>();
        for (var i = 0; i < 3000; i++)
            crossClient.Add(UpdateBuilder.V1(UpdateModel.Single(10_000 + (ulong)i, 0, Struct.InRoot("t", Content.Str("x"), origin: new Id(7_000_000, (ulong)i)))));
        var (relay, accepted, _) = StreamMeasured(YLimits.Default, crossClient, out var retained);
        Assert.Equal(crossClient.Count, accepted);
        Assert.True(retained <= BytesPerStruct * accepted + Slack, $"{accepted} one-struct updates retained {retained} bytes");
        RelayHostile.AssertPostCondition(relay, "cross-client origins");

        // a struct after a gap in its own client's clocks: pending, bounded
        var gapped = new List<byte[]>();
        for (var i = 0; i < 3000; i++)
            gapped.Add(UpdateBuilder.V1(UpdateModel.Single(20_000 + (ulong)i, 3, Struct.InRoot("t", Content.Str("x")))));
        var (tiny, tinyAccepted, tinyRejected) = Stream(Hostile.Tiny, gapped);
        Assert.True(tinyRejected > 0 && tinyAccepted > 0);
        Assert.True(tiny.EncodeStateAsUpdate().Length <= Hostile.Tiny.MaxPendingBytes + 16);
        RelayHostile.AssertPostCondition(tiny, "gapped clients");
    }

    [Fact, Covers("HOS-AMP", "RLY-7", Layer = CoverLayer.Unit)]
    public void ManyClientIds_DeleteRangesOnClientsWithNoStructs_AreBoundedAsPending()
    {
        // a delete range on a client the relay holds nothing of cannot be passed on as part of the document yet
        var deletes = new List<byte[]>();
        for (var i = 0; i < 5000; i++)
            deletes.Add(UpdateBuilder.V1(UpdateModel.Empty.WithDeletes(new DeleteClient(100_000 + (ulong)i, new[] { new DeleteRange(0, 1UL << 40) }))));
        var (relay, accepted, rejected) = Stream(Hostile.Tiny, deletes);
        Assert.True(accepted > 0 && rejected > 0);
        Assert.True(relay.EncodeStateAsUpdate().Length <= Hostile.Tiny.MaxPendingBytes + 16);
        RelayHostile.AssertPostCondition(relay, "many delete clients");
    }

    [Theory, Covers("HOS-AMP", Layer = CoverLayer.Unit)]
    [InlineData(UpdateFormat.V1)]
    [InlineData(UpdateFormat.V2)]
    public void Amplification_EachUpdateIsCappedByMaxStructsPerUpdate_AndRetainedMemoryIsPerStruct(UpdateFormat format)
    {
        // a V2 update run-length encodes its columns, so a hundred one-clock GCs take a handful of bytes: retention is bounded per struct, and
        // the struct count per update by the limit
        var limits = Hostile.Tiny;
        byte[] Encode(UpdateModel m) => format == UpdateFormat.V2 ? UpdateBuilder.V2(m) : UpdateBuilder.V1(m);
        UpdateModel Gcs(ulong client, int count)
        {
            var structs = new Struct[count];
            for (var i = 0; i < count; i++) structs[i] = Struct.Gc(1);
            return UpdateModel.Single(client, 0, structs);
        }

        Assert.Equal(YError.LimitExceeded, new YRelay(limits).TryApplyUpdate(Encode(Gcs(1, limits.MaxStructsPerUpdate + 1)), format).Error);

        var updates = new List<byte[]>();
        for (var i = 0; i < 2000; i++) updates.Add(Encode(Gcs(1000 + (ulong)i, limits.MaxStructsPerUpdate)));
        var (relay, accepted, _) = StreamMeasured(limits, updates, out var retained, format);
        Assert.Equal(updates.Count, accepted);
        Assert.True(retained <= BytesPerStruct * accepted * limits.MaxStructsPerUpdate + Slack, $"{accepted} updates of {limits.MaxStructsPerUpdate} structs retained {retained} bytes");
        RelayHostile.AssertPostCondition(relay, "amplified");
    }

    private static (YRelay Relay, int Accepted, int Rejected) Stream(YLimits limits, List<byte[]> updates, UpdateFormat format = UpdateFormat.V1) =>
        StreamMeasured(limits, updates, out _, format);

    /// <summary>Folds the updates into a fresh relay with <see cref="RelayHostile.ApplyTo"/>'s checks; <paramref name="retained"/> is the managed heap growth across the stream.</summary>
    private static (YRelay Relay, int Accepted, int Rejected) StreamMeasured(YLimits limits, List<byte[]> updates, out long retained, UpdateFormat format = UpdateFormat.V1)
    {
        var relay = new YRelay(limits);
        var before = GC.GetTotalMemory(forceFullCollection: true);
        int accepted = 0, rejected = 0;
        for (var i = 0; i < updates.Count; i++)
        {
            // the full guard (state before and after) for the first and every 97th update; the rest only fold, to keep the stream linear
            var status = i % 97 == 0 ? RelayHostile.ApplyTo(relay, $"update {i}", updates[i], format) : relay.TryApplyUpdate(updates[i], format);
            if (status.Ok) accepted++;
            else
            {
                Assert.Equal(YError.LimitExceeded, status.Error);
                rejected++;
            }
        }

        retained = GC.GetTotalMemory(forceFullCollection: true) - before;   // the inputs were allocated before the first measurement
        GC.KeepAlive(relay);
        return (relay, accepted, rejected);
    }

    // ------------------------------------------------------------------ #1 nothing rented after a rejected update

    [Fact, Covers("HOS-MUT", "RLY-4")]
    public void ByteMutator_FoldedIntoARelay_LeavesNothingRented() =>
        FuzzRunner.Check("HOS-MUT", ByteMutator.Layer(), (RawInput i) =>
        {
            var relay = new YRelay();
            var before = BufferPool.Outstanding;
            relay.TryApplyUpdate(i.Bytes, i.Format);
            relay.TryPrepareUpdate(i.Bytes, out _, i.Format);
            relay.EncodeStateAsUpdate(format: UpdateFormat.V2);
            Assert.Equal(before, BufferPool.Outstanding);
        }, i => (i.Bytes, i.Format), cases: 300);

    // ------------------------------------------------------------------ #1 the mailbox entry point

    [Theory, Covers("HOS-MUT", "SRV-8", "SRV-9")]
    [InlineData(false)]
    [InlineData(true)]
    public void ByteMutator_ThroughTheMailbox_ARejectedMessageReachesNoOne_AndAGoodPeerStillSyncs(bool hooked) =>
        FuzzRunner.Check("HOS-MUT", ByteMutator.Layer(), (RawInput i) => MailboxCase(i, hooked).GetAwaiter().GetResult(), i => (i.Bytes, i.Format), cases: 250);

    // nightly finds (run 40501680613, 2026-10-27): a record reusing an id with other content, folded live one update at a time but merged on
    // reload, so the store and the relay disagreed after a restart (TASK-18)
    [Theory, Covers("HOS-MUT", "SRV-8", "SRV-9")]
    [InlineData("02010200c40000000001000101000001000000000000", "mutation of structs/pending-two-missing-clients.json:e (Duplicate)", false)]
    [InlineData("030103008400000100010200840000010001010000010000000000", "mutation of structs/pending-chain-permutations.json:d (Overwrite)", false)]
    [InlineData("02010200c40000000001000101000001000000000000", "mutation of structs/pending-two-missing-clients.json:e (Duplicate)", true)]
    [InlineData("030103008400000100010200840000010001010000010000000000", "mutation of structs/pending-chain-permutations.json:d (Overwrite)", true)]
    public Task NightlyFinds_ThroughTheMailbox_AreContained(string hex, string origin, bool hooked) =>
        MailboxCase(new RawInput(UpdateFormat.V1, Convert.FromHexString(hex), origin), hooked);

    private static async Task MailboxCase(RawInput input, bool hooked)
    {
        var rented = BufferPool.Outstanding;
        var store = new InMemoryUpdateStore();
        ValidationHook accept = (in ValidationRequest _) => ValidationResult.Accept;
        YServer Start() => new(new YServerOptions { Store = store, Validation = hooked ? _ => accept : null });
        var message = Wrap(input);

        var server = Start();
        var good = await server.ConnectAsync("doc", cancellationToken: Ct);
        Assert.True(await good.ReceiveAsync(SyncFrame(w => YSync.WriteUpdate(w, UpdateBuilder.V1(UpdateBuilder.TextInsert(1, "t", "hello")))), Ct));
        await BarrierAsync(good);
        var records = await CountAsync(store);

        var hostile = await server.ConnectAsync("doc", cancellationToken: Ct);
        await hostile.ReceiveAsync(message, Ct);
        var (seen, state) = await BarrierAsync(good);
        var stored = await CountAsync(store);
        var label = $"{input} as {Describe(message)}";

        if (hostile.IsClosed)
        {
            // refused: nothing stored, relayed or sent to the other peer
            Assert.True(hostile.CloseReason == ConnectionCloseReason.ProtocolError, $"{label}: closed with {hostile.CloseReason} {hostile.CloseDetail}");
            Assert.True(stored == records, $"{label}: a refused message was stored");
            Assert.True(seen.Count == 0, $"{label}: a refused message reached another peer");
        }
        else
        {
            // accepted or ignored: an accepted update is stored once and broadcast once
            Assert.True(stored == records || stored == records + 1, $"{label}: {stored - records} records for one message");
            Assert.True(seen.Count <= 1, $"{label}: {seen.Count} messages for one");
            if (stored == records + 1) Assert.True(seen.Count == 1 && seen[0].Span[0] == 0, $"{label}: a stored update was not broadcast");
        }

        // the good peer's answer is a valid update that a Doc applies, and a server restarted over the store serves the same state
        Assert.True(YUpdate.TryValidate(state).Ok, $"{label}: the server's Step2 is not a valid update");
        var doc = Hostile.NewDoc();
        Assert.True(doc.TryApplyUpdate(state).Error is YError.None or YError.Rejected, $"{label}: a Doc refuses the server's state");
        await hostile.DisposeAsync();
        await good.DisposeAsync();
        await server.DisposeAsync();

        server = Start();
        var late = await server.ConnectAsync("doc", cancellationToken: Ct);
        var (_, reloaded) = await BarrierAsync(late);
        Assert.True(state.AsSpan().SequenceEqual(reloaded), $"{label}: the store and the relay disagree after a restart");
        await late.DisposeAsync();
        await server.DisposeAsync();
        Assert.True(rented == BufferPool.Outstanding, $"{label}: {BufferPool.Outstanding - rented} buffers still rented");
    }

    /// <summary>The mutated bytes as one of the messages a peer can send: a sync Update, a sync Step2, an awareness update, or the raw message.</summary>
    private static byte[] Wrap(RawInput input)
    {
        var pick = (input.Bytes.Length + input.Origin.Length) % 4;
        return pick switch
        {
            0 => SyncFrame(w => YSync.WriteUpdate(w, input.Bytes)),
            1 => SyncFrame(w => YSync.WriteStep2(w, input.Bytes)),
            2 => new ByteSink().VarUint(1).VarBytes(input.Bytes).ToArray(),
            _ => input.Bytes.Length == 0 ? new byte[] { 0 } : input.Bytes,
        };
    }

    private static string Describe(byte[] message) => message.Length == 0 ? "empty" : message[0] switch { 0 => "sync", 1 => "awareness", _ => $"type {message[0]}" };

    private static byte[] SyncFrame(Action<IBufferWriter<byte>> write)
    {
        var w = new ArrayBufferWriter<byte>();
        w.Write(new byte[] { 0 });
        write(w);
        return w.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Sends a Step1 with an empty state vector and reads until its Step2: everything posted earlier has been handled. Returns the other
    /// messages read meanwhile (Step1s from the server aside) and the Step2's update, the server's whole state.
    /// </summary>
    private static async Task<(List<ReadOnlyMemory<byte>> Seen, byte[] State)> BarrierAsync(YConnection connection)
    {
        Assert.True(await connection.ReceiveAsync(SyncFrame(w => YSync.WriteStep1(w, new byte[] { 0 })), Ct));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var seen = new List<ReadOnlyMemory<byte>>();
        while (true)
        {
            if (!await connection.Outgoing.WaitToReadAsync(timeout.Token)) throw new InvalidOperationException($"closed: {connection.CloseReason} {connection.CloseDetail}");
            while (connection.Outgoing.TryRead(out var message))
            {
                if (message.Span[0] == 0)
                {
                    ReadOnlySpan<byte> frame = message.Span[1..];
                    Assert.True(YSync.TryReadFrame(ref frame, out var type, out var payload).Ok);
                    if (type == SyncMessageType.Step2) return (seen, payload.ToArray());
                    if (type == SyncMessageType.Step1) continue;
                }

                seen.Add(message);
            }
        }
    }

    private static async Task<int> CountAsync(InMemoryUpdateStore store)
    {
        var count = 0;
        await foreach (var _ in store.ReadSinceAsync("doc", 0, Ct)) count++;
        return count;
    }
}
