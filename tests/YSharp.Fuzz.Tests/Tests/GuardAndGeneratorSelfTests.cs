using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FsCheck;
using FsCheck.Fluent;
using Xunit;
using Xunit.Sdk;
using YSharp.Fuzz.Builder;
using YSharp.Fuzz.Generators;
using YSharp.Fuzz.Guards;
using YSharp.TestKit.Ops;

namespace YSharp.Fuzz.Tests;

/// <summary>
/// Self-tests of the guards (time, allocation, stack, heap), the candidate writer and the three generator layers. None needs a
/// y-sharp implementation: a guard that never fires, or a generator that cannot build its input, would make every other test here worthless.
/// </summary>
public sealed class GuardAndGeneratorSelfTests
{
    // ------------------------------------------------------------------ guards

    [Fact]
    public void Guard_ATimeoutIsAHang()
    {
        var e = Assert.ThrowsAny<XunitException>(() => InputGuard.Run("sleeper", () => Thread.Sleep(5000), TimeSpan.FromMilliseconds(100)));
        Assert.Contains("HANG", e.Message);
        Assert.Contains("sleeper", e.Message);
    }

    [Fact]
    public void Guard_AFinishingBodyReturnsItsValueAndMeasures()
    {
        var run = InputGuard.Run("alloc", () => new byte[100_000].Length);
        Assert.Equal(100_000, run.Value);
        Assert.InRange(run.AllocatedBytes, 100_000, 400_000);
    }

    [Fact]
    public void Guard_AnExceptionIsRethrownOnTheCallerWithItsType()
    {
        Assert.Throws<NotImplementedException>(() => InputGuard.Run("stub", () => throw new NotImplementedException()));
        Assert.Throws<InvalidOperationException>(() => InputGuard.Run("bug", () => throw new InvalidOperationException("x")));
    }

    [Fact]
    public void Guard_RunsOnASeparateThread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var run = InputGuard.Run("thread", () => (Environment.CurrentManagedThreadId, Thread.CurrentThread.Name));
        Assert.NotEqual(caller, run.Value.Item1);
        Assert.Equal("ysharp-fuzz-probe", run.Value.Item2);
    }

    [Fact]
    public void Guard_TheProbeStackIsSmall()
    {
        // not by overflowing it (that would end the process): count how many frames fit before the runtime reports the stack as nearly used
        Assert.Equal(256 * 1024, Budget.ProbeStackBytes);
        var probe = InputGuard.Run("probe depth", () => FramesThatFit(0)).Value;
        var big = 0;
        var thread = new Thread(() => big = FramesThatFit(0), 16 * 1024 * 1024);
        thread.Start();
        thread.Join();
        Assert.True(probe > 100, $"the probe stack is too small to be usable ({probe} frames)");
        Assert.True(probe * 8 < big, $"the probe stack ({probe} frames) is not clearly smaller than a 16 MB stack ({big} frames)");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int FramesThatFit(int n)
    {
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack()) return n;
        Span<byte> pad = stackalloc byte[64];
        pad[0] = (byte)n;
        return FramesThatFit(n + 1) + pad[0] * 0;
    }
    [Fact]
    public void Guard_TheAllocationBoundIsKTimesNPlusC()
    {
        Assert.Equal(64, Budget.BytesPerInputByte);
        Assert.Equal(1024 * 1024, Budget.ConstantBytes);
        Assert.Equal(64 * 100 + 1024 * 1024, Budget.AllocationBound(100));
        InputGuard.AssertAllocationWithinBound("inside", Budget.AllocationBound(10), 10);
        var e = Assert.ThrowsAny<XunitException>(() => InputGuard.AssertAllocationWithinBound("outside", Budget.AllocationBound(10) + 1, 10));
        Assert.Contains("OVER-ALLOCATION", e.Message);
    }

    [Fact]
    public void Guard_AnOverAllocatingBodyIsCaught()
    {
        var run = InputGuard.Run("hog", () => new byte[8 * 1024 * 1024]);
        Assert.ThrowsAny<XunitException>(() => InputGuard.AssertAllocationWithinBound("hog", run.AllocatedBytes, 16));
    }

    [Fact]
    public void Guard_TheFuzzProcessRunsUnderAHeapHardLimit()
    {
        // runtimeconfig.template.json sets System.GC.HeapHardLimit to a few hundred MB (ADR 0013)
        var limit = AppContext.GetData("System.GC.HeapHardLimit");
        var info = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        Assert.True(limit != null || info <= 600L * 1024 * 1024, $"no heap hard limit is configured (GCHeapHardLimit={limit}, available {info:N0})");
        Assert.InRange(info, 100L * 1024 * 1024, 600L * 1024 * 1024);
    }

    [Fact]
    public void Candidate_WritesTheFailingInputAsAFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ysharp-candidates-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("YSHARP_FUZZ_CANDIDATES", dir);
        try
        {
            var path = Hostile.WriteCandidate("HOS-MUT", new byte[] { 1, 2, 255 }, UpdateFormat.V2, 42, "boom");
            Assert.StartsWith(dir, path, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("HOS-MUT", json.RootElement.GetProperty("class").GetString());
            Assert.Equal("v2", json.RootElement.GetProperty("encoding").GetString());
            Assert.Equal("0102ff", json.RootElement.GetProperty("bytes").GetString());
            Assert.Equal(42, json.RootElement.GetProperty("seed").GetInt64());
            Assert.Matches(@"^\d{4}-\d\d-\d\d$", json.RootElement.GetProperty("discovered").GetString()!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("YSHARP_FUZZ_CANDIDATES", null);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------ layer 1: byte mutator

    [Fact]
    public void ByteMutator_IsDeterministicPerCaseSeed_AndMutates()
    {
        var seeds = ByteMutator.Seeds();
        Assert.True(seeds.Count > 20);
        var changed = 0;
        for (var s = 0; s < 300; s++)
        {
            var a = ByteMutator.Generate(s, seeds);
            var b = ByteMutator.Generate(s, seeds);
            Assert.Equal(a.Bytes, b.Bytes);
            Assert.Equal(a.Format, b.Format);
            if (!seeds.Where(x => a.Origin.Contains(x.Source, StringComparison.Ordinal)).Any(x => x.Bytes.AsSpan().SequenceEqual(a.Bytes))) changed++;
        }
        Assert.True(changed > 200, $"only {changed} of 300 cases differ from their seed");
    }

    [Fact]
    public void ByteMutator_EveryKindIsApplied()
    {
        var seed = new byte[] { 1, 1, 1, 0, 4, 1, 1, 116, 2, 104, 105, 0 };
        foreach (var kind in Enum.GetValues<MutationKind>())
        {
            var mutated = ByteMutator.Apply(seed, new[] { new Mutation(kind, 500, 0x40, 2, 100) });
            Assert.False(seed.AsSpan().SequenceEqual(mutated), $"{kind} left the seed unchanged");
        }
    }

    [Fact]
    public void ByteMutator_ShrinkingGoesToTheShortestBytes()
    {
        var bytes = Enumerable.Range(1, 100).Select(i => (byte)i).ToArray();
        var candidates = ByteMutator.ShrinkBytes(bytes).ToList();
        Assert.Empty(ByteMutator.ShrinkBytes(Array.Empty<byte>()));
        Assert.Empty(candidates[0]);                       // the shortest comes first
        Assert.All(candidates, c => Assert.True(c.Length <= bytes.Length));
        // a property that fails for any input containing the byte 77 shrinks to exactly that byte
        var current = bytes;
        while (true)
        {
            var next = ByteMutator.ShrinkBytes(current).FirstOrDefault(c => c.Contains((byte)77) && c.Length < current.Length);
            if (next == null) break;
            current = next;
        }
        Assert.Equal(new byte[] { 77 }, current);
    }

    // ------------------------------------------------------------------ layer 2: structure-aware builder

    [Theory]
    [InlineData(Profile.Clock)]
    [InlineData(Profile.Ref)]
    [InlineData(Profile.Sem)]
    [InlineData(Profile.Len)]
    [InlineData(Profile.Amp)]
    public void HostileModels_EveryProfileBuildsBytesInBothFormats(Profile profile)
    {
        var lengths = new System.Collections.Generic.List<int>();
        for (var seed = 0; seed < 400; seed++)
        {
            var m = HostileModels.Generate(profile, seed);
            Assert.Equal(m.Bytes, HostileModels.Generate(profile, seed).Bytes);
            lengths.Add(m.Bytes.Length);
            var other = m with { Format = m.Format == UpdateFormat.V1 ? UpdateFormat.V2 : UpdateFormat.V1 };
            Assert.NotEmpty(other.Bytes);
        }
        Assert.True(lengths.Distinct().Count() > 50, "the profile produces too few distinct inputs");
    }

    [Fact]
    public void HostileModels_TheLenProfileClaimsMoreThanItWrites()
    {
        var claims = Enumerable.Range(0, 400).Count(s =>
        {
            var m = HostileModels.Generate(Profile.Len, s).Model;
            return m.NumClientsOverride != null || m.Blocks.Any(b => b.NumStructsOverride != null) || m.DeleteSet.Any(d => d.NumRangesOverride != null);
        });
        Assert.True(claims > 100, $"only {claims} of 400 Len models carry a claim");
    }

    [Fact]
    public void HostileModels_ShrinkingRemovesWholeStructsFirstAndNeverGrows()
    {
        var model = HostileModels.Generate(Profile.Ref, 7);
        var structs = model.Model.StructCount;
        var candidates = HostileModels.Shrink(model).ToList();
        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.True(c.Model.StructCount <= structs));
        Assert.Contains(candidates, c => c.Model.StructCount < structs);
        // greedy shrink to a fixpoint ends with a single struct when the "failure" is any struct at all
        var current = HostileModels.Generate(Profile.Clock, 11);
        for (var guard = 0; guard < 200; guard++)
        {
            var next = HostileModels.Shrink(current).FirstOrDefault(c => c.Model.StructCount >= 1 && c.Model.StructCount < current.Model.StructCount);
            if (next == null) break;
            current = next;
        }
        Assert.Equal(1, current.Model.StructCount);
    }

    [Fact]
    public void HostileModels_ValidModelsRoundTripThroughTheirOwnExpectation()
    {
        for (var seed = 0; seed < 300; seed++)
        {
            var v = HostileModels.GenerateValid(seed);
            Assert.NotEmpty(v.Bytes);
            Assert.True(v.Model.StructCount >= 1);
            Assert.Equal(v.Bytes, HostileModels.GenerateValid(seed).Bytes);
        }
    }

    // ------------------------------------------------------------------ layer 3: ops script and delivery schedules

    [Fact]
    public void OpsLayer_EveryPlanBecomesAScriptTheInterpreterAccepts()
    {
        var registry = StandardSteps.CreateRegistry();
        for (var seed = 0; seed < 300; seed++)
        {
            var plan = OpsLayer.Generate(seed);
            var script = OpsScript.Parse(OpsLayer.ToScript(plan), registry);
            Assert.NotEmpty(script.Steps);
            foreach (var shrunk in OpsLayer.Shrink(plan))
                Assert.NotEmpty(OpsScript.Parse(OpsLayer.ToScript(shrunk), registry).Steps);   // a shrunk plan is still a valid script
        }
    }

    [Fact]
    public void OpsLayer_SchedulesReorderDuplicateAndDrop()
    {
        var plan = new OpsPlan(2, Array.Empty<Edit>(), 123, Reorder: true, Duplicate: true, Drop: true);
        var (order, late) = OpsLayer.Schedule(plan, 20);
        var delivered = order.Concat(late).ToList();
        Assert.Equal(Enumerable.Range(0, 20), delivered.Distinct().OrderBy(i => i));   // nothing is lost: dropped updates arrive late
        Assert.True(delivered.Count > 20 || late.Count > 0, "no mutation happened");
        Assert.NotEqual(Enumerable.Range(0, 20), order.Take(20));
        var quiet = OpsLayer.Schedule(plan with { Reorder = false, Duplicate = false, Drop = false }, 20);
        Assert.Equal(Enumerable.Range(0, 20), quiet.Order);
        Assert.Empty(quiet.Late);
    }

    [Fact]
    public void FuzzRunner_FixedSeedGivesTheSameCasesTwice()
    {
        var gen = Gen.Choose(0, int.MaxValue);
        var first = Gen.Sample(gen, 10, 5);
        var config = Config.QuickThrowOnFailure.WithMaxTest(30).WithReplay(1234, 5);
        var seen1 = new System.Collections.Generic.List<int>();
        var seen2 = new System.Collections.Generic.List<int>();
        Prop.ForAll(Arb.From(gen), (int x) => { seen1.Add(x); }).Check(config);
        Prop.ForAll(Arb.From(gen), (int x) => { seen2.Add(x); }).Check(config);
        Assert.Equal(30, seen1.Count);
        Assert.Equal(seen1, seen2);
        Assert.NotNull(first);
    }

    [Fact]
    public void FuzzRunner_ShrinksAFailingByteInputToTheShortestFailingBytes()
    {
        // a deliberately wrong property, checked through FsCheck: it must fail and the reported counterexample must be minimal
        var seeds = ByteMutator.Seeds();
        var smallest = int.MaxValue;
        var config = Config.QuickThrowOnFailure.WithMaxTest(200).WithReplay(99, 7);
        var prop = Prop.ForAll(ByteMutator.Layer(seeds), (RawInput i) =>
        {
            var fails = i.Bytes.Length >= 3;
            if (fails) smallest = Math.Min(smallest, i.Bytes.Length);
            return !fails;
        });
        Assert.ThrowsAny<Exception>(() => prop.Check(config));
        Assert.Equal(3, smallest);
    }
}
