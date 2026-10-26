using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YSharp.Server.Testing;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Server.Tests.Storage;

/// <summary>
/// The default backend and the storage conformance suite (ADR 0024 SRV-12; TASK-5.5): the in-memory store is the default and passes every
/// check it can run, a framed store with crash hooks passes every check including the torn-write, corruption and interrupted-compaction ones,
/// and a store with each kind of contract break fails the check written for it.
/// </summary>
[Requires(Feature.UpdateFunctions, Feature.V2, Feature.V2Functions, Feature.Text, Feature.Map, Feature.Events)]
public sealed class StoreConformanceTests
{
    private static readonly string[] HookChecks =
        { "AnInterruptedCompactionLeavesTheOldRecords", "ATornLastRecordReadsAsAbsent", "ACorruptedRecordBeforeIntactOnesFailsTheRead" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static IEnumerable<object[]> Checks() => new InMemoryUpdateStoreConformance().CheckNames.Select(n => new object[] { n });

    [Theory, Covers("SRV-12")]
    [MemberData(nameof(Checks))]
    public async Task TheInMemoryStore_PassesEveryCheckItCanRun(string check)
    {
        var result = await new InMemoryUpdateStoreConformance().RunAsync(check, Ct);
        var expected = HookChecks.Contains(check) ? ConformanceOutcome.NotSupported : ConformanceOutcome.Passed;
        Assert.True(result.Outcome == expected, result.ToString());
    }

    [Theory, Covers("SRV-12")]
    [MemberData(nameof(Checks))]
    public async Task AStoreWithCrashHooks_PassesEveryCheck(string check)
    {
        var result = await new FramedConformance().RunAsync(check, Ct);
        Assert.True(result.Outcome == ConformanceOutcome.Passed, result.ToString());
    }

    [Theory, Covers("SRV-12")]
    [InlineData(FramedTestStore.Defect.SkipsCorruptRecords, "ACorruptedRecordBeforeIntactOnesFailsTheRead")]
    [InlineData(FramedTestStore.Defect.DropsLaterRecordsOnCompaction, "CompactionReplacesCoveredRecordsAndKeepsLaterOnes")]
    [InlineData(FramedTestStore.Defect.KeepsTheCallersBuffer, "ReadsReturnExactBytesInOrder")]
    [InlineData(FramedTestStore.Defect.ReusesSequencesAfterCompaction, "SequencesAreNotReusedAfterCompaction")]
    public async Task AStoreThatBreaksTheContract_FailsTheCheckForIt(FramedTestStore.Defect defect, string check)
    {
        var result = await new FramedConformance(defect).RunAsync(check, Ct);
        Assert.True(result.Outcome == ConformanceOutcome.Failed, result.ToString());
    }

    [Fact, Covers("SRV-12")]
    public async Task RunningTheWholeSuite_GivesOneResultPerCheck()
    {
        var suite = new InMemoryUpdateStoreConformance();
        var results = await suite.RunAsync(Ct);
        Assert.Equal(suite.CheckNames, results.Select(r => r.Check));
        Assert.Equal(HookChecks.Order(), results.Where(r => r.Outcome == ConformanceOutcome.NotSupported).Select(r => r.Check).Order());
        Assert.DoesNotContain(results, r => r.Outcome == ConformanceOutcome.Failed);
        await Assert.ThrowsAsync<ArgumentException>(() => suite.RunAsync("NoSuchCheck", Ct));
    }

    [Fact, Covers("SRV-12")]
    public void TheDefaultStore_IsInMemory_AndOneRegistrationReplacesIt()
    {
        var defaults = new YServerOptions();
        Assert.True(defaults.UsesDefaultStore);
        Assert.IsType<InMemoryUpdateStore>(defaults.Store);
        Assert.Same(defaults.Store, defaults.Store);

        var mine = new FramedTestStore(new FramedTestStore.Disk());
        var hosted = new YServerOptions { Store = mine };
        Assert.Same(mine, hosted.Store);
        Assert.False(hosted.UsesDefaultStore);
        Assert.Throws<ArgumentNullException>(() => new YServerOptions { Store = null! });
    }

    [Fact, Covers("SRV-12")]
    public async Task OnTheInMemoryStore_AnOpenReadBlocksNoAppend_OnItsDocumentOrAnother()
    {
        var store = new InMemoryUpdateStore();
        var update = Hex("0101010004010174016100");
        await store.AppendAsync("a", update, Ct);
        await store.AppendAsync("a", update, Ct);
        await using var reader = store.ReadSinceAsync("a", 0, Ct).GetAsyncEnumerator(Ct);
        Assert.True(await reader.MoveNextAsync());

        // the read is paused partway; appends and a compaction still complete at once, and the read goes on with the view it started with
        Assert.True(store.AppendAsync("a", update, Ct).IsCompletedSuccessfully);
        Assert.True(store.AppendAsync("b", update, Ct).IsCompletedSuccessfully);
        Assert.True(store.CompactAsync("a", 3, update, Ct).IsCompletedSuccessfully);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(2, reader.Current.Sequence);
        Assert.False(await reader.MoveNextAsync());
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private sealed class FramedConformance(FramedTestStore.Defect defect = FramedTestStore.Defect.None) : UpdateStoreConformance
    {
        protected override ValueTask<IUpdateStore> CreateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IUpdateStore>(new FramedTestStore(new FramedTestStore.Disk(), defect));

        // a new process over the same disk: nothing in memory survives
        protected override ValueTask<IUpdateStore> RestartAsync(IUpdateStore store, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IUpdateStore>(new FramedTestStore(((FramedTestStore)store).Medium, defect));

        protected override ValueTask<bool> TryTornAppendAsync(IUpdateStore store, string docId, ReadOnlyMemory<byte> updateV2, CancellationToken cancellationToken)
        {
            ((FramedTestStore)store).TornAppend(docId, updateV2.Span);
            return ValueTask.FromResult(true);
        }

        protected override ValueTask<bool> TryCorruptRecordAsync(IUpdateStore store, string docId, long sequence, CancellationToken cancellationToken) =>
            ValueTask.FromResult(((FramedTestStore)store).Corrupt(docId, sequence));

        protected override ValueTask<bool> TryInterruptedCompactionAsync(IUpdateStore store, string docId, long uptoSequence, ReadOnlyMemory<byte> mergedV2, CancellationToken cancellationToken)
        {
            ((FramedTestStore)store).InterruptedCompaction(docId, uptoSequence, mergedV2.Span);
            return ValueTask.FromResult(true);
        }
    }
}
