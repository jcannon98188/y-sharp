using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using Xunit;

namespace YSharp.TestKit.Allocation;

/// <summary>
/// Exact allocation assertions (ADR 0009): <see cref="GC.GetAllocatedBytesForCurrentThread"/> around a warmed-up loop.
/// Release builds only; in Debug the calling test is skipped visibly. A failed budget reruns the operation under an
/// <see cref="EventListener"/> on the runtime GC allocation-tick event to name the allocating types.
/// The test projects set <c>System.Runtime.TieredCompilation</c> off in <c>runtimeconfig.template.json</c> (ADR 0009) so JIT tiering and escape analysis behave the same on every run.
/// </summary>
public static class AllocationAssert
{
    /// <summary>Average bytes allocated per call of <paramref name="operation"/> on the current thread, after warm-up.</summary>
    public static long Measure(Action operation, int iterations = 200, int warmup = 50)
    {
        SkipUnlessRelease();
        return MeasureCore(operation, iterations, warmup);
    }

    /// <summary>Fails when one call allocates more than <paramref name="budgetBytes"/> (zero paths use 0).</summary>
    /// <param name="budgetId">Stable ID from the <c>AllocationBudgets</c> table, named in the failure.</param>
    public static void AtMost(string budgetId, long budgetBytes, Action operation, int iterations = 200, int warmup = 50)
    {
        SkipUnlessRelease();
        var perOp = MeasureCore(operation, iterations, warmup);
        if (perOp <= budgetBytes) return;

        var types = DescribeAllocators(operation, perOp);
        Assert.Fail($"{budgetId}: allocated {perOp} bytes per operation, budget is {budgetBytes}. Allocating types: {types}");
    }

    private static void SkipUnlessRelease()
    {
#if DEBUG
        Assert.Skip("Allocation tests run only in Release (ADR 0009).");
#endif
    }

    private static long MeasureCore(Action operation, int iterations, int warmup)
    {
        for (var i = 0; i < warmup; i++) operation();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) operation();
        var after = GC.GetAllocatedBytesForCurrentThread();
        var total = after - before;
        // round up so a single stray allocation in the loop is never averaged away
        return (total + iterations - 1) / iterations;
    }

    private static string DescribeAllocators(Action operation, long perOp)
    {
        using var listener = new AllocationTickListener();
        // Allocation ticks fire about every 100 KB; run enough iterations to see small allocators.
        var runs = (int)Math.Clamp(2_000_000 / Math.Max(1, perOp), 1000, 2_000_000);
        for (var i = 0; i < runs; i++) operation();
        Thread.Sleep(500);
        var top = listener.Counts.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} x{kv.Value}").ToArray();
        return top.Length == 0 ? "(no allocation ticks observed)" : string.Join(", ", top);
    }

    private sealed class AllocationTickListener : EventListener
    {
        public ConcurrentDictionary<string, int> Counts { get; } = new();

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1 /* GC */);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal)) return;
            var i = e.PayloadNames?.IndexOf("TypeName") ?? -1;
            if (i < 0 || e.Payload == null) return;
            var name = e.Payload[i]?.ToString();
            if (!string.IsNullOrEmpty(name)) Counts.AddOrUpdate(name, 1, (_, c) => c + 1);
        }
    }
}
