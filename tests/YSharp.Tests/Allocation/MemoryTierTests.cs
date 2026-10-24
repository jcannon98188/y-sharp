using System;
using System.Collections.Generic;
using Xunit;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Allocation;

/// <summary>
/// The slow memory tier of ADR 0009 and 0018: bytes per item on three large workloads against the Yjs figure, and the steady-state check
/// (zero gen2 collections, no large-object-heap growth). Runs only when <c>YSHARP_TIER=Nightly</c>, in Release, in the serial "Allocation"
/// collection. Each test writes its measured numbers to <c>TestResults/memory/</c> and the test output, so the counts are reported
/// even when the budget holds. Half of Yjs's figure is a goal, not an assertion; pause time is not measured here.
/// </summary>
[Collection("Allocation")]
[Tier(Tier.Nightly)]
public sealed class MemoryTierTests
{
    private readonly ITestOutputHelper _out;

    public MemoryTierTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> Workloads() => new[]
    {
        new object[] { AllocationBudgets.TextInsert },
        new object[] { AllocationBudgets.ArrayPush },
        new object[] { AllocationBudgets.MapSet },
    };

    private static void GateSlowTier()
    {
#if DEBUG
        Assert.Skip("Memory measurements run only in Release (ADR 0009).");
#endif
        if (YSharpEnv.CurrentTier != Tier.Nightly) Assert.Skip("The slow memory tier belongs to the Nightly tier (YSHARP_TIER=Nightly).");
    }

    private static long HeapAfterCollect()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    [Theory, Covers("BUD-13")]
    [MemberData(nameof(Workloads))]
    public void BytesPerItem_DoesNotExceedYjs(string workload)
    {
        GateSlowTier();
        Stub.Run(() =>
        {
            var yjs = MemoryWorkloads.YjsBytesPerItem(workload)
                      ?? throw new InvalidOperationException("bench/yjs-memory/yjs-bytes-per-item.json is missing; run node --expose-gc bench/yjs-memory/measure.mjs");
            var items = MemoryWorkloads.ItemsOf(workload);

            var before = HeapAfterCollect();
            var doc = MemoryWorkloads.Run(workload);
            var after = HeapAfterCollect();
            GC.KeepAlive(doc);

            var perItem = (after - before) / (double)items;
            _out.WriteLine($"BUD-13 {workload}: {perItem:F2} bytes/item over {items} items; Yjs {yjs:F2} (goal: half = {yjs / 2:F2})");
            MemoryWorkloads.WriteReport("bytes-per-item-" + workload, new Dictionary<string, object>
            {
                ["workload"] = workload, ["items"] = items, ["bytesPerItem"] = Math.Round(perItem, 2), ["yjsBytesPerItem"] = yjs,
                ["goalBytesPerItem"] = yjs / 2, ["withinCeiling"] = perItem <= yjs,
            });
            Assert.True(perItem <= yjs, $"BUD-13: {workload} uses {perItem:F2} bytes per item, Yjs used {yjs:F2} on the same workload.");
        });
    }

    [Fact, Covers("BUD-14")]
    public void SteadyStateEdits_CauseNoGen2Collections_AndNoLohGrowth()
    {
        GateSlowTier();
        Stub.Run(() =>
        {
            const int edits = 100_000;
            var doc = MemoryWorkloads.SteadyStateStart();
            MemoryWorkloads.SteadyStateEdits(doc, 2_000);   // reach a steady working set before measuring

            HeapAfterCollect();
            var lohBefore = GC.GetGCMemoryInfo(GCKind.FullBlocking).GenerationInfo[3].SizeAfterBytes;
            var gen2Before = GC.CollectionCount(2);

            MemoryWorkloads.SteadyStateEdits(doc, edits);

            var gen2 = GC.CollectionCount(2) - gen2Before;
            HeapAfterCollect();
            var lohGrowth = GC.GetGCMemoryInfo(GCKind.FullBlocking).GenerationInfo[3].SizeAfterBytes - lohBefore;
            GC.KeepAlive(doc);

            _out.WriteLine($"BUD-14: {gen2} gen2 collections and {lohGrowth} bytes of LOH growth over {edits} edits (allowance {AllocationBudgets.LohAllowanceBytes})");
            MemoryWorkloads.WriteReport("steady-state", new Dictionary<string, object>
            {
                ["edits"] = edits, ["gen2Collections"] = gen2, ["lohGrowthBytes"] = lohGrowth, ["lohAllowanceBytes"] = AllocationBudgets.LohAllowanceBytes,
            });
            Assert.True(gen2 == 0, $"BUD-14: {gen2} gen2 collections during {edits} steady-state edits; the budget is zero.");
            Assert.True(lohGrowth <= AllocationBudgets.LohAllowanceBytes, $"BUD-14: the large object heap grew by {lohGrowth} bytes; the allowance is {AllocationBudgets.LohAllowanceBytes}.");
        });
    }
}
