using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;

namespace YSharp.StoreSpike.Harness;

/// <summary>One measured run (or the median of three). Field names are the report keys of ADR 0018's spike table.</summary>
public sealed record WorkloadMeasurement(
    string Candidate,
    string Workload,
    int Items,
    string Configuration,
    bool ServerGc,
    double BytesPerOp,
    double StructsPerOp,
    long Gen2Collections,
    long LohGrowthBytes,
    double OpsPerSecond)
{
    /// <summary>Median of several runs of the same candidate and workload, field by field.</summary>
    public static WorkloadMeasurement Median(IReadOnlyList<WorkloadMeasurement> runs)
    {
        if (runs.Count == 0) throw new ArgumentException("no runs to take the median of", nameof(runs));
        var first = runs[0];
        return first with
        {
            BytesPerOp = MedianOf(runs.Select(r => r.BytesPerOp)),
            StructsPerOp = MedianOf(runs.Select(r => r.StructsPerOp)),
            Gen2Collections = (long)MedianOf(runs.Select(r => (double)r.Gen2Collections)),
            LohGrowthBytes = (long)MedianOf(runs.Select(r => (double)r.LohGrowthBytes)),
            OpsPerSecond = MedianOf(runs.Select(r => r.OpsPerSecond)),
        };
    }

    public static double MedianOf(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}

/// <summary>
/// Measures one workload on one candidate, using the method of <c>bench/yjs-memory/measure.mjs</c>: force two collections, read the heap, build, force two
/// collections, read again, and divide by the operation count. Numbers are meaningful only in Release (ADR 0009); the record says which configuration ran.
/// </summary>
public static class SpikeHarness
{
    private const string ConfigurationName =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    public static WorkloadMeasurement MeasureWorkload(string candidate, Func<IStoreDriver> create, string workload, int items)
    {
        var before = HeapAfterCollect();
        var lohBefore = LohBytes();
        var gen2Before = GC.CollectionCount(2);

        var driver = create();
        var clock = Stopwatch.StartNew();
        SpikeWorkloads.Build(driver, workload, items);
        clock.Stop();

        var gen2 = GC.CollectionCount(2) - gen2Before;
        var after = HeapAfterCollect();
        var lohAfter = LohBytes();
        var structs = driver.Stats.StructCount;
        GC.KeepAlive(driver);

        return Record(candidate, workload, items, (after - before) / (double)items, structs / (double)items, gen2, lohAfter - lohBefore,
            items / clock.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// The steady-state check of ADR 0018 and MemoryTierTests: build the working set, warm up, then count gen2 collections and large-object-heap growth over
    /// <paramref name="edits"/> edits.
    /// </summary>
    public static WorkloadMeasurement MeasureSteadyState(
        string candidate,
        Func<IStoreDriver> create,
        int edits = SpikeWorkloads.SteadyEdits,
        int warmup = SpikeWorkloads.SteadyWarmupEdits,
        int startChars = SpikeWorkloads.SteadyStartChars)
    {
        var driver = create();
        SpikeWorkloads.SteadyStateStart(driver, startChars);
        SpikeWorkloads.SteadyStateEdits(driver, warmup);

        var before = HeapAfterCollect();
        var lohBefore = LohBytes();
        var gen2Before = GC.CollectionCount(2);
        var structsBefore = driver.Stats.StructCount;

        var clock = Stopwatch.StartNew();
        SpikeWorkloads.SteadyStateEdits(driver, edits);
        clock.Stop();

        var gen2 = GC.CollectionCount(2) - gen2Before;
        var after = HeapAfterCollect();
        var lohAfter = LohBytes();
        var structs = driver.Stats.StructCount - structsBefore;
        GC.KeepAlive(driver);

        return Record(candidate, SpikeWorkloads.SteadyState, edits, (after - before) / (double)edits, structs / (double)edits, gen2,
            lohAfter - lohBefore, edits / clock.Elapsed.TotalSeconds);
    }

    /// <summary>Forces two full collections and returns the managed heap size, as measure.mjs does.</summary>
    public static long HeapAfterCollect()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>Bytes in the large object heap after the last full blocking collection.</summary>
    public static long LohBytes() => GC.GetGCMemoryInfo(GCKind.FullBlocking).GenerationInfo[3].SizeAfterBytes;

    private static WorkloadMeasurement Record(string candidate, string workload, int items, double bytesPerOp, double structsPerOp, long gen2, long lohGrowth,
        double opsPerSecond) =>
        new(candidate, workload, items, ConfigurationName, GCSettings.IsServerGC, bytesPerOp, structsPerOp, gen2, lohGrowth, opsPerSecond);
}

/// <summary>
/// Candidate registry and the one-process-per-workload runner. The runner launches the spike assembly with <c>measure</c> for each run, so every
/// workload gets a fresh process, and takes the median of <see cref="Runs"/> runs.
/// </summary>
public static class SpikeCandidates
{
    public const string Control = "control";

    public static Func<IStoreDriver> Factory(string candidate) => candidate switch
    {
        Control => () => new ControlStore(),
        _ => throw new ArgumentException("unknown candidate " + candidate, nameof(candidate)),
    };
}

public static class SpikeProcess
{
    public const int Runs = 3;

    /// <summary>The in-process measurement the child entry point runs for one candidate and workload.</summary>
    public static WorkloadMeasurement MeasureChild(string candidate, string workload)
    {
        var create = SpikeCandidates.Factory(candidate);
        return workload == SpikeWorkloads.SteadyState
            ? SpikeHarness.MeasureSteadyState(candidate, create)
            : SpikeHarness.MeasureWorkload(candidate, create, workload, SpikeWorkloads.ItemsOf(workload));
    }

    /// <summary>Runs <see cref="Runs"/> fresh processes for one workload and returns their median.</summary>
    public static WorkloadMeasurement MedianOfRuns(string candidate, string workload)
    {
        var runs = new List<WorkloadMeasurement>(Runs);
        for (var i = 0; i < Runs; i++) runs.Add(RunChild(candidate, workload));
        return WorkloadMeasurement.Median(runs);
    }

    private static WorkloadMeasurement RunChild(string candidate, string workload)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(SpikeProcess).Assembly.Location);
        start.ArgumentList.Add("measure");
        start.ArgumentList.Add(candidate);
        start.ArgumentList.Add(workload);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start the measurement process");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"measurement process failed ({process.ExitCode}): {stderr}");

        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
        return System.Text.Json.JsonSerializer.Deserialize<WorkloadMeasurement>(line) ?? throw new InvalidOperationException("empty measurement");
    }
}
