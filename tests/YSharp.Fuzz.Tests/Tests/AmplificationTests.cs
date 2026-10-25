using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Xunit;
using YSharp.Fuzz.Builder;
using YSharp.Fuzz.Corpus;
using YSharp.Fuzz.Generators;
using YSharp.Fuzz.Guards;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Fuzz.Tests;

/// <summary>
/// HOS-AMP and the long-running parts of the other classes (ADR 0013). The gating tier runs scaled-down shapes (100k tiny structs,
/// thousands of tiny pending updates, quadratic merge and diff shapes); the nightly tier runs the 1M-struct amplification, the
/// whole corpus and the mutation layer under tiny limits, and the random-seed layers. Nightly tests skip visibly in other tiers.
/// </summary>
[Requires(Feature.UpdateFunctions)]
public sealed class AmplificationTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    private static byte[] ManyStructs(int count, bool skips) =>
        UpdateBuilder.V1(UpdateModel.Single(1, 0, Enumerable.Range(0, count).Select(_ => skips ? Struct.Skip(1) : Struct.Gc(1)).ToArray()));

    // ------------------------------------------------------------------ gating-size amplification

    [Theory, Covers("HOS-AMP", Layer = CoverLayer.Unit)]
    [InlineData(false)]
    [InlineData(true)]
    [Requires(Feature.Text)]
    public void ManyOneClockStructs_100k_AreBoundedInTimeAndAllocation(bool skips)
    {
        var bytes = ManyStructs(100_000, skips);
        var run = InputGuard.Run("100k structs", () => Hostile.NewDoc().TryApplyUpdate(bytes), Generous);
        InputGuard.AssertAllocationWithinBound("100k structs", run.AllocatedBytes, bytes.Length);
        Assert.True(run.Value.Ok || run.Value.Error == YError.LimitExceeded, $"100k tiny structs gave {run.Value.Error}");
        Hostile.ApplyAndCheck("100k structs", bytes, UpdateFormat.V1, new[] { "ok", "LimitExceeded" });
    }

    [Fact, Covers("HOS-AMP", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text)]
    public void DeleteRange_Claiming2Pow40Clocks_IsBoundedAndKeepsTheDocumentUsable()
    {
        var before = new[] { HiBytes };
        foreach (var length in new[] { 1UL << 40, (1UL << 53) - 1 })
        {
            var update = UpdateBuilder.V1(UpdateModel.Empty.WithDeletes(new DeleteClient(1, new[] { new DeleteRange(0, length) })));
            var applied = Hostile.ApplyAndCheck($"delete range of {length} clocks", update, UpdateFormat.V1, new[] { "ok", "LimitExceeded", "Malformed" }, before: before);
            if (applied.Result.Ok) Assert.Equal("", applied.Doc.GetText("t").ToString());   // it deletes what exists, and nothing else
        }
    }

    private static byte[] HiBytes => UpdateBuilder.V1(UpdateBuilder.TextInsert(1, "t", "hi"));

    [Fact, Covers("HOS-AMP", "HOS-REF", "ERR-10", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text, Feature.Fuzz)]
    public void ThousandsOfTinyPendingUpdates_StayLinear_AndAllIntegrateWhenTheDependencyArrives()
    {
        const int count = 3000;
        const ulong missing = 7_000_000;
        var doc = Hostile.NewDoc();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var update = UpdateBuilder.V1(UpdateModel.Single(10_000 + (ulong)i, 0, Struct.InRoot("t", Content.Str("x"), origin: new Id(missing, 0))));
            var result = InputGuard.Run($"pending update {i}", () => doc.TryApplyUpdate(update)).Value;
            Assert.True(result.Ok, $"pending update {i} gave {result.Error}");
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"{count} pending updates took {watch.Elapsed}: the retry on arrival looks quadratic");

        var dependency = UpdateBuilder.V1(UpdateModel.Single(missing, 0, Struct.InRoot("t", Content.Str("D"))));
        var resolved = InputGuard.Run("dependency arrives", () => doc.TryApplyUpdate(dependency), Generous);
        InputGuard.AssertAllocationWithinBound("dependency arrives", resolved.AllocatedBytes, dependency.Length + count * 16);
        Assert.True(resolved.Value.Ok);
        Assert.Equal(count + 1, doc.GetText("t").ToString().Length);
        Hostile.AssertPostCondition(doc, "pending flood");
    }

    [Fact, Covers("HOS-AMP", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text, Feature.V2, Feature.V2Functions)]
    public void QuadraticMergeAndDiffShapes_ManySingleStructUpdates_FinishInTime()
    {
        const int count = 3000;
        var updates = new List<ReadOnlyMemory<byte>>();
        for (var i = 0; i < count; i++)
            updates.Add(UpdateBuilder.V1(UpdateModel.Single(1, (ulong)i, i == 0 ? Struct.InRoot("t", Content.Str("a")) : Struct.InRoot("t", Content.Str("a"), origin: new Id(1, (ulong)(i - 1))))));

        var merged = new ArrayBufferWriter<byte>();
        var run = InputGuard.Run("merge of 3000 updates", () => YUpdate.TryMerge(updates, merged), Generous);
        Assert.True(run.Value.Ok, $"TryMerge failed with {run.Value.Error}");
        InputGuard.AssertAllocationWithinBound("merge", run.AllocatedBytes, updates.Sum(u => (long)u.Length));

        var whole = new Doc();
        Assert.True(whole.TryApplyUpdate(merged.WrittenSpan).Ok);
        Assert.Equal(new string('a', count), whole.GetText("t").ToString());

        // diff against every 500th clock, convert to V2, state vector from the update
        foreach (var clock in new ulong[] { 0, 500, 1500, 2999 })
        {
            var diff = new ArrayBufferWriter<byte>();
            var diffRun = InputGuard.Run($"diff at {clock}", () => YUpdate.TryDiff(merged.WrittenSpan, UpdateBuilder.StateVector((1, clock)), diff), Generous);
            Assert.True(diffRun.Value.Ok);
        }
        var v2 = new ArrayBufferWriter<byte>();
        Assert.True(InputGuard.Run("convert", () => YUpdate.TryConvert(merged.WrittenSpan, UpdateFormat.V1, UpdateFormat.V2, v2), Generous).Value.Ok);
        var sv = new ArrayBufferWriter<byte>();
        Assert.True(InputGuard.Run("state vector", () => YUpdate.TryStateVectorFromUpdate(merged.WrittenSpan, sv), Generous).Value.Ok);
    }

    // ------------------------------------------------------------------ nightly

    [Fact, Tier(Tier.Nightly), Covers("HOS-AMP", Layer = CoverLayer.Unit)]
    public void Nightly_MillionOneClockStructs_AreBounded()
    {
        NightlyGate.Require();
        foreach (var skips in new[] { false, true })
        {
            var bytes = ManyStructs(1_000_000, skips);
            var run = InputGuard.Run("1M structs", () => Hostile.NewDoc().TryApplyUpdate(bytes), TimeSpan.FromSeconds(60));
            InputGuard.AssertAllocationWithinBound("1M structs", run.AllocatedBytes, bytes.Length);
            Assert.True(run.Value.Ok || run.Value.Error == YError.LimitExceeded, $"1M tiny structs gave {run.Value.Error}");
        }
    }

    [Fact, Tier(Tier.Nightly), Covers("HOS-LEN", "HOS-AMP", "HOS-DEEP", "HOS-REF", "HOS-CLOCK", "HOS-SEM", "HOS-PROTO", Layer = CoverLayer.Unit)]
    public void Nightly_TheWholeCorpus_UnderTinyLimits_IsContained()
    {
        NightlyGate.Require();
        CorpusRunner.ReplayAll(CorpusFiles.LoadAll(), Hostile.Tiny);
    }

    [Fact, Tier(Tier.Nightly), Covers("HOS-MUT")]
    public void Nightly_ByteMutator_UnderTinyLimits_IsContained()
    {
        NightlyGate.Require();
        FuzzRunner.Check("HOS-MUT", ByteMutator.Layer(), (RawInput i) =>
            Hostile.ApplyAndCheck(i.ToString(), i.Bytes, i.Format, new[] { "ok", "Malformed", "LimitExceeded", "Rejected" }, Hostile.Tiny),
            i => (i.Bytes, i.Format));
    }

    [Fact, Tier(Tier.Nightly), Covers("HOS-CLOCK", "HOS-REF", "HOS-SEM", "HOS-LEN", "HOS-AMP")]
    public void Nightly_StructureAwareBuilder_UnderTinyLimits_IsContained()
    {
        NightlyGate.Require();
        foreach (var profile in Enum.GetValues<Profile>())
            FuzzRunner.Check("HOS-" + profile.ToString().ToUpperInvariant(), HostileModels.Layer(profile), (ModelInput i) =>
                Hostile.ApplyAndCheck(i.ToString(), i.Bytes, i.Format, new[] { "ok", "Malformed", "LimitExceeded", "Rejected" }, Hostile.Tiny),
                i => (i.Bytes, i.Format), cases: 10_000);
    }
}
