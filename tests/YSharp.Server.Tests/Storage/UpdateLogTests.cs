using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Relay;
using YSharp.TestKit.Traceability;

namespace YSharp.Server.Tests.Storage;

/// <summary>
/// The update log (ADR 0024 SRV-10, SRV-11; TASK-5.4): validated appends stored as V2, recovery by folding the log through the relay tier,
/// compaction into one V2 snapshot, and crash consistency at every step of append and compaction. A recovered state is compared as a document:
/// a <see cref="Doc"/> built from it must encode the same bytes as one built from the updates the log acknowledged.
/// </summary>
[Requires(Feature.UpdateFunctions, Feature.V2, Feature.V2Functions, Feature.Text, Feature.TextFormat, Feature.Array, Feature.Map, Feature.Events)]
public sealed class UpdateLogTests
{
    private const string DocId = "doc";

    public static IEnumerable<object[]> Formats() => new[] { new object[] { UpdateFormat.V1 }, new object[] { UpdateFormat.V2 } };

    private static byte[] StateOf(IEnumerable<byte[]> updates, UpdateFormat format)
    {
        var doc = new Doc();
        foreach (var u in updates) doc.ApplyUpdate(u, null, format);
        return doc.EncodeStateAsUpdate(default, UpdateFormat.V1);
    }

    private static byte[] StateOf(YRelay relay) => StateOf(new[] { relay.EncodeStateAsUpdate(default, UpdateFormat.V2) }, UpdateFormat.V2);

    /// <summary>Exactly <paramref name="count"/> updates: the workload's sync steps record nothing, so it runs three times as many steps and takes the first.</summary>
    private static List<byte[]> Workload(int seed, int count, UpdateFormat format)
    {
        var updates = RelayWorkload.Updates(seed, count * 3, format);
        Assert.True(updates.Count >= count, $"seed {seed}: only {updates.Count} updates");
        return updates.Take(count).ToList();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<byte[]> LoadedState(UpdateLog log) => StateOf((await log.LoadAsync(DocId, cancellationToken: Ct)).Relay);

    private static async Task AssertLoaded(byte[] expected, UpdateLog log, string label)
    {
        var actual = await LoadedState(log);
        HexAssert.Equal(expected, actual, label);
    }

    private static async Task<List<UpdateRecord>> ReadAll(IUpdateStore store, long after = 0)
    {
        var list = new List<UpdateRecord>();
        await foreach (var r in store.ReadSinceAsync(DocId, after, cancellationToken: Ct)) list.Add(r);
        return list;
    }

    [Theory, Covers("SRV-10")]
    [MemberData(nameof(Formats))]
    public async Task AnAppend_IsValidated_StoredAsV2_AndNumberedInOrder(UpdateFormat format)
    {
        var updates = Workload(3, 12, format);
        var store = new InMemoryUpdateStore();
        var log = new UpdateLog(store);
        var seqs = new List<long>();
        foreach (var u in updates)
        {
            var r = await log.AppendAsync(DocId, u, format, cancellationToken: Ct);
            Assert.True(r.Ok, r.Status.Error.ToString());
            seqs.Add(r.Sequence);
        }

        Assert.Equal(Enumerable.Range(1, updates.Count).Select(i => (long)i), seqs);
        var records = await ReadAll(store);
        Assert.Equal(seqs, records.Select(r => r.Sequence));
        for (var i = 0; i < updates.Count; i++)
        {
            var v2 = new ArrayBufferWriter<byte>();
            Assert.True(YUpdate.TryConvert(updates[i], format, UpdateFormat.V2, v2).Ok);
            HexAssert.Equal(v2.WrittenSpan.ToArray(), records[i].UpdateV2.ToArray(), $"record {i + 1}");
            Assert.False(records[i].IsSnapshot);
        }

        await AssertLoaded(StateOf(updates, format), log, "loaded");
    }

    [Fact, Covers("SRV-10")]
    public async Task AnInvalidOrOverLimitUpdate_IsRefused_AndNothingIsWritten()
    {
        var store = new InMemoryUpdateStore();
        var log = new UpdateLog(store, new YLimits { MaxUpdateBytes = 16 });
        Assert.Equal(YError.Malformed, (await log.AppendAsync(DocId, Hex.Bytes("01 01 01"), cancellationToken: Ct)).Status.Error);
        Assert.Equal(YError.LimitExceeded, (await log.AppendAsync(DocId, new byte[17], cancellationToken: Ct)).Status.Error);
        Assert.Empty(await ReadAll(store));
        Assert.Equal(1, (await log.AppendAsync(DocId, Hex.Bytes("0101010004010174016100"), cancellationToken: Ct)).Sequence);
    }

    [Theory, Covers("SRV-10", "SRV-11")]
    [MemberData(nameof(Formats))]
    public async Task Compaction_SwapsInOneV2Snapshot_AndKeepsTheState(UpdateFormat format)
    {
        var updates = Workload(7, 30, format);
        var store = new InMemoryUpdateStore();
        var log = new UpdateLog(store);
        foreach (var u in updates.Take(20)) Assert.True((await log.AppendAsync(DocId, u, format, cancellationToken: Ct)).Ok);
        Assert.Equal(20, await log.CompactAsync(DocId, cancellationToken: Ct));

        var records = await ReadAll(store);
        var snapshot = Assert.Single(records);
        Assert.True(snapshot.IsSnapshot);
        Assert.Equal(20, snapshot.Sequence);
        await AssertLoaded(StateOf(updates.Take(20), format), log, "after compaction");

        foreach (var u in updates.Skip(20)) Assert.True((await log.AppendAsync(DocId, u, format, cancellationToken: Ct)).Ok);
        records = await ReadAll(store);
        Assert.Equal(Enumerable.Range(20, 11).Select(i => (long)i), records.Select(r => r.Sequence));
        Assert.Empty(await ReadAll(store, 30));
        Assert.Equal(new long[] { 25, 26, 27, 28, 29, 30 }, (await ReadAll(store, 24)).Select(r => r.Sequence));
        Assert.Equal(new long[] { 20, 21 }, (await ReadAll(store, 19)).Select(r => r.Sequence).Take(2));
        await AssertLoaded(StateOf(updates, format), log, "after later appends");

        Assert.Equal(30, await log.CompactAsync(DocId, cancellationToken: Ct));
        await AssertLoaded(StateOf(updates, format), log, "after a second compaction");
        Assert.Equal(31, (await log.AppendAsync(DocId, updates[0], format, cancellationToken: Ct)).Sequence);
    }

    [Fact, Covers("SRV-10", "SRV-11")]
    public async Task AnAppendThatRacesACompaction_SurvivesIt()
    {
        var updates = Workload(11, 20, UpdateFormat.V1);
        var gate = new PausingStore(new InMemoryUpdateStore());
        var log = new UpdateLog(gate);
        foreach (var u in updates.Take(10)) Assert.True((await log.AppendAsync(DocId, u, cancellationToken: Ct)).Ok);

        var compaction = log.CompactAsync(DocId, cancellationToken: Ct).AsTask();
        await gate.CompactionReached.Task;
        foreach (var u in updates.Skip(10)) Assert.True((await log.AppendAsync(DocId, u, cancellationToken: Ct)).Ok);
        gate.Release.SetResult();
        Assert.Equal(10, await compaction);

        var records = await ReadAll(gate);
        Assert.Equal(Enumerable.Range(10, 11).Select(i => (long)i), records.Select(r => r.Sequence));
        await AssertLoaded(StateOf(updates, UpdateFormat.V1), log, "loaded");
    }

    public static IEnumerable<object[]> CrashCases() =>
        from point in new[] { CrashPoint.AppendBeforeWrite, CrashPoint.AppendAfterWrite, CrashPoint.CompactBeforeSwap, CrashPoint.CompactAfterSwap, CrashPoint.ReadMidway }
        from occurrence in new[] { 0, 1, 3, 5 }
        select new object[] { point, occurrence };

    /// <summary>
    /// A server appends each update and compacts every fifth, and crashes once at <paramref name="point"/> on its <paramref name="occurrence"/>-th
    /// pass. After a restart over the surviving store, the loaded state is the pre-crash state: every acknowledged update, plus an in-flight one the
    /// store had recorded. The client then resends what was not acknowledged, the run finishes, and the result equals a document that applied
    /// every update once: nothing lost, and nothing applied twice.
    /// </summary>
    [Theory, Covers("SRV-10", "SRV-11")]
    [MemberData(nameof(CrashCases))]
    public async Task ACrashAtAnyStep_RecoversThePreCrashState_WithNoLossAndNoDoubleApply(CrashPoint point, int occurrence)
    {
        var updates = Workload(100 + occurrence, 30, UpdateFormat.V1);
        var durable = new InMemoryUpdateStore();
        var crashing = new CountingCrashStore(durable, point, occurrence);
        var log = new UpdateLog(crashing);
        var acked = new List<byte[]>();
        var crashedAt = -1;
        byte[]? inFlight = null;
        for (var i = 0; i < updates.Count && crashedAt < 0; i++)
        {
            try
            {
                inFlight = updates[i];
                Assert.True((await log.AppendAsync(DocId, updates[i], cancellationToken: Ct)).Ok);
                acked.Add(updates[i]);
                inFlight = null;
                if (acked.Count % 5 == 0) await log.CompactAsync(DocId, cancellationToken: Ct);
            }
            catch (SimulatedCrashException)
            {
                crashedAt = i;
            }
        }

        Assert.True(crashedAt >= 0, "the crash point was never reached");
        var restarted = new UpdateLog(durable);
        var stored = point == CrashPoint.AppendAfterWrite ? acked.Append(inFlight!) : acked;
        await AssertLoaded(StateOf(stored, UpdateFormat.V1), restarted, $"after the crash at {point} #{occurrence}");

        // the client never heard back about the in-flight update, so it resends it; then the run goes on
        var rest = (inFlight is null ? Array.Empty<byte[]>() : new[] { inFlight }).Concat(updates.Skip(crashedAt + 1));
        foreach (var u in rest) Assert.True((await restarted.AppendAsync(DocId, u, cancellationToken: Ct)).Ok);
        await restarted.CompactAsync(DocId, cancellationToken: Ct);
        await AssertLoaded(StateOf(updates, UpdateFormat.V1), restarted, "after the run finished");
    }

    [Theory, Covers("SRV-11")]
    [MemberData(nameof(Formats))]
    public async Task ReplayingTheLogTwice_IsIdempotent(UpdateFormat format)
    {
        var updates = Workload(5, 25, format);
        var log = new UpdateLog(new InMemoryUpdateStore());
        foreach (var u in updates.Take(15)) Assert.True((await log.AppendAsync(DocId, u, format, cancellationToken: Ct)).Ok);
        await log.CompactAsync(DocId, cancellationToken: Ct);
        foreach (var u in updates.Skip(15)) Assert.True((await log.AppendAsync(DocId, u, format, cancellationToken: Ct)).Ok);

        var once = (await log.LoadAsync(DocId, cancellationToken: Ct)).Relay;
        var twice = (await log.LoadAsync(DocId, cancellationToken: Ct)).Relay;
        Assert.Equal(25, await log.FoldAsync(DocId, 0, twice, cancellationToken: Ct));
        HexAssert.Equal(once.EncodeStateAsUpdate(default, UpdateFormat.V2), twice.EncodeStateAsUpdate(default, UpdateFormat.V2), "replayed twice");
        HexAssert.Equal(StateOf(updates, format), StateOf(twice), "against the inputs");
    }

    [Fact, Covers("SRV-10")]
    public async Task ARecordThatDoesNotDecode_FailsTheLoad_InsteadOfBeingSkipped()
    {
        var store = new InMemoryUpdateStore();
        var log = new UpdateLog(store);
        Assert.True((await log.AppendAsync(DocId, Hex.Bytes("0101010004010174016100"), cancellationToken: Ct)).Ok);
        await store.AppendAsync(DocId, Hex.Bytes("ff ff"), cancellationToken: Ct);
        var e = await Assert.ThrowsAsync<InvalidDataException>(async () => await log.LoadAsync(DocId, cancellationToken: Ct));
        Assert.Contains("record 2", e.Message, StringComparison.Ordinal);
    }

    [Theory, Covers("SRV-10", "SRV-11")]
    [MemberData(nameof(Formats))]
    public async Task ALoad_GivesTheBytesOfFoldingTheRecordsOneAtATime(UpdateFormat format)
    {
        // a snapshot, then records in order, out of order and repeated: the load merges them once (TASK-13)
        var updates = Workload(11, 60, format);
        var store = new InMemoryUpdateStore();
        var log = new UpdateLog(store);
        foreach (var u in updates.Take(20)) Assert.True((await log.AppendAsync(DocId, u, format, cancellationToken: Ct)).Ok);
        await log.CompactAsync(DocId, cancellationToken: Ct);
        var rng = new Random(11);
        var rest = updates.Skip(20).Concat(updates.Where(_ => rng.Next(4) == 0)).OrderBy(_ => rng.Next()).ToList();
        foreach (var u in rest) Assert.True((await log.AppendAsync(DocId, u, format, cancellationToken: Ct)).Ok);

        var folded = new YRelay(log.StoredLimits);
        foreach (var r in await ReadAll(store)) Assert.True(folded.TryApplyUpdate(r.UpdateV2.Span, UpdateFormat.V2).Ok);
        var loaded = await log.LoadAsync(DocId, cancellationToken: Ct);
        foreach (var f in new[] { UpdateFormat.V1, UpdateFormat.V2 })
            HexAssert.Equal(folded.EncodeStateAsUpdate(default, f), loaded.Relay.EncodeStateAsUpdate(default, f), $"{f}");

        // catching up a relay that holds the first records folds the rest the same way
        var caughtUp = new YRelay(log.StoredLimits);
        foreach (var u in updates.Take(20)) Assert.True(caughtUp.TryApplyUpdate(u, format).Ok);
        Assert.Equal(loaded.LastSequence, await log.FoldAsync(DocId, 20, caughtUp, cancellationToken: Ct));
        HexAssert.Equal(folded.EncodeStateAsUpdate(default, UpdateFormat.V2), caughtUp.EncodeStateAsUpdate(default, UpdateFormat.V2), "caught up");
    }

    // a record holding other content at an id the log already holds: live, the relay folds each update in turn and skips a struct it holds, so the
    // first content wins; one merge of the whole log can let the later one win (which one depends on the other clients in the record, as Yjs
    // orders mergeUpdates' readers), and a restart then serves another document (TASK-18)
    [Theory, Covers("SRV-10", "SRV-11")]
    [InlineData("01 01 01 00 00 01 00")]                                                                    // a GC over the first character
    [InlineData("01 01 01 00 04 01 01 74 01 6A 00")]                                                        // a string "j" at its id
    [InlineData("02 01 02 00 C4 00 00 00 00 01 00 01 01 00 00 01 00 00 00 00 00 00")]                       // the nightly finds of
    [InlineData("03 01 03 00 84 00 00 01 00 01 02 00 84 00 00 01 00 01 01 00 00 01 00 00 00 00 00")]        // 2026-10-27: a GC at it after
    public async Task ALoad_OfARecordReusingAnId_GivesTheBytesOfFoldingTheRecordsOneAtATime(string hex)
    {
        var doc = new Doc(new DocOptions { ClientId = 1 });
        var updates = new List<byte[]>();
        doc.UpdateV2 += (u, _, _, _) => updates.Add(u.ToArray());
        var text = doc.GetText("t");
        text.Insert(0, "hello");
        text.Insert(5, " world");
        var store = new InMemoryUpdateStore();
        var log = new UpdateLog(store);
        Assert.True((await log.AppendAsync(DocId, updates[0], UpdateFormat.V2, cancellationToken: Ct)).Ok);
        Assert.True((await log.AppendAsync(DocId, Hex.Bytes(hex), UpdateFormat.V1, cancellationToken: Ct)).Ok);
        Assert.True((await log.AppendAsync(DocId, updates[1], UpdateFormat.V2, cancellationToken: Ct)).Ok);

        var folded = new YRelay(log.StoredLimits);
        foreach (var r in await ReadAll(store)) folded.TryApplyUpdate(r.UpdateV2.Span, UpdateFormat.V2);
        var expected = folded.EncodeStateAsUpdate(default, UpdateFormat.V2);
        var loaded = (await log.LoadAsync(DocId, cancellationToken: Ct)).Relay.EncodeStateAsUpdate(default, UpdateFormat.V2);
        HexAssert.Equal(expected, loaded, "load");

        // catching up a relay that holds the first record, and compacting, fold the same way
        var caughtUp = new YRelay(log.StoredLimits);
        Assert.True(caughtUp.TryApplyUpdate(updates[0], UpdateFormat.V2).Ok);
        await log.FoldAsync(DocId, 1, caughtUp, cancellationToken: Ct);
        HexAssert.Equal(expected, caughtUp.EncodeStateAsUpdate(default, UpdateFormat.V2), "caught up");
        await log.CompactAsync(DocId, cancellationToken: Ct);
        var compacted = (await log.LoadAsync(DocId, cancellationToken: Ct)).Relay.EncodeStateAsUpdate(default, UpdateFormat.V2);
        HexAssert.Equal(expected, compacted, "compacted");
    }

    // one character per record, stored newest first: folding these one at a time held every record as a gap and re-merged the whole state each
    // time, n times the state; one merge costs the log's size times log n (TASK-13). A resent record ends a merged batch (TASK-18), which in
    // order costs no more than the batch
    [Theory, Covers("SRV-10")]
    [InlineData(true, 0)]
    [InlineData(false, 50)]
    public async Task ALoad_CostsTheLogsSize_NotItsRecordsTimesTheState(bool newestFirst, int resendEvery)
    {
        async Task<double> BytesPerRecord(int n)
        {
            var doc = new Doc(new DocOptions { ClientId = 1 });
            var updates = new List<byte[]>();
            doc.UpdateV2 += (u, _, _, _) => updates.Add(u.ToArray());
            var text = doc.GetText("t");
            for (var i = 0; i < n; i++) text.Insert(i, "x");
            var log = new UpdateLog(new InMemoryUpdateStore());
            for (var k = 0; k < updates.Count; k++)
            {
                var u = updates[newestFirst ? updates.Count - 1 - k : k];
                Assert.True((await log.AppendAsync(DocId, u, UpdateFormat.V2, cancellationToken: Ct)).Ok);
                if (resendEvery > 0 && k % resendEvery == resendEvery - 1) Assert.True((await log.AppendAsync(DocId, u, UpdateFormat.V2, cancellationToken: Ct)).Ok);
            }
            await log.LoadAsync(DocId, cancellationToken: Ct);   // warm
            var before = GC.GetAllocatedBytesForCurrentThread();
            var loaded = log.LoadAsync(DocId, cancellationToken: Ct).AsTask().GetAwaiter().GetResult();   // the in-memory store reads synchronously
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            HexAssert.Equal(StateOf(updates, UpdateFormat.V2), StateOf(loaded.Relay), $"n = {n}");
            return (double)bytes / n;
        }

        var small = await BytesPerRecord(300);
        var large = await BytesPerRecord(2400);
        Assert.True(large < small * 2, $"a load allocated {small:F0} bytes per record over 300 records and {large:F0} over 2400");
    }

    [Fact, Covers("SRV-10")]
    public async Task AnEmptyLog_LoadsAnEmptyRelay_AndCompactsToNothing()
    {
        var store = new InMemoryUpdateStore();
        var log = new UpdateLog(store);
        var loaded = await log.LoadAsync(DocId, cancellationToken: Ct);
        Assert.True(loaded.Relay.IsEmpty);
        Assert.Equal(0, loaded.LastSequence);
        Assert.Equal(0, await log.CompactAsync(DocId, cancellationToken: Ct));
        Assert.Empty(await ReadAll(store));
    }

    /// <summary>Crashes at the <c>occurrence</c>-th pass through <c>point</c> (counting from 0), then never again.</summary>
    private sealed class CountingCrashStore(IUpdateStore inner, CrashPoint point, int occurrence) : IUpdateStore
    {
        private readonly CrashingStore _crash = new(inner);
        private int _seen;

        private CrashingStore Arm(CrashPoint at)
        {
            _crash.Point = at == point && _seen++ == occurrence ? point : CrashPoint.None;
            return _crash;
        }

        public ValueTask<long> AppendAsync(string docId, ReadOnlyMemory<byte> updateV2, System.Threading.CancellationToken cancellationToken = default) =>
            Arm(point is CrashPoint.AppendBeforeWrite or CrashPoint.AppendAfterWrite ? point : CrashPoint.None).AppendAsync(docId, updateV2, cancellationToken);

        public IAsyncEnumerable<UpdateRecord> ReadSinceAsync(string docId, long afterSequence, System.Threading.CancellationToken cancellationToken = default) =>
            Arm(point == CrashPoint.ReadMidway ? point : CrashPoint.None).ReadSinceAsync(docId, afterSequence, cancellationToken);

        public ValueTask CompactAsync(string docId, long uptoSequence, ReadOnlyMemory<byte> mergedV2, System.Threading.CancellationToken cancellationToken = default) =>
            Arm(point is CrashPoint.CompactBeforeSwap or CrashPoint.CompactAfterSwap ? point : CrashPoint.None).CompactAsync(docId, uptoSequence, mergedV2, cancellationToken);

        public ValueTask DeleteAsync(string docId, System.Threading.CancellationToken cancellationToken = default) => inner.DeleteAsync(docId, cancellationToken);
    }

    /// <summary>Holds a compaction between its read and its swap until <see cref="Release"/> is set.</summary>
    private sealed class PausingStore(IUpdateStore inner) : IUpdateStore
    {
        public TaskCompletionSource CompactionReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<long> AppendAsync(string docId, ReadOnlyMemory<byte> updateV2, System.Threading.CancellationToken cancellationToken = default) =>
            inner.AppendAsync(docId, updateV2, cancellationToken);

        public IAsyncEnumerable<UpdateRecord> ReadSinceAsync(string docId, long afterSequence, System.Threading.CancellationToken cancellationToken = default) =>
            inner.ReadSinceAsync(docId, afterSequence, cancellationToken);

        public async ValueTask CompactAsync(string docId, long uptoSequence, ReadOnlyMemory<byte> mergedV2, System.Threading.CancellationToken cancellationToken = default)
        {
            CompactionReached.SetResult();
            await Release.Task;
            await inner.CompactAsync(docId, uptoSequence, mergedV2, cancellationToken);
        }

        public ValueTask DeleteAsync(string docId, System.Threading.CancellationToken cancellationToken = default) => inner.DeleteAsync(docId, cancellationToken);
    }
}
