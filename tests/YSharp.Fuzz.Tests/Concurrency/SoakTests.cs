using System;
using System.Diagnostics;
using Xunit;
using YSharp.TestKit.Concurrency;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;

namespace YSharp.Fuzz.Concurrency;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SoakCollection
{
    public const string Name = "Concurrency soak";
}

/// <summary>
/// The nightly soak of ADR 0006: the stress workloads of the gating tier with random seeds for a time box
/// (<c>YSHARP_SOAK_SECONDS</c>, default 20 per test), one thread and more threads than cores, plus the schedule scenarios with thousands of
/// random seeds (the non-gating systematic exploration). Runs only when <c>YSHARP_TIER=Nightly</c>. The start seed is printed in every
/// failure; <c>YSHARP_SEED</c> pins it. A single failure is a real bug: no retry.
/// </summary>
[Collection(SoakCollection.Name)]
[Tier(Tier.Nightly)]
public sealed class SoakTests
{
    private static double Seconds => double.TryParse(Environment.GetEnvironmentVariable("YSHARP_SOAK_SECONDS"), out var s) && s > 0 ? s : 20;

    private static uint StartSeed => YSharpEnv.Seed ?? (uint)Environment.TickCount64;

    private static void Soak(Func<StressOptions, StressResult> run, int writers, int readers, bool implicitTx = false, int ops = 400)
    {
        NightlyGate.Require();
        Stub.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            var seed = StartSeed;
            do
            {
                run(new StressOptions { Seed = seed++, Writers = writers, Readers = readers, Implicit = implicitTx, OpsPerThread = ops }).AssertClean();
            } while (clock.Elapsed.TotalSeconds < Seconds);
        });
    }

    [Fact, Covers("CC-1", "CC-11")]
    public void Serializability_OneThread_HoldsUnderSoak() => Soak(StressHarness.RunSharedDoc, 1, 0);

    [Fact, Covers("CC-1", "CC-11")]
    public void SerializabilityAndTornReads_SoakWithMoreThreadsThanCores_MoreThreadsThanCores() => Soak(StressHarness.RunSharedDoc, StressOptions.Oversubscribed, 4);

    [Fact, Covers("CC-8")]
    public void ImplicitTransactions_MoreThreadsThanCores_StaySerializable() => Soak(StressHarness.RunSharedDoc, StressOptions.Oversubscribed, 4, implicitTx: true);

    [Fact, Covers("CC-4")]
    public void ReadPurity_MoreThreadsThanCores_HoldsUnderSoak() => Soak(StressHarness.RunReadPurity, 0, StressOptions.Oversubscribed);

    [Fact, Covers("CC-8", "CC-11")]
    public void Convergence_MoreThreadsThanCores_HoldsUnderSoak() => Soak(StressHarness.RunConvergence, Math.Min(8, StressOptions.Oversubscribed), 0, ops: 150);

    [Fact, Covers("CC-2", "CC-3", "CC-12", "CC-17", "CC-18", "CC-19", "CC-20")]
    public void ScheduleScenarios_Soak_WithManyRandomSeeds()
    {
        NightlyGate.Require();
        Stub.Run(() =>
        {
            var start = StartSeed;
            foreach (var scenario in Scenarios.All)
            {
                var clock = Stopwatch.StartNew();
                for (uint i = 0; (clock.Elapsed.TotalSeconds < Seconds / 2 || i < 200) && i <= 20000; i++)
                    Explorer.RunSeeds(scenario, new[] { start + i });
            }
        });
    }
}
