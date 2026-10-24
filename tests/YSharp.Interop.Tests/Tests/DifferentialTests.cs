using System;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using Xunit;
using YSharp.Interop.Generation;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Interop.Tests;

/// <summary>
/// The live differential (ADR 0012): FsCheck-generated ops scripts replayed on a Node child (the oracle) and on y-sharp, and the observations
/// compared. Smoke tier: fixed seeds, about a minute for the tier. Nightly tier: random seeds, a time box, failing scripts uploaded. Until
/// the features it needs have landed, every property skips (ADR 0020). The <c>[Covers]</c> lists
/// name the behaviours the generator really draws on, so the differential layer of those entries is reported (ADR 0010).
/// </summary>
[Requires(Feature.V1, Feature.V2, Feature.UpdateFunctions, Feature.V2Functions, Feature.Array, Feature.Map, Feature.Text, Feature.TextFormat, Feature.Events)]
[NeedsNode]
[Tier(Tier.Smoke)]
public sealed class DifferentialTests(OracleHost host)
{
    private static Arbitrary<ScriptPlan> Schedules() => Arb.From(
        Gen.Choose(0, int.MaxValue).Select(s => ScriptPlan.Generate(s) with { Exchange = Exchange.Schedule }),
        p => ScriptPlan.Shrink(p).Where(q => q.Exchange == Exchange.Schedule));

    private static ScriptPlan Generated(int seed) => ScriptPlan.Generate(seed);

    [Fact, Covers("DIFF-01", "DIFF-09", "DIFF-10", "ENC-21", "ENC-27", "ENC-74", "SY-14", "Q16")]
    public void Differential_StatelessScripts_ProduceEqualObservations() =>
        DiffProperty.Check(host, "stateless-scripts", ScriptPlan.Arbitrary(), (run, plan) => run.CheckPlan(plan));

    [Fact, Covers("DIFF-01", "A-4", "A-5", "M-1", "M-5")]
    public void Differential_ArrayAndMapEdits_ProduceEqualDumps() =>
        DiffProperty.Check(host, "array-map-edits", ScriptPlan.Arbitrary().Filter(p => p.Edits.Any(e => e.Kind >= EditKind.ArrayInsert)), (run, plan) => run.CheckPlan(plan));

    [Fact, Covers("DIFF-03", "SY-14", "SY-17", "SY-18")]
    public void DeliverySchedules_ReorderedDuplicatedOrDropped_ConvergeToTheSameState() =>
        DiffProperty.Check(host, "delivery-schedules", Schedules(), (run, plan) => run.CheckPlan(plan));

    [Fact, Covers("DIFF-02", "ENC-21", "ENC-27")]
    public void ApplyForeign_BytesOfOneSideApplyToTheOther_InV1AndV2() =>
        DiffProperty.Check(host, "apply-foreign", ScriptPlan.Arbitrary(), (run, plan) =>
        {
            if (run.CheckPlan(plan) is { } basis) run.CheckForeign(plan, basis);
        }, cases: 60);

    [Fact, Covers("DIFF-01", "ENC-23", "ENC-24", "ENC-25")]
    public void UpdateFunctions_MergeDiffConvert_AgreeOnTheUpdatesOfAPlan() =>
        DiffProperty.Check(host, "update-functions", ScriptPlan.Arbitrary(), (run, plan) =>
        {
            if (run.CheckPlan(plan) is { } basis) run.CheckPure(plan, basis.Oracle);
        }, cases: 60);

    [Fact, Covers("DIFF-04", "ENC-21")]
    public void Differential_MutatedValidUpdates_FallIntoAKnownOutcomeClass() =>
        DiffProperty.Check(host, "mutated-updates", MutationInput.Arbitrary(), (run, input) =>
        {
            if (run.CheckPlan(input.Plan) is not { } basis) return;
            var format = input.Plan.V2 ? UpdateFormat.V2 : UpdateFormat.V1;
            var valid = Convert.FromHexString(basis.Oracle.Observations!["state"]!["value"]!["a"]![input.Plan.V2 ? "v2" : "v1"]!["full"]!["hex"]!.GetValue<string>());
            var (mutated, offset) = Mutator.Apply(valid, input.Kind, input.Seed);
            run.CheckMutation(valid, mutated, input.Kind, offset, format);
        }, cases: 80);

    [Fact, Covers("DIFF-05", "ENC-28", "ENC-60", "ENC-62", "ENC-64", "ENC-65", "ENC-67", "ENC-68")]
    public void TruncatedUpdates_AreRejectedAtomically_OrAreExactlyTheRecordedLenientDivergence() =>
        DiffProperty.Check(host, "truncation-divergence", ScriptPlan.Arbitrary(), (run, plan) =>
        {
            if (run.CheckPlan(plan) is not { } basis) return;
            var format = plan.V2 ? UpdateFormat.V2 : UpdateFormat.V1;
            var valid = Convert.FromHexString(basis.Oracle.Observations!["state"]!["value"]!["a"]![plan.V2 ? "v2" : "v1"]!["full"]!["hex"]!.GetValue<string>());
            foreach (var cut in Mutator.CutOffsets(valid.Length, 25))
                run.CheckMutation(valid, valid[..cut], MutationKind.Truncate, cut, format);
        }, cases: 30);
}
