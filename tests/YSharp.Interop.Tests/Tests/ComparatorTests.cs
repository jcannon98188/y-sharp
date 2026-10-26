using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Xunit;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Interop.Tests;

/// <summary>Equivalence of observations and the first-difference report (ADR 0012 "Compared", "Reporting"). No Node needed.</summary>
[LibraryIndependent]
public sealed class ComparatorTests
{
    private static JsonNode Bytes(string hex) => JsonNode.Parse($"{{\"$\":\"bytes\",\"hex\":\"{hex}\"}}")!;

    private static JsonObject Observations(string updateHex = "010203", string docInsert = "hi") => JsonNode.Parse("""
        {
          "updates": {"api":"x","value":{"a":{"v1":[{"$":"bytes","hex":"@UPDATE@"}],"v2":[]}}},
          "state":   {"api":"x","value":{"a":{"v1":{"full":{"$":"bytes","hex":"00"},"diff":{}},"v2":{"full":{"$":"bytes","hex":"00"},"diff":{}}}}},
          "sv":      {"api":"x","value":{"a":{"v1":{"$":"bytes","hex":"00"},"v2":{"$":"bytes","hex":"00"}}}},
          "doc":     {"api":"x","value":{"a":[["t",{"kind":"text","delta":[{"insert":"@INSERT@"}]}]]}}
        }
        """.Replace("@UPDATE@", updateHex).Replace("@INSERT@", docInsert))!.AsObject();

    [Fact, Covers("DIFF-01")]
    public void Comparator_EqualObservations_HaveNoDifference() => Assert.Null(ObservationDiff.Find(Observations(), Observations()));

    [Fact, Covers("DIFF-06")]
    public void DifferentBytes_AreReportedAtTheFirstDifferingOffset_WithHexAround()
    {
        var d = ObservationDiff.Find(Observations("010203"), Observations("010903"));
        Assert.NotNull(d);
        Assert.Equal("updates", d!.Observation);
        Assert.Equal("$.a.v1[0]", d.Path);
        Assert.Contains("First difference at offset 1", d.Detail);
        Assert.Contains("<02>", d.Detail);
        Assert.Contains("<09>", d.Detail);
    }

    [Fact, Covers("DIFF-06")]
    public void Comparator_DifferentDoc_IsReportedAsAPathIntoTheDump()
    {
        var d = ObservationDiff.Find(Observations(docInsert: "hi"), Observations(docInsert: "ho"));
        Assert.NotNull(d);
        Assert.Equal("doc", d!.Observation);
        Assert.Equal("$.a[0][1].delta[0].insert", d.Path);
        Assert.Contains("hi", d.Detail);
        Assert.Contains("ho", d.Detail);
    }

    [Fact, Covers("DIFF-01")]
    public void ApiLabels_AndMemberOrder_DoNotMatter_ButBytesDo()
    {
        var a = Observations();
        var b = Observations();
        b["updates"]!["api"] = "a different label";
        Assert.Null(ObservationDiff.Find(a, b));

        var reordered = JsonNode.Parse("""{"sv":{"api":"y","value":{"a":{"v2":{"$":"bytes","hex":"00"},"v1":{"$":"bytes","hex":"00"}}}}}""")!.AsObject();
        Assert.Null(ObservationDiff.Find(new JsonObject { ["sv"] = a["sv"]!.DeepClone() }, reordered));
    }

    [Fact, Covers("DIFF-01")]
    public void AnObservationOnlyOneSideProduced_IsADifference_ButOnesNeitherComparesAreIgnored()
    {
        var a = Observations();
        var b = Observations();
        b.Remove("sv");
        var d = ObservationDiff.Find(a, b);
        Assert.Equal("sv", d!.Observation);
        Assert.Contains("missing in y-sharp", d.Detail);

        // events, structs and ds (Spec 40) and awareness (Spec 41) are compared too: events the oracle fired and y-sharp did not are a difference
        foreach (var name in new[] { "events", "awareness", "structs", "ds" })
        {
            var withExtra = Observations();
            withExtra[name] = new JsonObject { ["api"] = "x", ["value"] = new JsonArray() };
            Assert.Equal(name, ObservationDiff.Find(withExtra, Observations())!.Observation);
        }

        // an observation outside the compared list (one only the oracle can make, such as an undo stack dump) is not
        var withOther = Observations();
        withOther["undoStack"] = new JsonObject { ["api"] = "x", ["value"] = new JsonObject() };
        Assert.Null(ObservationDiff.Find(withOther, Observations()));
    }

    [Fact, Covers("DIFF-01")]
    public void Results_CompareByValue_NotByApiLabel()
    {
        JsonObject Results(string api, bool accepted) => new()
        {
            ["results"] = new JsonObject { ["api"] = "x", ["value"] = new JsonObject { ["n"] = new JsonObject { ["api"] = api, ["value"] = new JsonObject { ["accepted"] = accepted } } } },
        };
        Assert.Null(ObservationDiff.Find(Results("Y.applyUpdate", true), Results("y-sharp", true)));
        var d = ObservationDiff.Find(Results("Y.applyUpdate", true), Results("y-sharp", false));
        Assert.Equal("results", d!.Observation);
        Assert.Equal("$.n.value.accepted", d.Path);
    }

    [Fact, Covers("DIFF-01")]
    public void MapPairOrder_IsExactForOneReplica_ButCanonicalAcrossReplicas()
    {
        JsonNode Map(params string[] keys) => JsonNode.Parse("{\"kind\":\"map\",\"pairs\":[" + string.Join(",", keys.Select(k => $"[\"{k}\",1]")) + "]}")!;
        // the same replica on both sides: iteration order is part of the contract (the JS integer-key rule is tested, not assumed)
        Assert.NotNull(ObservationDiff.CompareValues(Map("a", "b"), Map("b", "a"), "$"));
        // two replicas of one side: arrival order may differ, the set of pairs may not
        Assert.Null(ObservationDiff.CompareValues(Map("a", "b"), Map("b", "a"), "$", isDoc: true));
        Assert.NotNull(ObservationDiff.CompareValues(Map("a", "b"), Map("a", "c"), "$", isDoc: true));
    }

    [Fact, Covers("DIFF-03")]
    public void Comparator_ReplicaDiffersFromTheFirst_IsFlaggedByConvergence()
    {
        var obs = Observations();
        var docs = (JsonObject)obs["doc"]!["value"]!;
        docs["b"] = docs["a"]!.DeepClone();
        var state = (JsonObject)obs["state"]!["value"]!;
        state["b"] = state["a"]!.DeepClone();
        Assert.Null(DiffRun.Convergence(obs, "y-sharp"));

        docs["b"]![0]![1]!["delta"]![0]!["insert"] = "other";
        var d = DiffRun.Convergence(obs, "y-sharp");
        Assert.NotNull(d);
        Assert.Contains("replica b differs from a", d!.Detail);

        docs["b"] = docs["a"]!.DeepClone();
        state["b"]!["v1"]!["full"] = Bytes("0101");
        Assert.Contains("encoded state of b", DiffRun.Convergence(obs, "the oracle")!.Detail);

        // Yjs leaves GC runs from separate transactions unmerged on one replica: equal stores with the runs joined still converge
        static JsonNode Gc(int clock, int len) => JsonNode.Parse($"{{\"id\":[101,{clock}],\"len\":{len},\"kind\":\"gc\"}}")!;
        static JsonNode Store(params JsonNode[] structs) => new JsonArray(new JsonArray(101, new JsonArray(structs)));
        var item = JsonNode.Parse("{\"id\":[101,5],\"len\":1,\"kind\":\"item\",\"parent\":\"t\",\"deleted\":false}")!;
        obs["structs"] = new JsonObject { ["value"] = new JsonObject { ["a"] = Store(Gc(1, 2), Gc(3, 2), item.DeepClone()), ["b"] = Store(Gc(1, 4), item.DeepClone()) } };
        obs["ds"] = new JsonObject { ["value"] = new JsonObject { ["a"] = JsonNode.Parse("[[101,[[1,4]]]]"), ["b"] = JsonNode.Parse("[[101,[[1,4]]]]") } };
        Assert.Null(DiffRun.Convergence(obs, "the oracle"));
        obs["ds"]!["value"]!["b"] = JsonNode.Parse("[[101,[[1,5]]]]");
        Assert.Contains("encoded state of b", DiffRun.Convergence(obs, "the oracle")!.Detail);
        obs["ds"]!["value"]!["b"] = JsonNode.Parse("[[101,[[1,4]]]]");
        obs["structs"]!["value"]!["b"] = Store(Gc(1, 3), item.DeepClone());
        Assert.Contains("encoded state of b", DiffRun.Convergence(obs, "the oracle")!.Detail);
    }

    // ------------------------------------------------------------------ outcome classes and normalisers

    [Theory, Covers("DIFF-04")]
    [InlineData(true, true, OutcomeClass.BothAccept)]
    [InlineData(false, false, OutcomeClass.BothReject)]
    [InlineData(true, false, OutcomeClass.SharpRejectsOracleAccepts)]
    [InlineData(false, true, OutcomeClass.OracleRejectsSharpAccepts)]
    public void Comparator_FourOutcomeClasses_AreNamed(bool oracle, bool sharp, OutcomeClass expected) =>
        Assert.Equal(expected, Normalisers.Classify(oracle, sharp));

    private static MutationCase Case(MutationKind kind, bool oracle, bool sharp, bool unchanged = true, int validLength = 20, int mutatedLength = 12) =>
        new(UpdateFormat.V1, new byte[validLength], new byte[mutatedLength], kind, mutatedLength, oracle, sharp, unchanged);

    [Fact, Covers("DIFF-05")]
    public void Normaliser_LenientTruncation_ClaimsExactlyTheDocumentedDifference()
    {
        var n = new LenientTruncationNormaliser();
        Assert.True(n.Claims(Case(MutationKind.Truncate, oracle: true, sharp: false)));
        Assert.False(n.Claims(Case(MutationKind.Truncate, oracle: true, sharp: true)), "both accepting is not a divergence");
        Assert.False(n.Claims(Case(MutationKind.Truncate, oracle: false, sharp: false)), "both rejecting is the match case");
        Assert.False(n.Claims(Case(MutationKind.Truncate, oracle: false, sharp: true)), "y-sharp accepting what JS rejects is never recorded");
        Assert.False(n.Claims(Case(MutationKind.Truncate, oracle: true, sharp: false, unchanged: false)), "a rejected update must leave y-sharp unchanged");
        Assert.False(n.Claims(Case(MutationKind.FlipByte, oracle: true, sharp: false)), "only truncation is the recorded EOF divergence");
        Assert.False(n.Claims(Case(MutationKind.Truncate, oracle: true, sharp: false, validLength: 12, mutatedLength: 12)), "a prefix must be proper");
    }

    [Fact, Covers("DIFF-05", "ENC-64")]
    public void Normaliser_LenientColumnEnd_ClaimsOnlyWhenTheOracleReadPastTheEnd()
    {
        var n = new LenientColumnEndNormaliser();
        var flip = Case(MutationKind.FlipByte, oracle: true, sharp: false, validLength: 20, mutatedLength: 20);
        Assert.True(n.Claims(flip with { OracleReadPastEnd = true }));
        Assert.False(n.Claims(flip), "a mutation the oracle decodes in bounds is not the EOF divergence");
        Assert.False(n.Claims(Case(MutationKind.FlipByte, oracle: true, sharp: true) with { OracleReadPastEnd = true }), "both accepting is not a divergence");
        Assert.False(n.Claims(Case(MutationKind.FlipByte, oracle: false, sharp: false) with { OracleReadPastEnd = true }), "both rejecting is the match case");
        Assert.False(n.Claims(Case(MutationKind.FlipByte, oracle: true, sharp: false, unchanged: false) with { OracleReadPastEnd = true }), "a rejected update must leave y-sharp unchanged");
    }

    [Fact, Covers("DIFF-05", "ENC-51")]
    public void Normaliser_ZeroLengthDeleteRange_ClaimsOnlyTheReEncodeRejection()
    {
        var n = new ZeroLengthDeleteRangeNormaliser();
        var insert = Case(MutationKind.InsertByte, oracle: false, sharp: true, validLength: 20, mutatedLength: 21);
        Assert.True(n.Claims(insert with { OracleZeroLengthDeleteRange = true }));
        Assert.False(n.Claims(insert), "any other oracle rejection that y-sharp accepts is a bug");
        Assert.False(n.Claims(Case(MutationKind.InsertByte, oracle: false, sharp: false) with { OracleZeroLengthDeleteRange = true }), "both rejecting is the match case");
        Assert.False(n.Claims(Case(MutationKind.InsertByte, oracle: true, sharp: true) with { OracleZeroLengthDeleteRange = true }), "both accepting is not a divergence");
    }

    [Fact, Covers("DIFF-05", "ENC-86")]
    public void Normaliser_UnsortedPendingDeleteSet_ClaimsOnlyWhenBothAcceptOutOfOrderParkedRanges()
    {
        var n = new UnsortedPendingDeleteSetNormaliser();
        var both = Case(MutationKind.FlipByte, oracle: true, sharp: true, validLength: 20, mutatedLength: 20);
        Assert.True(n.Claims(both with { OracleUnsortedPendingDeleteSet = true }));
        Assert.False(n.Claims(both), "a state difference without out-of-order parked ranges is a bug");
        Assert.False(n.Claims(Case(MutationKind.FlipByte, oracle: true, sharp: false) with { OracleUnsortedPendingDeleteSet = true }), "y-sharp rejecting is another class");
        Assert.Equal(new[] { "sv", "doc" }, UnsortedPendingDeleteSetNormaliser.StillCompared);
    }

    [Fact, Covers("DIFF-05", "Q15")]
    public void Normaliser_SelfDependentStruct_ClaimsOnlyAParkedSelfDependency()
    {
        var n = new SelfDependentStructNormaliser();
        var rejects = Case(MutationKind.InsertByte, oracle: true, sharp: false, validLength: 20, mutatedLength: 21);
        Assert.True(n.Claims(rejects with { OracleSelfDependentPending = true }));
        Assert.False(n.Claims(rejects), "any other y-sharp rejection of an update the oracle accepts is a bug");
        Assert.False(n.Claims(Case(MutationKind.InsertByte, oracle: true, sharp: false, unchanged: false) with { OracleSelfDependentPending = true }), "a rejection must change nothing");
        Assert.False(n.Claims(Case(MutationKind.InsertByte, oracle: true, sharp: true) with { OracleSelfDependentPending = true }), "both accepting is not this divergence");
    }

    [Fact, Covers("DIFF-05")]
    public void Normaliser_EveryOne_NamesRegisteredInventoryEntries()
    {
        var ids = InventoryLoader.LoadEntries(RepoRoot.Find()).Select(e => e.Id).ToHashSet();
        Assert.NotEmpty(Normalisers.Registered);
        foreach (var n in Normalisers.Registered)
        {
            Assert.NotEmpty(n.RegistryIds);
            Assert.All(n.RegistryIds, id => Assert.Contains(id, ids));
            Assert.False(string.IsNullOrWhiteSpace(n.Description));
        }
        Assert.Equal(Normalisers.Registered.Count, Normalisers.Registered.Select(n => n.Key).Distinct().Count());
    }

    [Fact, Covers("DIFF-05")]
    public void Claim_ReturnsTheNormaliser_OrNullForAnUnrecordedDivergence()
    {
        Assert.Equal("lenient-truncation", Normalisers.Claim(Case(MutationKind.Truncate, true, false))!.Key);
        Assert.Null(Normalisers.Claim(Case(MutationKind.DeleteByte, true, false)));
        Assert.Equal("lenient-column-end", Normalisers.Claim(Case(MutationKind.DeleteByte, true, false) with { OracleReadPastEnd = true })!.Key);
    }
}

/// <summary>Candidates, reports and replay input (ADR 0012 "Reporting"). No Node needed.</summary>
[LibraryIndependent]
public sealed class ReportingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ysharp-reporting-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private const string Script = """{"schema":1,"replicas":[{"name":"a","clientId":1}],"steps":[{"step":"map.set","doc":"a","target":{"root":"m","kind":"map"},"key":"k","value":1}]}""";

    private static JsonObject Obs() => JsonNode.Parse("""{"sv":{"api":"x","value":{"a":{"v1":{"$":"bytes","hex":"00"},"v2":{"$":"bytes","hex":"00"}}}}}""")!.AsObject();

    [Fact, Covers("DIFF-06")]
    public void ACandidate_IsAFixtureShapedFile_WithAReproHeader_TheFixtureLoaderAccepts()
    {
        var saved = Reporting.Save(_dir, Script, Obs(), FailureReason.Mismatch, "stateless-scripts", "12345,678", 42, "doc differs");
        Assert.True(File.Exists(saved.Path));
        Assert.StartsWith("YSHARP_REPLAY=", saved.Replay);
        Assert.StartsWith(_dir, saved.Path);

        var fixture = FixtureLoader.Load(saved.Path, PinnedVersions.Read(RepoRoot.Find()));
        Assert.Equal(42, fixture.Header.Seed);
        Assert.Equal(JsonValueKind.Object, fixture.Script.ValueKind);
        using var doc = JsonDocument.Parse(File.ReadAllText(saved.Path));
        var repro = doc.RootElement.GetProperty("header").GetProperty("repro");
        Assert.Equal("mismatch", repro.GetProperty("reason").GetString());
        Assert.Equal("stateless-scripts", repro.GetProperty("property").GetString());
        Assert.Equal("12345,678", repro.GetProperty("seed").GetString());
        Assert.Equal("doc differs", repro.GetProperty("message").GetString());
        Assert.True(doc.RootElement.GetProperty("observations").TryGetProperty("sv", out _), "the oracle's observations travel with the candidate");
    }

    [Theory, Covers("DIFF-06")]
    [InlineData(FailureReason.Mismatch, "mismatch")]
    [InlineData(FailureReason.SharpFault, "ysharp-fault")]
    [InlineData(FailureReason.OracleMisbehaved, "oracle-misbehaved")]
    [InlineData(FailureReason.OutcomeClass, "outcome-class")]
    public void Candidate_Reason_IsNamedInTheHeaderAndTheFileName(FailureReason reason, string name)
    {
        var saved = Reporting.Save(_dir, Script, null, reason, "p", null, 0, "m");
        Assert.Contains(name, Path.GetFileName(saved.Path));
        using var doc = JsonDocument.Parse(File.ReadAllText(saved.Path));
        Assert.Equal(name, doc.RootElement.GetProperty("header").GetProperty("repro").GetProperty("reason").GetString());
    }

    [Fact, Covers("DIFF-06")]
    public void ReplayInput_CandidateOrBareScript_IsAccepted()
    {
        var saved = Reporting.Save(_dir, Script, Obs(), FailureReason.Mismatch, "p", null, 1, "m");
        var bare = Path.Combine(_dir, "bare.json");
        File.WriteAllText(bare, Script);
        Assert.Equal(JsonNode.Parse(Script)!.ToJsonString(), Reporting.LoadScript(saved.Path));
        Assert.Equal(JsonNode.Parse(Script)!.ToJsonString(), Reporting.LoadScript(bare));
    }

    [Fact, Covers("DIFF-06")]
    public void Candidate_SavingTheSameScriptTwice_OverwritesOneFile()
    {
        var a = Reporting.Save(_dir, Script, null, FailureReason.Mismatch, "p", null, 1, "m");
        var b = Reporting.Save(_dir, Script, null, FailureReason.Mismatch, "p", null, 2, "m");
        Assert.Equal(a.Path, b.Path);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact, Covers("DIFF-06")]
    public void Report_AnyFailure_CarriesSeedShrunkScriptBothObservationSetsAndTheFirstDifference()
    {
        var saved = Reporting.Save(_dir, Script, Obs(), FailureReason.Mismatch, "stateless-scripts", "1,2", 7, "m");
        var data = new FailureData(FailureReason.Mismatch, "stateless-scripts", "1,2", 7, "2 replicas, 1 edits", Script, Obs(), Obs(),
            new Difference("doc", "$.a", "differs"), "something");
        var text = Reporting.Render(data, saved);
        Assert.Contains("case seed: 7, FsCheck replay: 1,2", text);
        Assert.Contains("shrunk script:", text);
        Assert.Contains(Script, text);
        Assert.Contains("oracle observations:", text);
        Assert.Contains("y-sharp observations:", text);
        Assert.Contains("first difference: doc $.a: differs", text);
        Assert.Contains("fixture candidate: " + saved.Path, text);
        Assert.Contains("YSHARP_REPLAY=", text);
        Assert.Contains("no retry, no quarantine", text);
    }

    [Fact, Covers("DIFF-06", "DIFF-11")]
    public void TheArtifactsDirectory_DefaultsToAFailingSeedsFolder_TheNightlyJobUploads()
    {
        var previous = Environment.GetEnvironmentVariable("YSHARP_ARTIFACTS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("YSHARP_ARTIFACTS_DIR", null);
            Assert.EndsWith("failing-seeds", Reporting.ArtifactsDirectory);
            Environment.SetEnvironmentVariable("YSHARP_ARTIFACTS_DIR", _dir);
            Assert.Equal(_dir, Reporting.ArtifactsDirectory);
        }
        finally { Environment.SetEnvironmentVariable("YSHARP_ARTIFACTS_DIR", previous); }
    }
}

/// <summary>The y-sharp side's case timeout and error mapping (ADR 0012 "Hangs"). No Node needed.</summary>
[LibraryIndependent]
public sealed class SharpSideGuardTests
{
    [Fact, Covers("DIFF-07")]
    public void AHang_IsReported_AsYSharpsFault_AfterTheCaseTimeout()
    {
        var gate = new ManualResetEventSlim();
        var reply = SharpSide.RunGuarded(() => { gate.Wait(); return new JsonObject(); }, TimeSpan.FromMilliseconds(150));
        gate.Set();
        Assert.Equal(SharpStatus.Hang, reply.Status);
        Assert.Contains("150 ms", reply.Message);
    }

    [Fact, Covers("DIFF-07")]
    public void ACrash_IsReported_AsYSharpsFault_WithTheException()
    {
        var reply = SharpSide.RunGuarded(() => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5));
        Assert.Equal(SharpStatus.Fault, reply.Status);
        Assert.Contains("InvalidOperationException: boom", reply.Message);
        Assert.IsType<InvalidOperationException>(reply.Exception);
    }

    [Fact, Covers("DIFF-07")]
    public void StackOverflowGuard_DeepRecursion_RunsOnALargeStack()
    {
        int Depth(int n) => n == 0 ? 0 : 1 + Depth(n - 1);
        var reply = SharpSide.RunGuarded(() => new JsonObject { ["d"] = Depth(50_000) }, TimeSpan.FromSeconds(10));
        Assert.Equal(SharpStatus.Ok, reply.Status);
    }

    [Fact, Covers("DIFF-01")]
    public void AnInvalidScript_IsAScriptError_NotAFault()
    {
        var reply = SharpSide.Run("""{"schema":1,"steps":[{"step":"no.such.step"}]}""");
        Assert.Equal(SharpStatus.ScriptError, reply.Status);
    }

    [Fact, Covers("DIFF-01")]
    [Requires(Feature.Events)]
    public void AGeneratedScript_IsAcceptedByTheInterpreter_NeverReportedAsAScriptError()
    {
        var reply = SharpSide.Run(ScriptPlanScript());
        Assert.NotEqual(SharpStatus.ScriptError, reply.Status);   // Ok once the library works; a Fault only while it does not
    }

    private static string ScriptPlanScript() => Generation.ScriptPlan.Generate(3).ToScript().ToJsonString();

    [Fact, Covers("DIFF-01")]
    public void RootsOf_FindsEveryAddressedRoot_IncludingInsideTransact()
    {
        var script = JsonNode.Parse("""
            {"schema":1,"steps":[
              {"step":"transact","doc":"a","steps":[{"step":"map.set","doc":"a","target":{"root":"m","kind":"map"},"key":"k","value":1}]},
              {"step":"text.insert","doc":"a","target":[{"root":"t","kind":"text"},"x",2],"at":0,"value":"v"}]}
            """)!;
        Assert.Equal(new[] { ("m", "map"), ("t", "text") }, SharpSide.RootsOf(script).OrderBy(r => r.Name, StringComparer.Ordinal).ToArray());
        var clash = JsonNode.Parse("""{"steps":[{"target":{"root":"x","kind":"map"}},{"target":{"root":"x","kind":"text"}}]}""")!;
        Assert.Throws<YSharp.TestKit.Ops.OpsScriptException>(() => SharpSide.RootsOf(clash));
    }
}
