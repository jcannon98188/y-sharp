using System;
using System.Linq;
using Xunit;
using YSharp.TestKit.Concurrency;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Concurrency;

/// <summary>
/// The systematic-exploration scenarios of ADR 0006 under the hand-rolled scheduler (<see cref="CooperativeScheduler"/>), small enough to run
/// in the gating tier: <see cref="Explorer.GatingSeedCount"/> fixed seeds each (seeds 1 to 40), and every schedule of the tiny ones by
/// depth-first search. A failure names the scenario and seed and prints the trace; <c>YSHARP_SEED=n</c> replays it. The same scenarios run
/// with thousands of random seeds in the nightly tier (<c>YSharp.Fuzz.Tests</c>). They need the schedule seam in the implementation
/// (<see cref="ScheduleSeam"/>); until the implementation exists they skip with the rest.
/// </summary>
[Requires(Feature.Concurrency)]
[Collection(ConcurrencyCollection.Name)]
[Tier(Tier.Gating)]
public sealed class ScheduleScenarioTests
{
    private static void Seeds(Scenario s)=> Explorer.RunSeeds(s, Conc.Seeds);

    [Fact, Covers("CC-1", "CC-2")]
    public void WriterPreference_ReaderArrivesAfterAWaitingWriter_QueuesBehindIt() => Seeds(Scenarios.WriterPreference);

    [Fact, Covers("CC-1", "CC-2")]
    public void NestedWrites_AnotherThreadsTransaction_NeverOverlapsAndNestedOnesJoin() => Seeds(Scenarios.NestedJoinAndExclusion);

    [Fact, Covers("CC-3")]
    public void TheUpgradeIsRejected_InEverySchedule_AndNeverWaitsForItself() => Seeds(Scenarios.UpgradeIsRejected);

    [Fact, Covers("CC-12", "CC-13")]
    public void SubscribeAndUnsubscribe_RaceACommit_WithoutTornEventsOrLateCalls() => Seeds(Scenarios.SubscribeVsCommit);

    [Fact, Covers("CC-17")]
    public void Destroy_RacesAWrite_BothCompleteAndTheDocumentStillAcceptsAcquires() => Seeds(Scenarios.DestroyVsWrite);

    [Fact, Covers("CC-18")]
    [Requires(Feature.Subdocs)]
    public void TheSubdocHierarchy_ParentBeforeChild_NeverDeadlocks() => Seeds(Scenarios.SubdocHierarchy);

    [Fact, Covers("CC-18")]
    [Requires(Feature.Subdocs)]
    public void TheAbbaCase_ChildThenParent_IsRejectedInEverySchedule_NotAHang() => Seeds(Scenarios.SubdocAbba);

    [Fact, Covers("CC-19", "CC-11")]
    public void TheAwarenessTimer_RacesSetLocalState_WithConsistentStatesAndNoDocLock() => Seeds(Scenarios.AwarenessTimerVsSetState);

    [Fact, Covers("CC-20")]
    public void Undo_RacesAnOrdinaryWrite_ConvergesInEitherOrder() => Seeds(Scenarios.UndoVsWrite);

    [Fact, Covers("CC-3", "CC-17", "CC-2")]
    public void TinyScenarios_AreExploredExhaustively_EveryScheduleOnce()
    {
        foreach (var scenario in Scenarios.Exhaustive)
        {
            var ran = Explorer.RunDfs(scenario, max: 400, out var exhausted);
            Assert.True(ran >= 1);
            if (!exhausted)
                Assert.Fail($"{scenario.Name}: not exhausted after {ran} schedules; shrink the scenario or raise the cap.");
        }
    }

    [Fact, Covers("CC-1")]
    [LibraryIndependent]
    public void Scenarios_EveryContractGuaranteeWithAScenario_IsListed()
    {
        // the scenario table is the index the matrix is built from; a scenario that names no CC id would be invisible there
        Assert.All(Scenarios.All, s => Assert.NotEmpty(s.Covers));
        var named = Scenarios.All.SelectMany(s => s.Covers).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "CC-1", "CC-11", "CC-12", "CC-13", "CC-17", "CC-18", "CC-19", "CC-2", "CC-20", "CC-3" }, named);
    }
}
