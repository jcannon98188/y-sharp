using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using Xunit;
using YSharp.Fuzz.Builder;
using YSharp.Fuzz.Generators;
using YSharp.Fuzz.Guards;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Fuzz.Tests;

/// <summary>
/// The three FsCheck layers of ADR 0013 (raw-byte mutator, structure-aware builder, ops-script with delivery-schedule mutation) as
/// properties per hostile class. Gating runs a fixed seed and about 500 cases per property; the nightly tier runs random seeds with a
/// time box. These are the <c>fuzz</c> layer of each HOS entry; the deterministic replays are in <see cref="CorpusReplayTests"/>.
/// </summary>
[Requires(Feature.Fuzz)]
public sealed class HostileProperties
{
    private static readonly string[] AnyOutcome = { "ok", "Malformed", "LimitExceeded", "Rejected" };
    private static readonly string[] RejectOutcome = { "Malformed", "LimitExceeded" };

    private static (byte[], UpdateFormat)? Candidate(RawInput i) => (i.Bytes, i.Format);

    private static (byte[], UpdateFormat)? Candidate(ModelInput i) => (i.Bytes, i.Format);

    private static void Hostile(string label, byte[] bytes, UpdateFormat format, string[] allowed, YLimits? limits = null) =>
        Guards.Hostile.ApplyAndCheck(label, bytes, format, allowed, limits);

    // ------------------------------------------------------------------ layer 1: raw-byte mutator

    [Fact, Covers("HOS-MUT", "ERR-21")]
    public void ByteMutator_AnyMutationOfAValidUpdate_IsContained() =>
        FuzzRunner.Check("HOS-MUT", ByteMutator.Layer(), (RawInput i) => Hostile(i.ToString(), i.Bytes, i.Format, AnyOutcome), Candidate);

    [Fact, Covers("HOS-TRUNC")]
    public void ByteMutator_TruncatedMutations_AreContained() =>
        FuzzRunner.Check("HOS-TRUNC", ByteMutator.Layer(), (RawInput i) =>
        {
            var cut = i.Bytes.Length == 0 ? 0 : i.Bytes.Sum(b => (int)b) % i.Bytes.Length;
            Hostile(i + $" cut {cut}", i.Bytes[..cut], i.Format, AnyOutcome);
        }, i => (i.Bytes, i.Format));

    [Fact, Covers("HOS-LEN")]
    public void ByteMutator_InflatedLengths_StayWithinTheAllocationBound() =>
        FuzzRunner.Check("HOS-LEN", ByteMutator.Layer(ByteMutator.Seeds(512)), (RawInput i) =>
        {
            // every apply is checked against the k*N+C bound inside Hostile.Apply; the inflate mutation puts ten-byte claims into counts and lengths
            var inflated = ByteMutator.Apply(i.Bytes, new[] { new Mutation(MutationKind.Inflate, Math.Abs(i.Bytes.Length * 7) % 1000, 1, 3, 0) });
            Hostile(i + " inflated", inflated, i.Format, AnyOutcome);
        }, Candidate);

    // ------------------------------------------------------------------ layer 2: structure-aware builder

    [Fact, Covers("HOS-CLOCK")]
    public void Builder_ClockShapes_AreContained() =>
        FuzzRunner.Check("HOS-CLOCK", HostileModels.Layer(Profile.Clock), (ModelInput i) => Hostile(i.ToString(), i.Bytes, i.Format, AnyOutcome), Candidate);

    [Fact, Covers("HOS-REF")]
    public void Builder_References_MissingSelfAndCyclicNeverSpin() =>
        FuzzRunner.Check("HOS-REF", HostileModels.Layer(Profile.Ref), (ModelInput i) => Hostile(i.ToString(), i.Bytes, i.Format, AnyOutcome), Candidate);

    [Fact, Covers("HOS-SEM")]
    public void Builder_MeaninglessContent_IsRejectedOrIgnored() =>
        FuzzRunner.Check("HOS-SEM", HostileModels.Layer(Profile.Sem), (ModelInput i) => Hostile(i.ToString(), i.Bytes, i.Format, AnyOutcome), Candidate);

    [Fact, Covers("HOS-LEN")]
    public void Builder_CountClaimsBeyondTheBytes_AreRejectedWithoutAllocatingForThem() =>
        FuzzRunner.Check("HOS-LEN", HostileModels.Layer(Profile.Len), (ModelInput i) =>
        {
            var claims = i.Model.NumClientsOverride != null || i.Model.NumDeleteClientsOverride != null
                         || i.Model.Blocks.Any(b => b.NumStructsOverride != null || b.Structs.Any(s => s.Content is { CountOverride: true }))
                         || i.Model.DeleteSet.Any(d => d.NumRangesOverride != null);
            // a claim larger than the data always runs the reader off the end, before anything integrates; without a claim anything goes
            Hostile(i.ToString(), i.Bytes, i.Format, claims ? RejectOutcome : AnyOutcome);
        }, Candidate);

    [Fact, Covers("HOS-AMP")]
    public void Builder_ManyTinyStructsClientsAndRanges_AreBoundedInTimeAndAllocation() =>
        FuzzRunner.Check("HOS-AMP", HostileModels.Layer(Profile.Amp), (ModelInput i) => Hostile(i.ToString(), i.Bytes, i.Format, AnyOutcome), Candidate, cases: 200);

    [Fact, Covers("HOS-TRUNC", "ERR-03")]
    [Requires(Feature.Text)]
    public void Builder_ValidModels_EveryTruncationIsMalformed_AndTheWholeDecodesToTheModel() =>
        FuzzRunner.Check("HOS-TRUNC", HostileModels.ValidLayer(), (ValidInput v) =>
        {
            var bytes = v.Bytes;
            var doc = Guards.Hostile.NewDoc();
            var whole = Guards.Hostile.ApplyTo(doc, v.ToString(), bytes, v.Format);
            Assert.True(whole.Result.Ok, $"{v}: the independent builder's valid update was rejected with {whole.Outcome}");
            Assert.Equal(v.ExpectedText, doc.GetText("t").ToString());
            var fresh = Guards.Hostile.NewDoc();
            for (var cut = 0; cut < bytes.Length; cut++)
            {
                var prefix = Guards.Hostile.ApplyTo(fresh, $"{v} cut {cut}", bytes[..cut], v.Format);
                Assert.True(prefix.Outcome == "Malformed", $"{v}: the {cut}-byte prefix gave {prefix.Outcome}");
            }
            Assert.Equal("", fresh.GetText("t").ToString());
        }, v => (v.Bytes, v.Format), cases: 200);

    // ------------------------------------------------------------------ layer 3: ops script with delivery-schedule mutation

    [Fact, Covers("HOS-REF", "HOS-AMP")]
    [Requires(Feature.Text, Feature.Array, Feature.Map)]
    public void OpsScript_MutatedDeliverySchedules_ConvergeOnceEveryUpdateHasArrived() =>
        FuzzRunner.Check("HOS-REF", OpsLayer.Layer(), (OpsPlan plan) =>
        {
            var (produced, replicas) = OpsLayer.Run(plan);
            var expected = Guards.Hostile.NewDoc();
            foreach (var p in produced) Assert.True(expected.TryApplyUpdate(p.Update).Ok, $"{plan}: in-order delivery rejected an update");

            var (order, late) = OpsLayer.Schedule(plan, produced.Count);
            var receiver = Guards.Hostile.NewDoc();
            foreach (var idx in order)
            {
                var applied = Guards.Hostile.ApplyTo(receiver, $"{plan} deliver {idx} of [{OpsLayer.Describe(order)}]", produced[idx].Update);
                Assert.True(applied.Result.Ok, $"{plan}: a valid update was rejected with {applied.Outcome} (order {OpsLayer.Describe(order)})");
            }
            Guards.Hostile.AssertPostCondition(receiver, plan.ToString());
            foreach (var idx in late) Assert.True(receiver.TryApplyUpdate(produced[idx].Update).Ok);

            Assert.True(Guards.Hostile.StateVector(expected).AsSpan().SequenceEqual(Guards.Hostile.StateVector(receiver)), $"{plan}: state vectors differ after full delivery");
            Assert.True(expected.GetText(OpsLayer.TextRoot).ToString() == receiver.GetText(OpsLayer.TextRoot).ToString(), $"{plan}: text differs");
            Assert.True(expected.GetArray(OpsLayer.ArrayRoot).ToJson() == receiver.GetArray(OpsLayer.ArrayRoot).ToJson(), $"{plan}: array differs");
            Assert.True(expected.GetMap(OpsLayer.MapRoot).ToJson() == receiver.GetMap(OpsLayer.MapRoot).ToJson(), $"{plan}: map differs");
            _ = replicas;
        });

    // ------------------------------------------------------------------ protocol frames

    [Fact, Covers("HOS-PROTO")]
    [Requires(Feature.Awareness, Feature.Sync)]
    public void ProtocolFrames_MutatedSyncAndAwarenessMessages_AreContained() =>
        FuzzRunner.Check("HOS-PROTO", ByteMutator.Layer(ByteMutator.Seeds(256)), (RawInput i) =>
        {
            var doc = Guards.Hostile.NewDoc();
            using var awareness = new Awareness(doc);
            foreach (var frame in Frames(i))
            {
                var run = InputGuard.Run($"{i} sync frame", () =>
                {
                    ReadOnlySpan<byte> input = frame;
                    var reply = new ArrayBufferWriter<byte>();
                    for (var guard = 0; guard < 1000 && !input.IsEmpty; guard++)
                    {
                        var before = input.Length;
                        var status = YSync.TryReadMessage(ref input, reply, doc, null, out _);
                        if (!status.Ok || input.Length >= before) break;
                    }
                    return true;
                });
                InputGuard.AssertAllocationWithinBound(i.ToString(), run.AllocatedBytes, frame.Length);

                var result = InputGuard.Run($"{i} awareness frame", () => awareness.TryApplyUpdate(frame, null));
                InputGuard.AssertAllocationWithinBound(i.ToString(), result.AllocatedBytes, frame.Length);
                Assert.True(AnyOutcome.Contains(Guards.Hostile.Label(result.Value)) || result.Value.Error == YError.UnknownMessage);
            }
            Guards.Hostile.AssertPostCondition(doc, i.ToString());
        }, Candidate);

    private static IEnumerable<byte[]> Frames(RawInput i)
    {
        yield return i.Bytes;                                         // the mutated bytes read as a bare frame
        yield return UpdateBuilder.SyncMessage(2, i.Bytes);           // wrapped as a sync update message
        yield return UpdateBuilder.SyncMessage(1, i.Bytes);           // and as a step 2
        yield return i.Bytes.Length == 0 ? new byte[] { 3 } : new[] { (byte)3 }.Concat(i.Bytes).ToArray();   // an unknown message type in front
    }

    // ------------------------------------------------------------------ HOS-DEEP at fuzz level (document depth is the canary's)

    [Fact, Covers("HOS-DEEP", "ERR-12")]
    public void AnyDepth_BeyondTheLimit_IsRejectedAndShallowIsAccepted() =>
        FuzzRunner.Check("HOS-DEEP", Arb.From(Gen.Choose(0, 5000)), (int depth) =>
        {
            // MaxAnyDepth is 128 by default; depths close to it are the boundary test's business, so the property stays clear of +-2
            var bytes = UpdateBuilder.DeepAnyArrays(depth);
            if (depth > 130)
                Guards.Hostile.ApplyAndCheck($"Any depth {depth}", bytes, UpdateFormat.V1, new[] { "LimitExceeded" });
            else if (depth < 126)
                Guards.Hostile.ApplyAndCheck($"Any depth {depth}", bytes, UpdateFormat.V1, new[] { "ok" });
        }, d => (UpdateBuilder.DeepAnyArrays(d), UpdateFormat.V1));
}
