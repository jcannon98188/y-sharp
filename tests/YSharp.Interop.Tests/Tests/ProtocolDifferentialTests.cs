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
/// The live differential over the protocols (Spec 41): generated <c>sync.*</c>, <c>auth.*</c> and <c>awareness.*</c> scripts replayed on the
/// oracle's pinned y-protocols and on y-sharp's <c>YSync</c>, <c>YAuth</c> and <c>Awareness</c>. Every named result (bytes, type, error,
/// handler, read position), the awareness dumps (client id, states, meta with virtual times) and the one ordered event log are compared.
/// </summary>
[Requires(Feature.V1, Feature.Text, Feature.Events, Feature.Sync, Feature.Awareness)]
[NeedsNode]
[Tier(Tier.Smoke)]
public sealed class ProtocolDifferentialTests(OracleHost host)
{
    private static Arbitrary<ProtocolPlan> Plans(Func<ProtocolPlan, bool> filter) => ProtocolPlan.Arbitrary().Filter(filter);

    [Fact, Covers("DIFF-01", "DIFF-10", "SY-3", "SY-4", "SY-5", "SY-6", "SY-7", "SY-8", "SY-11", "SY-12", "SY-14", "SY-18", "SY-19", "SY-20")]
    public void Differential_SyncExchanges_ProduceEqualMessagesResultsAndState() =>
        DiffProperty.Check(host, "sync-protocol", Plans(p => !p.Awareness), (run, plan) => run.CheckProtocol(plan), cases: 80);

    [Fact, Covers("DIFF-01", "DIFF-10", "AW-6", "AW-8", "AW-9", "AW-10", "AW-12", "AW-14", "AW-15", "AW-16", "AW-17", "AW-22", "AW-25", "AW-27",
        "AW-28", "AW-30", "AW-31", "AW-32", "AW-33", "AW-34", "AW-35", "AW-36", "AW-37", "AW-38", "AW-39", "AW-41", "AW-42", "AW-44", "AW-45",
        "AW-46", "AW-47", "AW-48", "AW-50", "AW-51", "AW-52")]
    public void Differential_Awareness_ProducesEqualUpdatesStatesMetaAndEvents() =>
        DiffProperty.Check(host, "awareness-protocol", Plans(p => p.Awareness), (run, plan) => run.CheckProtocol(plan), cases: 120);

    /// <summary>
    /// DIFF-05 for AW-51: batches whose bad entry follows good ones. Node applies the leading entries and throws; y-sharp rejects the batch
    /// whole (ADR 0005). The normaliser asserts y-sharp equals the oracle with each rejected batch failing before its first entry.
    /// </summary>
    [Fact, Covers("DIFF-05", "AW-51")]
    public void AwarenessBatchesFailingPartway_AreExactlyTheRecordedAtomicDivergence() =>
        DiffProperty.Check(host, "awareness-partial-batch",
            Arb.From(Gen.Choose(0, int.MaxValue).Select(s => ProtocolPlan.Generate(s) with { Awareness = true, Partial = true }),
                p => ProtocolPlan.Shrink(p).Where(q => q.Awareness && q.Partial)).Filter(p => p.Actions.Any(a => a.Kind == ProtocolActionKind.ApplyHostile)),
            (run, plan) => run.CheckPartialAwareness(plan), cases: 60);
}
