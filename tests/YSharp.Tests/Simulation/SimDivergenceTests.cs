using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.Tests.Structs;
using YSharp.TestKit.Rng;
using YSharp.TestKit.Sim;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Simulation;

/// <summary>
/// The five simulation divergences of ADR 0017 (<c>SIM-01</c> to <c>SIM-05</c>): where the port fixes a defect of the upstream test
/// machinery (<c>docs/research/js-tests.md</c> section 6.7) instead of reproducing it. Each test names the upstream behaviour.
/// </summary>
public sealed class SimDivergenceTests
{
    // ------------------------------------------------------------------ SIM-01

    /// <summary>
    /// Upstream <c>checkResult</c> (<c>y-text.tests.js</c>) reads <c>result.users[i].getText('text').toDelta()</c> twice and compares
    /// the two results, i.e. every replica with itself, so it never fails. The port compares each replica with replica 0.
    /// </summary>
    [Fact]
    [Diverges("SIM-01", adr: "0017")]
    public void CheckResult_ComparesDifferentReplicas_UpstreamComparedEachWithItself()
    {
        Assert.Equal(-1, SimChecks.FirstDivergentReplica(new[] { "x", "x", "x" }));
        // upstream: p1 and p2 both come from users[i] -> always equal -> passes. Port: replica 2 differs from replica 0 -> reported.
        Assert.Equal(2, SimChecks.FirstDivergentReplica(new[] { "x", "x", "y" }));
        Assert.Equal(1, SimChecks.FirstDivergentReplica(new[] { "x", "y", "x" }));
    }

    // ------------------------------------------------------------------ SIM-02

    private sealed class RecordingInternals : ISimInternals
    {
        public int Pending { get; private set; }
        public int Stores { get; private set; }
        public void AssertNoPending(Doc doc) => Pending++;
        public void AssertStoresEqual(Doc a, Doc b) => Stores++;
    }

    /// <summary>
    /// Upstream <c>compare</c> calls <c>Y.equalDeleteSets(...)</c> and throws the boolean away, so delete-set equality is only checked
    /// indirectly. The port asks the internals to compare store <i>and</i> delete set for every consecutive pair of replicas
    /// (<see cref="StructsSimInternals"/> asserts the delete sets equal).
    /// </summary>
    [Fact]
    [Diverges("SIM-02", adr: "0017")]
    public void Compare_AssertsStoresAndDeleteSets_ForEveryPair_UpstreamDiscardedTheDeleteSetResult() => Stub.Run(() =>
    {
        var sim = SimHarness.Init(Prng.Create(1), users: 3);
        var internals = new RecordingInternals();
        SimHarness.Compare(sim.Users, internals);
        // 3 real replicas + 3 rebuilt from merged updates = 6 docs: no pending state in any, 5 consecutive pairs compared
        Assert.Equal(6, internals.Pending);
        Assert.Equal(5, internals.Stores);
    });

    [Fact]
    [Covers("SIM-02")]
    public void DeleteSetDumps_AreMergedAndSorted_SoUnequalDeletionsAreUnequal()
    {
        static List<ClientStructs> Dump(params (long Clock, long Len, bool Deleted)[] structs) =>
            new() { new ClientStructs(1, structs.Select(s => new StructInfo(1, s.Clock, s.Len, "item", null, null, "t", null, null, 4, null, s.Deleted, false)).ToList()) };

        var same = StructsProbe.DeleteSetJson(Dump((0, 2, true), (2, 3, true), (5, 1, false)));
        Assert.Equal("[[1,[[0,5]]]]", same);
        Assert.NotEqual(same, StructsProbe.DeleteSetJson(Dump((0, 2, true), (2, 3, false), (5, 1, false))));
    }

    // ------------------------------------------------------------------ SIM-03

    /// <summary>
    /// Upstream's code-block operation passes <c>{ insert: '\n', format: { 'code-block': true } }</c>; <c>applyDelta</c> ignores the
    /// key <c>format</c>, so upstream code blocks never carry the attribute. The port uses <c>attributes</c>.
    /// </summary>
    [Fact]
    [Diverges("SIM-03", adr: "0017")]
    public void CodeBlockDelta_UsesAttributes_UpstreamPassedTheIgnoredKeyFormat()
    {
        var delta = SimPlans.CodeBlockDelta(3, "0abc");
        Assert.Equal(3, delta.Count);
        var newline = delta[2]!.AsObject();
        var pairs = newline["pairs"]!.AsArray().Select(p => p![0]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "insert", "attributes" }, pairs);
        Assert.DoesNotContain("format", pairs);
        Assert.Equal("{\"$\":\"obj\",\"pairs\":[[\"code-block\",true]]}", newline["pairs"]![1]![1]!.ToJsonString());
        // no retain when the insert position is 0
        Assert.Equal(2, SimPlans.CodeBlockDelta(0, "x").Count);
    }

    // ------------------------------------------------------------------ SIM-04

    /// <summary>
    /// Upstream <c>init</c> draws the coin flip to choose V1 or V2 but both branches fall back to V1 ("sync protocol doesnt support v2
    /// protocol yet"). The port's flip selects the encoding of the whole run, and the connector really uses it.
    /// </summary>
    [Fact]
    [Diverges("SIM-04", adr: "0017")]
    public void CoinFlip_SelectsV1OrV2ForTheWholeRun_UpstreamFellBackToV1()
    {
        Assert.Equal(UpdateFormat.V1, SimHarness.ChooseEncoding(new ScriptedPrng(0.25)));
        Assert.Equal(UpdateFormat.V2, SimHarness.ChooseEncoding(new ScriptedPrng(0.75)));
        Assert.Equal(UpdateFormat.V2, new TestConnector(Prng.Create(1), UpdateFormat.V2).Format);

        // the recorder learns the chosen encoding, which the delivery schedule then carries
        var log = new SimLog(2);
        log.Begin(UpdateFormat.V2);
        var sv = log.StateVector(0);
        log.Edit(new JsonObject { ["step"] = "text.insert", ["doc"] = "a" });
        var steps = log.ToScript()["steps"]!.AsArray();
        Assert.Equal("v2", steps[0]!["encoding"]!.GetValue<string>());
        Assert.Equal(sv, steps[0]!["schedule"]![0]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Covers("SIM-04", "SIM-14")]
    public void ARunWithEitherEncoding_Converges(bool v2) => Stub.Run(() =>
    {
        // find a seed whose first draw (the coin flip) picks the wanted encoding
        var seed = Enumerable.Range(1, 100).Select(i => (uint)i).First(s => Prng.Bool(Prng.Create(s)) == v2);
        var sim = SimHarness.Init(Prng.Create(seed), users: 3);
        Assert.Equal(v2 ? UpdateFormat.V2 : UpdateFormat.V1, sim.TestConnector.Format);
        sim.Users[0].Doc.GetText("text").Insert(0, "hello");
        sim.Users[1].Doc.GetText("text").Insert(0, "world");
        SimHarness.Compare(sim.Users, new StructsSimInternals());
    });

    // ------------------------------------------------------------------ SIM-05

    /// <summary>
    /// Upstream payload counters (<c>_uniqueNumber</c>, <c>charCounter</c>) are module-level and leak between tests, so a seed alone does not
    /// reproduce payload text (and, through the length of the text, not even the structure). The port's counters belong to one run.
    /// </summary>
    [Fact]
    [Diverges("SIM-05", adr: "0017")]
    public void PayloadCounters_StartAtZeroForEveryRun_UpstreamLeakedThemBetweenTests()
    {
        // the same plan on the same draws yields the same payload in a second run: "0..." both times (upstream: "0..." then "1...")
        string Payload()
        {
            var counters = new PayloadCounters();
            var gen = new ScriptedPrng(0.1, 0.1, 0.5, 0.5); // position 0, a word of 2 letters
            var steps = SimPlans.Text[0](gen, "a", new FixedView(5), counters);
            return steps[0]["value"]!.GetValue<string>();
        }
        var first = Payload();
        Assert.StartsWith("0", first);
        Assert.Equal(first, Payload());
        Assert.NotSame(new TestConnector(Prng.Create(1)).Counters, new TestConnector(Prng.Create(1)).Counters);
        Assert.Equal(0, new TestConnector(Prng.Create(1)).Counters.NextChar());
    }

    private sealed class FixedView : ISimView
    {
        public FixedView(int length) => Length = length;
        public int Length { get; }
        public int StringLength => Length;
        public int NestedArrayLength(int index) => -1;
    }
}
