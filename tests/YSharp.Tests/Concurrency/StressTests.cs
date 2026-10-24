using System;
using Xunit;
using YSharp.TestKit.Concurrency;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Concurrency;

/// <summary>
/// The gating stress tier of ADR 0006: a short run (a second or two each), a fixed seed (12345; <c>YSHARP_SEED</c> replays another), capped
/// thread counts, and both one thread and more threads than cores. The long random-seed soak is in <c>YSharp.Fuzz.Tests</c>. Every failure
/// prints the seed and each thread's operation log. A single failure is a real bug: no retry, no quarantine.
/// </summary>
[Collection(ConcurrencyCollection.Name)]
[Tier(Tier.Gating)]
public sealed class StressTests
{
    private static uint Seed => YSharpEnv.Seed ?? 12345;

    private static StressOptions Opts(int writers, int readers, bool implicitTx = false, int ops = 150) =>
        new() { Seed = Seed, Writers = writers, Readers = readers, OpsPerThread = ops, Implicit = implicitTx };

    [Fact, Covers("CC-1", "CC-11")]
    public void Stress_OneWriterThread_CommitStreamReplaysToTheSameBytes() => Impl.Run(() => StressHarness.RunSharedDoc(Opts(1, 0)).AssertClean());

    [Fact, Covers("CC-1", "CC-11")]
    public void Stress_FewThreads_SerializabilityAndNoTornReads() => Impl.Run(() => StressHarness.RunSharedDoc(Opts(2, 2)).AssertClean());

    [Fact, Covers("CC-1", "CC-11")]
    public void Stress_MoreThreadsThanCores_SerializabilityAndNoTornReads() =>
        Impl.Run(() => StressHarness.RunSharedDoc(Opts(StressOptions.Oversubscribed, 2)).AssertClean());

    [Fact, Covers("CC-8", "CC-11")]
    public void ImplicitTransactions_FromManyThreads_SerializeToo() =>
        Impl.Run(() => StressHarness.RunSharedDoc(Opts(StressOptions.Oversubscribed, 2, implicitTx: true)).AssertClean());

    [Fact, Covers("CC-4")]
    public void ConcurrentReads_LeaveTheStateUnchanged_AndMatchASingleThreadedRead() =>
        Impl.Run(() => StressHarness.RunReadPurity(Opts(0, StressOptions.Oversubscribed)).AssertClean());

    [Fact, Covers("CC-8", "CC-11")]
    public void Stress_DocumentsSyncedAcrossThreads_Converge() => Impl.Run(() => StressHarness.RunConvergence(Opts(4, 0, ops: 80)).AssertClean());

    [Fact, Covers("CC-8")]
    public void Stress_DocumentsSyncedAcrossMoreThreadsThanCores_Converge() =>
        Impl.Run(() => StressHarness.RunConvergence(Opts(Math.Min(8, StressOptions.Oversubscribed), 0, ops: 60)).AssertClean());
}
