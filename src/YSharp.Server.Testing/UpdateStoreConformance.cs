using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace YSharp.Server.Testing;

/// <summary>How one conformance check ended.</summary>
public enum ConformanceOutcome
{
    Passed,
    Failed,
    /// <summary>The check needs a fault-injection hook the backend does not provide; it did not run.</summary>
    NotSupported,
}

/// <summary>The result of one conformance check.</summary>
public sealed record ConformanceResult(string Check, ConformanceOutcome Outcome, string? Message = null)
{
    public override string ToString() => Message is null ? $"{Check}: {Outcome}" : $"{Check}: {Outcome}: {Message}";
}

/// <summary>A failed expectation inside a conformance check.</summary>
public sealed class ConformanceException(string message) : Exception(message);

/// <summary>
/// The storage conformance suite (ADR 0024 SRV-12): checks an <see cref="IUpdateStore"/> against the contract in its documentation. It needs no
/// test framework. Derive from it, say how to create and restart the backend, and run <see cref="RunAsync(CancellationToken)"/> or each name in
/// <see cref="CheckNames"/> from any test runner; a check passes when it ends without an exception.
/// </summary>
/// <remarks>
/// <para>
/// A crash is simulated at the boundaries around each store call: before an append reaches the store, after the store recorded it but before
/// the caller heard back, before and after a compaction's swap, and midway through a read. After the crash the suite calls
/// <see cref="RestartAsync"/>, which must return a store over the same durable medium, as a new process would see it.
/// </para>
/// <para>
/// Crashes inside a call need the backend's help. A backend that can tear its last record (a partial write), corrupt a record, or interrupt a
/// compaction midway overrides the matching hook; the checks that need a hook it does not provide report
/// <see cref="ConformanceOutcome.NotSupported"/>. A backend whose calls are atomic in memory, such as <see cref="InMemoryUpdateStore"/>, has no
/// such states to recover from.
/// </para>
/// </remarks>
public abstract class UpdateStoreConformance
{
    private readonly Dictionary<string, Func<CancellationToken, Task>> _checks;

    protected UpdateStoreConformance()
    {
        _checks = new Dictionary<string, Func<CancellationToken, Task>>(StringComparer.Ordinal)
        {
            ["SequencesStartAtOneAndIncrease"] = SequencesStartAtOneAndIncrease,
            ["ReadsReturnExactBytesInOrder"] = ReadsReturnExactBytesInOrder,
            ["ReadSinceSkipsEarlierRecords"] = ReadSinceSkipsEarlierRecords,
            ["AnUnknownDocumentReadsEmpty"] = AnUnknownDocumentReadsEmpty,
            ["CompactionReplacesCoveredRecordsAndKeepsLaterOnes"] = CompactionReplacesCoveredRecordsAndKeepsLaterOnes,
            ["CompactionPastTheLastSequenceIsRefused"] = CompactionPastTheLastSequenceIsRefused,
            ["SequencesAreNotReusedAfterCompaction"] = SequencesAreNotReusedAfterCompaction,
            ["ReadsDuringCompactionSeeOldOrNewNeverBoth"] = ReadsDuringCompactionSeeOldOrNewNeverBoth,
            ["DeleteRemovesTheLogAndANewLogStartsAtOne"] = DeleteRemovesTheLogAndANewLogStartsAtOne,
            ["AcknowledgedAppendsSurviveARestart"] = AcknowledgedAppendsSurviveARestart,
            ["ACrashAtEachStepOfAnAppendLosesNothingAcknowledged"] = ACrashAtEachStepOfAnAppendLosesNothingAcknowledged,
            ["ACrashAtEachStepOfACompactionKeepsTheState"] = ACrashAtEachStepOfACompactionKeepsTheState,
            ["AnInterruptedCompactionLeavesTheOldRecords"] = AnInterruptedCompactionLeavesTheOldRecords,
            ["ATornLastRecordReadsAsAbsent"] = ATornLastRecordReadsAsAbsent,
            ["ACorruptedRecordBeforeIntactOnesFailsTheRead"] = ACorruptedRecordBeforeIntactOnesFailsTheRead,
            ["ReplayingTheLogTwiceIsIdempotent"] = ReplayingTheLogTwiceIsIdempotent,
            ["ConcurrentAppendsToOneDocumentGetDistinctSequences"] = ConcurrentAppendsToOneDocumentGetDistinctSequences,
            ["ConcurrentDocumentsStayIndependent"] = ConcurrentDocumentsStayIndependent,
        };
    }

    /// <summary>The names of every check, for a test runner's data source.</summary>
    public IReadOnlyList<string> CheckNames => _checks.Keys.ToList();

    /// <summary>A new, empty store.</summary>
    protected abstract ValueTask<IUpdateStore> CreateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The store a new process would open over the same durable medium as <paramref name="store"/>, after a crash. A store with no durable medium
    /// (the in-memory default) returns <paramref name="store"/> itself: the suite then checks it as if the process had survived.
    /// </summary>
    protected abstract ValueTask<IUpdateStore> RestartAsync(IUpdateStore store, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="updateV2"/> as <paramref name="docId"/>'s next record but cuts the write off partway, as a crash during the write
    /// would, without completing the append. Return false when the backend cannot simulate this.
    /// </summary>
    protected virtual ValueTask<bool> TryTornAppendAsync(IUpdateStore store, string docId, ReadOnlyMemory<byte> updateV2, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);

    /// <summary>Damages the stored bytes of record <paramref name="sequence"/> so its integrity check fails. Return false when the backend cannot.</summary>
    protected virtual ValueTask<bool> TryCorruptRecordAsync(IUpdateStore store, string docId, long sequence, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);

    /// <summary>
    /// Starts a compaction of <paramref name="docId"/> up to <paramref name="uptoSequence"/> and cuts it off partway, as a crash during the
    /// compaction would, without completing it. Return false when the backend cannot simulate this.
    /// </summary>
    protected virtual ValueTask<bool> TryInterruptedCompactionAsync(IUpdateStore store, string docId, long uptoSequence, ReadOnlyMemory<byte> mergedV2, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);

    /// <summary>Runs every check and returns one result per check.</summary>
    public async Task<IReadOnlyList<ConformanceResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ConformanceResult>();
        foreach (var name in CheckNames) results.Add(await RunAsync(name, cancellationToken).ConfigureAwait(false));
        return results;
    }

    /// <summary>Runs one check by name. A failed expectation or an exception from the store is a <see cref="ConformanceOutcome.Failed"/> result.</summary>
    public async Task<ConformanceResult> RunAsync(string check, CancellationToken cancellationToken = default)
    {
        if (!_checks.TryGetValue(check, out var run)) throw new ArgumentException($"Unknown conformance check '{check}'.", nameof(check));
        try
        {
            await run(cancellationToken).ConfigureAwait(false);
            return new ConformanceResult(check, ConformanceOutcome.Passed);
        }
        catch (NotSupportedCheck e)
        {
            return new ConformanceResult(check, ConformanceOutcome.NotSupported, e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new ConformanceResult(check, ConformanceOutcome.Failed, e is ConformanceException ? e.Message : e.ToString());
        }
    }

    // ---------------------------------------------------------------- workload and helpers

    private const string Doc1 = "conformance-doc-1";
    private const string Doc2 = "conformance-doc-2";

    private sealed class NotSupportedCheck(string hook) : Exception($"the backend does not provide {hook}");

    /// <summary>
    /// <paramref name="count"/> V2 updates from two replicas that edit one text and one map and sync now and then, so later updates depend on
    /// earlier ones. Deterministic for a seed.
    /// </summary>
    protected static List<byte[]> Updates(int seed, int count)
    {
        var rng = new Random(seed);
        var docs = new[] { new Doc(new DocOptions { ClientId = 1 }), new Doc(new DocOptions { ClientId = 2 }) };
        var sync = new object();
        var updates = new List<byte[]>();
        foreach (var d in docs) d.UpdateV2 += (u, origin, _, _) => { if (!ReferenceEquals(origin, sync)) updates.Add(u.ToArray()); };
        while (updates.Count < count)
        {
            var doc = docs[rng.Next(2)];
            var text = doc.GetText("text");
            switch (rng.Next(6))
            {
                case 0 or 1 or 2:
                    text.Insert(rng.Next(text.Length + 1), "ab"[rng.Next(2)] + rng.Next(100).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case 3 when text.Length > 0:
                    text.Delete(rng.Next(text.Length), 1);
                    break;
                case 4:
                    doc.GetMap("map").Set("k" + rng.Next(3), rng.Next(1000));
                    break;
                default:
                {
                    var other = docs[1 - Array.IndexOf(docs, doc)];
                    var sv = new ArrayBufferWriter<byte>();
                    doc.EncodeStateVector(sv);
                    doc.ApplyUpdate(other.EncodeStateAsUpdate(sv.WrittenSpan), sync);
                    break;
                }
            }
        }

        foreach (var d in docs) d.Dispose();
        return updates.Take(count).ToList();
    }

    /// <summary>The document state of some V2 updates, as V1 bytes of a <see cref="Doc"/> that applied them.</summary>
    protected static byte[] StateOf(IEnumerable<ReadOnlyMemory<byte>> updatesV2)
    {
        using var doc = new Doc();
        foreach (var u in updatesV2) doc.ApplyUpdate(u.Span, null, UpdateFormat.V2);
        return doc.EncodeStateAsUpdate(default, UpdateFormat.V1);
    }

    private static byte[] StateOf(IEnumerable<byte[]> updatesV2) => StateOf(updatesV2.Select(u => (ReadOnlyMemory<byte>)u));

    private static async Task<List<UpdateRecord>> ReadAll(IUpdateStore store, string docId, long after, CancellationToken ct)
    {
        var list = new List<UpdateRecord>();
        await foreach (var r in store.ReadSinceAsync(docId, after, ct).ConfigureAwait(false)) list.Add(r);
        return list;
    }

    private static async Task<byte[]> StoredState(IUpdateStore store, string docId, CancellationToken ct) =>
        StateOf((await ReadAll(store, docId, 0, ct).ConfigureAwait(false)).Select(r => r.UpdateV2));

    private static byte[] Merge(IEnumerable<byte[]> updatesV2)
    {
        var output = new ArrayBufferWriter<byte>();
        var status = YUpdate.TryMerge(updatesV2.Select(u => (ReadOnlyMemory<byte>)u).ToList(), output, UpdateFormat.V2);
        if (!status.Ok) throw new InvalidOperationException("the workload did not merge: " + status.Error);
        return output.WrittenSpan.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new ConformanceException(message);
    }

    private static void SameState(byte[] expected, byte[] actual, string what) =>
        Check(expected.AsSpan().SequenceEqual(actual), $"{what}: the stored records do not fold to the expected document state");

    private static void SameSequences(IEnumerable<long> expected, IEnumerable<long> actual, string what)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        Check(e.SequenceEqual(a), $"{what}: expected sequences [{string.Join(", ", e)}], got [{string.Join(", ", a)}]");
    }

    private async Task<(IUpdateStore Store, List<byte[]> Updates, List<long> Seqs)> Seeded(int seed, int count, CancellationToken ct)
    {
        var store = await CreateAsync(ct).ConfigureAwait(false);
        var updates = Updates(seed, count);
        var seqs = new List<long>();
        foreach (var u in updates) seqs.Add(await store.AppendAsync(Doc1, u, ct).ConfigureAwait(false));
        return (store, updates, seqs);
    }

    // ---------------------------------------------------------------- checks

    private async Task SequencesStartAtOneAndIncrease(CancellationToken ct)
    {
        var (_, updates, seqs) = await Seeded(1, 10, ct).ConfigureAwait(false);
        SameSequences(Enumerable.Range(1, updates.Count).Select(i => (long)i), seqs, "appends");
    }

    private async Task ReadsReturnExactBytesInOrder(CancellationToken ct)
    {
        var store = await CreateAsync(ct).ConfigureAwait(false);
        var updates = Updates(2, 8);
        var buffer = new byte[updates.Max(u => u.Length)];
        foreach (var u in updates)
        {
            // the store must copy: the caller reuses its buffer after the append completes
            u.CopyTo(buffer, 0);
            await store.AppendAsync(Doc1, buffer.AsMemory(0, u.Length), ct).ConfigureAwait(false);
            Array.Fill(buffer, (byte)0xEE);
        }

        var records = await ReadAll(store, Doc1, 0, ct).ConfigureAwait(false);
        SameSequences(Enumerable.Range(1, updates.Count).Select(i => (long)i), records.Select(r => r.Sequence), "read");
        for (var i = 0; i < updates.Count; i++)
        {
            Check(!records[i].IsSnapshot, $"record {i + 1} is marked as a snapshot");
            Check(records[i].UpdateV2.Span.SequenceEqual(updates[i]), $"record {i + 1} does not hold the appended bytes");
        }
    }

    private async Task ReadSinceSkipsEarlierRecords(CancellationToken ct)
    {
        var (store, updates, _) = await Seeded(3, 10, ct).ConfigureAwait(false);
        SameSequences(new long[] { 7, 8, 9, 10 }, (await ReadAll(store, Doc1, 6, ct).ConfigureAwait(false)).Select(r => r.Sequence), "read after 6");
        Check((await ReadAll(store, Doc1, updates.Count, ct).ConfigureAwait(false)).Count == 0, "a read after the last sequence returned records");
    }

    private async Task AnUnknownDocumentReadsEmpty(CancellationToken ct)
    {
        var store = await CreateAsync(ct).ConfigureAwait(false);
        Check((await ReadAll(store, "conformance-never-written", 0, ct).ConfigureAwait(false)).Count == 0, "an unknown document returned records");
    }

    private async Task CompactionReplacesCoveredRecordsAndKeepsLaterOnes(CancellationToken ct)
    {
        var (store, updates, _) = await Seeded(4, 12, ct).ConfigureAwait(false);
        await store.CompactAsync(Doc1, 8, Merge(updates.Take(8)), ct).ConfigureAwait(false);
        var records = await ReadAll(store, Doc1, 0, ct).ConfigureAwait(false);
        Check(records.Count > 0 && records[0].IsSnapshot && records[0].Sequence == 8, "the snapshot covering 8 is not the first record");
        SameSequences(new long[] { 8, 9, 10, 11, 12 }, records.Select(r => r.Sequence), "after compacting up to 8");
        Check(records.Skip(1).All(r => !r.IsSnapshot), "a later record is marked as a snapshot");
        SameState(StateOf(updates), StateOf(records.Select(r => r.UpdateV2)), "after compaction");

        SameSequences(new long[] { 10, 11, 12 }, (await ReadAll(store, Doc1, 9, ct).ConfigureAwait(false)).Select(r => r.Sequence), "read after 9");
        var fromFive = await ReadAll(store, Doc1, 5, ct).ConfigureAwait(false);
        Check(fromFive.Count > 0 && fromFive[0].IsSnapshot, "a read from inside the compacted range does not start with the snapshot");

        await store.CompactAsync(Doc1, 12, Merge(updates), ct).ConfigureAwait(false);
        records = await ReadAll(store, Doc1, 0, ct).ConfigureAwait(false);
        Check(records.Count == 1 && records[0].IsSnapshot && records[0].Sequence == 12, "a second compaction did not replace the first snapshot");
        SameState(StateOf(updates), StateOf(records.Select(r => r.UpdateV2)), "after a second compaction");
    }

    private async Task CompactionPastTheLastSequenceIsRefused(CancellationToken ct)
    {
        var (store, updates, _) = await Seeded(5, 4, ct).ConfigureAwait(false);
        var refused = false;
        try
        {
            await store.CompactAsync(Doc1, 5, Merge(updates), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            refused = true;
        }

        Check(refused, "a compaction past the last appended sequence was accepted");
        SameSequences(new long[] { 1, 2, 3, 4 }, (await ReadAll(store, Doc1, 0, ct).ConfigureAwait(false)).Select(r => r.Sequence), "after the refused compaction");
    }

    private async Task SequencesAreNotReusedAfterCompaction(CancellationToken ct)
    {
        var (store, updates, _) = await Seeded(6, 6, ct).ConfigureAwait(false);
        await store.CompactAsync(Doc1, 6, Merge(updates), ct).ConfigureAwait(false);
        var next = await store.AppendAsync(Doc1, Updates(60, 1)[0], ct).ConfigureAwait(false);
        Check(next == 7, $"the append after compacting 1..6 got sequence {next}, not 7");
        store = await RestartAsync(store, ct).ConfigureAwait(false);
        next = await store.AppendAsync(Doc1, Updates(61, 1)[0], ct).ConfigureAwait(false);
        Check(next == 8, $"the append after a restart got sequence {next}, not 8");
    }

    private async Task ReadsDuringCompactionSeeOldOrNewNeverBoth(CancellationToken ct)
    {
        var (store, updates, _) = await Seeded(7, 20, ct).ConfigureAwait(false);
        var expected = StateOf(updates);
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested || reads == 0)
            {
                var records = await ReadAll(store, Doc1, 0, ct).ConfigureAwait(false);
                var snapshots = records.Count(r => r.IsSnapshot);
                Check(snapshots <= 1, "a read saw two snapshots");
                Check(records.Select(r => r.Sequence).Zip(records.Select(r => r.Sequence).Skip(1)).All(p => p.First < p.Second), "a read was out of order");
                SameState(expected, StateOf(records.Select(r => r.UpdateV2)), "a read during compaction");
                reads++;
            }
        }, ct)).ToList();

        for (var upto = 5; upto <= 20; upto += 5)
            await store.CompactAsync(Doc1, upto, Merge(updates.Take(upto)), ct).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(readers).ConfigureAwait(false);
    }

    private async Task DeleteRemovesTheLogAndANewLogStartsAtOne(CancellationToken ct)
    {
        var (store, updates, _) = await Seeded(8, 5, ct).ConfigureAwait(false);
        await store.AppendAsync(Doc2, updates[0], ct).ConfigureAwait(false);
        await store.CompactAsync(Doc1, 3, Merge(updates.Take(3)), ct).ConfigureAwait(false);
        await store.DeleteAsync(Doc1, ct).ConfigureAwait(false);
        Check((await ReadAll(store, Doc1, 0, ct).ConfigureAwait(false)).Count == 0, "a deleted document still has records");
        Check((await ReadAll(store, Doc2, 0, ct).ConfigureAwait(false)).Count == 1, "deleting one document changed another");
        Check(await store.AppendAsync(Doc1, updates[0], ct).ConfigureAwait(false) == 1, "a deleted document written again did not start at 1");
        await store.DeleteAsync("conformance-never-written", ct).ConfigureAwait(false);
    }

    private async Task AcknowledgedAppendsSurviveARestart(CancellationToken ct)
    {
        var (store, updates, _) = await Seeded(9, 10, ct).ConfigureAwait(false);
        await store.CompactAsync(Doc1, 4, Merge(updates.Take(4)), ct).ConfigureAwait(false);
        store = await RestartAsync(store, ct).ConfigureAwait(false);
        SameSequences(new long[] { 4, 5, 6, 7, 8, 9, 10 }, (await ReadAll(store, Doc1, 0, ct).ConfigureAwait(false)).Select(r => r.Sequence), "after a restart");
        SameState(StateOf(updates), await StoredState(store, Doc1, ct).ConfigureAwait(false), "after a restart");
    }

    private async Task ACrashAtEachStepOfAnAppendLosesNothingAcknowledged(CancellationToken ct)
    {
        foreach (var afterWrite in new[] { false, true })
        {
            var (store, updates, _) = await Seeded(10, 6, ct).ConfigureAwait(false);
            var inFlight = Updates(11, 7)[6];
            // before the write: the record never reached the store. After it: the store recorded it, and the caller never heard back.
            if (afterWrite) await store.AppendAsync(Doc1, inFlight, ct).ConfigureAwait(false);
            store = await RestartAsync(store, ct).ConfigureAwait(false);
            var expected = afterWrite ? updates.Append(inFlight) : updates;
            SameState(StateOf(expected), await StoredState(store, Doc1, ct).ConfigureAwait(false), $"after a crash {(afterWrite ? "after" : "before")} an append's write");

            // the client resends what was not acknowledged; a record stored twice folds as once
            var seq = await store.AppendAsync(Doc1, inFlight, ct).ConfigureAwait(false);
            Check(seq > updates.Count, $"the resent update got sequence {seq}, which an acknowledged append already had");
            SameState(StateOf(updates.Append(inFlight)), await StoredState(store, Doc1, ct).ConfigureAwait(false), "after the resend");
        }
    }

    private async Task ACrashAtEachStepOfACompactionKeepsTheState(CancellationToken ct)
    {
        foreach (var afterSwap in new[] { false, true })
        {
            var (store, updates, _) = await Seeded(12, 10, ct).ConfigureAwait(false);
            // before the swap: the merge was computed and never written. After it: the swap is done, and the caller never heard back.
            if (afterSwap) await store.CompactAsync(Doc1, 10, Merge(updates), ct).ConfigureAwait(false);
            store = await RestartAsync(store, ct).ConfigureAwait(false);
            var records = await ReadAll(store, Doc1, 0, ct).ConfigureAwait(false);
            SameState(StateOf(updates), StateOf(records.Select(r => r.UpdateV2)), $"after a crash {(afterSwap ? "after" : "before")} a compaction's swap");
            Check(afterSwap ? records.Count == 1 && records[0].IsSnapshot : records.All(r => !r.IsSnapshot), "the log is not in the state the crash left");

            // the compaction is simply run again
            await store.CompactAsync(Doc1, 10, Merge(updates), ct).ConfigureAwait(false);
            SameState(StateOf(updates), await StoredState(store, Doc1, ct).ConfigureAwait(false), "after compacting again");
        }
    }

    private async Task AnInterruptedCompactionLeavesTheOldRecords(CancellationToken ct)
    {
        var (store, updates, _) = await Seeded(13, 10, ct).ConfigureAwait(false);
        await store.CompactAsync(Doc1, 4, Merge(updates.Take(4)), ct).ConfigureAwait(false);
        if (!await TryInterruptedCompactionAsync(store, Doc1, 10, Merge(updates), ct).ConfigureAwait(false))
            throw new NotSupportedCheck(nameof(TryInterruptedCompactionAsync));
        store = await RestartAsync(store, ct).ConfigureAwait(false);
        var records = await ReadAll(store, Doc1, 0, ct).ConfigureAwait(false);
        Check(records.Count(r => r.IsSnapshot) == 1, "an interrupted compaction left no snapshot or two");
        var old = records.Count == 7 && records[0].Sequence == 4;
        var done = records.Count == 1 && records[0].Sequence == 10;
        Check(old || done, $"an interrupted compaction left a mix of old and new records ({records.Count} records)");
        SameState(StateOf(updates), StateOf(records.Select(r => r.UpdateV2)), "after an interrupted compaction");
    }

    private async Task ATornLastRecordReadsAsAbsent(CancellationToken ct)
    {
        var (store, updates, _) = await Seeded(14, 6, ct).ConfigureAwait(false);
        var torn = Updates(15, 7)[6];
        if (!await TryTornAppendAsync(store, Doc1, torn, ct).ConfigureAwait(false))
            throw new NotSupportedCheck(nameof(TryTornAppendAsync));
        store = await RestartAsync(store, ct).ConfigureAwait(false);
        var records = await ReadAll(store, Doc1, 0, ct).ConfigureAwait(false);
        SameSequences(Enumerable.Range(1, 6).Select(i => (long)i), records.Select(r => r.Sequence), "after a torn append");
        SameState(StateOf(updates), StateOf(records.Select(r => r.UpdateV2)), "after a torn append");

        // the torn record was never acknowledged: the next append gets a fresh number and nothing torn shows up again
        var seq = await store.AppendAsync(Doc1, torn, ct).ConfigureAwait(false);
        Check(seq == 7, $"the append after a torn record got sequence {seq}, not 7");
        records = await ReadAll(store, Doc1, 0, ct).ConfigureAwait(false);
        SameSequences(Enumerable.Range(1, 7).Select(i => (long)i), records.Select(r => r.Sequence), "after appending over a torn record");
        Check(records[6].UpdateV2.Span.SequenceEqual(torn), "the record appended over a torn one does not hold its bytes");
    }

    private async Task ACorruptedRecordBeforeIntactOnesFailsTheRead(CancellationToken ct)
    {
        var (store, _, _) = await Seeded(16, 6, ct).ConfigureAwait(false);
        if (!await TryCorruptRecordAsync(store, Doc1, 3, ct).ConfigureAwait(false))
            throw new NotSupportedCheck(nameof(TryCorruptRecordAsync));
        store = await RestartAsync(store, ct).ConfigureAwait(false);
        var failed = false;
        try
        {
            await ReadAll(store, Doc1, 0, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            failed = true;
        }

        Check(failed, "a read over a corrupted record followed by intact ones succeeded instead of failing");
    }

    private async Task ReplayingTheLogTwiceIsIdempotent(CancellationToken ct)
    {
        var (store, updates, _) = await Seeded(17, 12, ct).ConfigureAwait(false);
        await store.CompactAsync(Doc1, 6, Merge(updates.Take(6)), ct).ConfigureAwait(false);
        var records = await ReadAll(store, Doc1, 0, ct).ConfigureAwait(false);
        SameState(StateOf(updates), StateOf(records.Concat(records).Select(r => r.UpdateV2)), "the log replayed twice");
        var log = new UpdateLog(store);
        var relay = (await log.LoadAsync(Doc1, ct).ConfigureAwait(false)).Relay;
        await log.FoldAsync(Doc1, 0, relay, ct).ConfigureAwait(false);
        SameState(StateOf(updates), StateOf(new[] { relay.EncodeStateAsUpdate(default, UpdateFormat.V2) }), "a relay that folded the log twice");
    }

    private async Task ConcurrentAppendsToOneDocumentGetDistinctSequences(CancellationToken ct)
    {
        var store = await CreateAsync(ct).ConfigureAwait(false);
        var updates = Updates(18, 64);
        var seqs = await Task.WhenAll(updates.Select(u => Task.Run(async () => await store.AppendAsync(Doc1, u, ct).ConfigureAwait(false), ct))).ConfigureAwait(false);
        SameSequences(Enumerable.Range(1, updates.Count).Select(i => (long)i), seqs.OrderBy(s => s), "concurrent appends");
        var records = await ReadAll(store, Doc1, 0, ct).ConfigureAwait(false);
        var bySeq = seqs.Zip(updates).ToDictionary(p => p.First, p => p.Second);
        Check(records.All(r => r.UpdateV2.Span.SequenceEqual(bySeq[r.Sequence])), "a record does not hold the bytes of the append that got its sequence");
    }

    private async Task ConcurrentDocumentsStayIndependent(CancellationToken ct)
    {
        var store = await CreateAsync(ct).ConfigureAwait(false);
        var docs = Enumerable.Range(0, 8).Select(i => ($"conformance-concurrent-{i}", Updates(100 + i, 20))).ToList();
        await Task.WhenAll(docs.Select(d => Task.Run(async () =>
        {
            var (id, updates) = d;
            for (var i = 0; i < updates.Count; i++)
            {
                var seq = await store.AppendAsync(id, updates[i], ct).ConfigureAwait(false);
                Check(seq == i + 1, $"{id}: append {i + 1} got sequence {seq}");
                if (i == 9) await store.CompactAsync(id, 10, Merge(updates.Take(10)), ct).ConfigureAwait(false);
            }
        }, ct))).ConfigureAwait(false);
        foreach (var (id, updates) in docs)
            SameState(StateOf(updates), await StoredState(store, id, ct).ConfigureAwait(false), id);
    }
}

/// <summary>The conformance suite over <see cref="InMemoryUpdateStore"/>, which has no durable medium: a restart returns the same store.</summary>
public sealed class InMemoryUpdateStoreConformance : UpdateStoreConformance
{
    protected override ValueTask<IUpdateStore> CreateAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IUpdateStore>(new InMemoryUpdateStore());

    protected override ValueTask<IUpdateStore> RestartAsync(IUpdateStore store, CancellationToken cancellationToken) => ValueTask.FromResult(store);
}
