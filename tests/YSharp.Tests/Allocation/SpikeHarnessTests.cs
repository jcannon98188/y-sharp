using System;
using System.Collections.Generic;
using System.Text;
using Xunit;
using YSharp.Store;
using YSharp.StoreHarness;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Allocation;

/// <summary>
/// Self-tests of the M0 spike driver, workloads, harness and shared content layer (TASK-4.3). They do not measure budgets: the measurements run in
/// the Nightly tier, in Release. The content-layer and workload tests run in every configuration. The content layer and the control store are
/// built from the library's <c>Store/</c> module since the spike project was deleted (TASK-4.11), so the class requires the Store feature.
/// </summary>
[Requires(Feature.Store)]
public sealed class SpikeHarnessTests
{
    private const long ChunkCeiling = 64 * 1024;

    [Fact]
    public void Constants_MatchMemoryWorkloads()
    {
        Assert.Equal(MemoryWorkloads.Batch, SpikeWorkloads.Batch);
        Assert.Equal(MemoryWorkloads.TextInserts, SpikeWorkloads.TextInserts);
        Assert.Equal(MemoryWorkloads.ArrayPushes, SpikeWorkloads.ArrayPushes);
        Assert.Equal(MemoryWorkloads.MapKeys, SpikeWorkloads.MapKeys);
        Assert.Equal(AllocationBudgets.TextInsert, SpikeWorkloads.TextInsert);
        Assert.Equal(AllocationBudgets.ArrayPush, SpikeWorkloads.ArrayPush);
        Assert.Equal(AllocationBudgets.MapSet, SpikeWorkloads.MapSet);
        Assert.Equal(100_000, SpikeWorkloads.SteadyEdits);
    }

    [Fact]
    public void TextWorkload_MatchesMemoryWorkloadsPositions()
    {
        // Mirrors MemoryWorkloads.Text: each position is max(0, length - back), and the character is 'a' + j % 26.
        const int n = 5_000;
        var driver = new ControlStore();
        SpikeWorkloads.TypeText(driver, n);

        var expected = new StringBuilder();
        for (var j = 0; j < n; j++)
        {
            var index = Math.Max(0, expected.Length - (int)(unchecked((uint)j * 0x9E3779B1u) & 15));
            expected.Insert(index, (char)('a' + j % 26));
        }

        Assert.Equal(expected.ToString(), driver.Text());
        Assert.Equal(n, driver.TextLength);
    }

    [Fact]
    public void SteadyState_CommitsOnceForTheStartAndOnceForEachEdit()
    {
        var driver = new ControlStore();
        SpikeWorkloads.SteadyStateStart(driver, 1_000);
        Assert.Equal(1, driver.Stats.Commits);
        SpikeWorkloads.SteadyStateEdits(driver, 4_000);
        Assert.Equal(4_001, driver.Stats.Commits);
        Assert.Equal(1_000, driver.TextLength);
    }

    [Fact]
    public void ControlStore_DriverOperations_CountStructs()
    {
        var driver = new ControlStore();
        driver.InsertChar(0, 'a');
        driver.PushValue(1.5);
        driver.SetKey("k", 2);
        driver.SetKey("k", 3);      // an existing key reuses its payload: no new struct
        driver.DeleteAt(0);
        driver.Commit();

        Assert.Equal(3, driver.Stats.StructCount);
        Assert.Equal(1, driver.Stats.Commits);
        Assert.Equal(0, driver.TextLength);
        Assert.Equal(3.0, driver.ValueOfKey("k"));
    }

    [Fact]
    public void Measure_TextWorkload_ReportsOneStructPerInsert()
    {
        var m = SpikeHarness.MeasureWorkload(SpikeCandidates.Control, SpikeCandidates.Factory(SpikeCandidates.Control), SpikeWorkloads.TextInsert, 5_000);

        Assert.Equal(5_000, m.Items);
        Assert.Equal(1.0, m.StructsPerOp);
        Assert.True(m.Gen2Collections >= 0);
        Assert.True(m.OpsPerSecond > 0);
        Assert.Equal(SpikeCandidates.Control, m.Candidate);
    }

    [Fact]
    public void Measure_ArrayAndMapWorkloads_ReportOneStructPerOperation()
    {
        var array = SpikeHarness.MeasureWorkload(SpikeCandidates.Control, SpikeCandidates.Factory(SpikeCandidates.Control), SpikeWorkloads.ArrayPush, 3_000);
        var map = SpikeHarness.MeasureWorkload(SpikeCandidates.Control, SpikeCandidates.Factory(SpikeCandidates.Control), SpikeWorkloads.MapSet, 3_000);

        Assert.Equal(1.0, array.StructsPerOp);
        Assert.Equal(1.0, map.StructsPerOp);
    }

    [Fact]
    public void Measure_SteadyState_ReportsHalfAStructPerEdit()
    {
        // Inserts create a struct, deletes do not: over an even number of edits, half of them are inserts.
        var m = SpikeHarness.MeasureSteadyState(SpikeCandidates.Control, SpikeCandidates.Factory(SpikeCandidates.Control), edits: 4_000, warmup: 200, startChars: 1_000);

        Assert.Equal(4_000, m.Items);
        Assert.Equal(0.5, m.StructsPerOp);
        Assert.Equal(SpikeWorkloads.SteadyState, m.Workload);
    }

    [Fact]
    public void Median_OfThreeRuns_TakesTheMiddleValueOfEachField()
    {
        WorkloadMeasurement Run(double bytes, long gen2) => new("c", "w", 1, "Release", false, bytes, 1, gen2, 0, 1);

        var median = WorkloadMeasurement.Median(new[] { Run(10, 5), Run(30, 0), Run(20, 2) });

        Assert.Equal(20.0, median.BytesPerOp);
        Assert.Equal(2, median.Gen2Collections);
    }

    [Fact]
    public void Measurement_RoundTripsThroughTheChildProcessFormat()
    {
        var m = new WorkloadMeasurement("control", SpikeWorkloads.TextInsert, 10, "Release", false, 1.25, 1.0, 3, 0, 12.5);
        var json = System.Text.Json.JsonSerializer.Serialize(m);

        Assert.Equal(m, System.Text.Json.JsonSerializer.Deserialize<WorkloadMeasurement>(json));
    }

    [Fact]
    public void ChunkedList_RandomInsertsAndRemoves_MatchAListModel()
    {
        // Enough elements to cross several block boundaries (16384 ints per block).
        var random = new Random(4321);
        var list = new ChunkedList<int>();
        var model = new List<int>();
        for (var i = 0; i < 60_000; i++)
        {
            var index = random.Next(model.Count + 1);
            list.Insert(index, i);
            model.Insert(index, i);
        }

        for (var i = 0; i < 40_000; i++)
        {
            if (model.Count > 0 && random.Next(3) == 0)
            {
                var index = random.Next(model.Count);
                list.RemoveAt(index);
                model.RemoveAt(index);
            }
            else
            {
                var index = random.Next(model.Count + 1);
                list.Insert(index, -i);
                model.Insert(index, -i);
            }
        }

        Assert.Equal(model.Count, list.Count);
        for (var i = 0; i < model.Count; i++) Assert.Equal(model[i], list[i]);
        Assert.True(list.MaxBlockBytes <= ChunkCeiling);
    }

    [Fact]
    public void TextBuffer_LongRun_EveryChunkAtOrBelow64KB_AndSlicesReadBack()
    {
        var buffer = new TextBuffer();
        var slices = new List<(int Address, char Value)>();
        var one = new char[1];
        for (var i = 0; i < 300_000; i++)
        {
            var c = (char)('a' + i % 26);
            one[0] = c;
            slices.Add((buffer.Append(one), c));
        }

        Assert.True(buffer.ChunkCount > 1);
        Assert.True(buffer.MaxChunkBytes <= ChunkCeiling);
        Assert.All(slices, s => Assert.Equal(s.Value, buffer.CharAt(s.Address)));
    }

    [Fact]
    public void TextBuffer_AnAppendLongerThanAChunk_SpansChunks_AndReadsBack()
    {
        // The spike rejected such appends; the store's buffer is one address space, so a long string continues into the next chunk.
        var buffer = new TextBuffer();
        buffer.Append("ab");
        var longText = new string('x', TextBuffer.ChunkChars) + "yz";

        var address = buffer.Append(longText);

        Assert.Equal(2, address);
        Assert.Equal(2, buffer.ChunkCount);
        Assert.True(buffer.MaxChunkBytes <= ChunkCeiling);
        Assert.Equal(longText, buffer.GetString(address, longText.Length));
        Assert.False(buffer.TryGetSpan(address, longText.Length, out _));
    }

    [Fact]
    public void KeyTableAndValueColumn_ManyKeys_KeepValuesAndKeysAndStayInChunks()
    {
        var keys = new KeyTable();
        var values = new ValueColumn();
        const int count = 100_000;
        for (var i = 0; i < count; i++)
        {
            keys.Add(("k" + i).AsSpan());
            values.Append(i);
        }

        Assert.Equal(count, keys.Count);
        Assert.Equal("k99999", new string(keys.Get(99_999)));
        Assert.Equal(12_345.0, values.Get(12_345).AsNumber());
        Assert.True(keys.MaxChunkBytes <= ChunkCeiling);
        Assert.True(values.MaxChunkBytes <= ChunkCeiling);
    }

    [Fact]
    public void ControlStore_EveryChunk_AtOrBelow64KB_AfterSlowTierShapedWork()
    {
        var driver = new ControlStore();
        SpikeWorkloads.Build(driver, SpikeWorkloads.TextInsert, 200_000);
        SpikeWorkloads.Build(driver, SpikeWorkloads.ArrayPush, 200_000);
        SpikeWorkloads.Build(driver, SpikeWorkloads.MapSet, 20_000);

        Assert.True(driver.MaxChunkBytes <= ChunkCeiling);
    }

    [Fact]
    public void Candidates_UnknownId_IsRejected() => Assert.Throws<ArgumentException>(() => SpikeCandidates.Factory("no-such-candidate"));

    [Tier(Tier.Nightly)]
    [Fact]
    public void Process_OneWorkloadRunsThreeFreshProcesses_AndReportsTheMedian()
    {
#if DEBUG
        Assert.Skip("The spike measurements run only in Release (ADR 0009).");
#endif
        if (YSharpEnv.CurrentTier != Tier.Nightly) Assert.Skip("The spike measurements belong to the Nightly tier (YSHARP_TIER=Nightly).");

        var median = SpikeProcess.MedianOfRuns(SpikeCandidates.Control, SpikeWorkloads.TextInsert);

        Assert.Equal(SpikeWorkloads.TextInserts, median.Items);
        Assert.Equal("Release", median.Configuration);
        Assert.False(median.ServerGc);
        Assert.Equal(1.0, median.StructsPerOp);
    }
}
