using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Interop.Tests;

/// <summary>
/// <c>YSHARP_REPLAY=&lt;path&gt;</c> re-runs one saved script (a fixture candidate from the artifacts directory, or a bare ops script) on both
/// sides with no FsCheck and no regeneration (ADR 0012 "Reporting"). Skips when the variable is not set.
/// </summary>
[Requires(Feature.V1, Feature.V2, Feature.UpdateFunctions, Feature.V2Functions, Feature.Array, Feature.Map, Feature.Text, Feature.TextFormat, Feature.Events)]
[NeedsNode]
[Tier(Tier.Smoke)]
public sealed class ReplayTests(OracleHost host)
{
    [Fact, Covers("DIFF-06")]
    public void ReplaySavedScript_WhenEnvVarIsSet_ReplaysIt()
    {
        var path = YSharpEnv.ReplayPath;
        if (path == null) Assert.Skip("Set YSHARP_REPLAY=<path of a saved script> to re-run one failing case.");
        host.RequireOrSkip(ignoreTier: true);
        var script = Reporting.LoadScript(path);
        var run = new DiffRun(host, "replay") { Label = "replay of " + path };
        try { run.Run(script); }
        catch (DifferentialFailure f) { Assert.Fail(f.Persist(run.Directory)); }
    }
}

/// <summary>
/// The oracle's <c>readPastEnd</c> flag on a named <c>applyForeign</c> result, which <see cref="LenientColumnEndNormaliser"/> relies on.
/// The case is the first live differential's find (TASK-4.28): a flipped byte shortens the V2 info column, so the oracle reads the length
/// of the second struct past the end of the len column (ENC-64) and accepts a zero-length ContentAny.
/// </summary>
[Requires(Feature.V1, Feature.V2, Feature.UpdateFunctions, Feature.V2Functions, Feature.Array, Feature.Map, Feature.Text, Feature.TextFormat, Feature.Events)]
[NeedsNode]
[Tier(Tier.Smoke)]
public sealed class ForeignReadPastEndTests(OracleHost host)
{
    private static string Script(string hex, string encoding) =>
        $$"""{"schema":1,"replicas":[{"name":"x","clientId":7000}],"steps":[{"step":"applyForeign","doc":"x","bytes":{"$":"bytes","hex":"{{hex}}"},"encoding":"{{encoding}}","name":"r"}]}""";

    private JsonNode Result(string hex, string encoding)
    {
        host.RequireOrSkip();
        var reply = host.RunAsync(Script(hex, encoding)).GetAwaiter().GetResult();
        return reply.Observations!["results"]!["value"]!["r"]!["value"]!;
    }

    [Fact, Covers("DIFF-05", "ENC-64")]
    public void ApplyForeign_AnUpdateReadPastTheEndOfAColumn_IsFlagged()
    {
        var value = Result("000004a701a5010000030802040b07617468656c6c6f410005010100010102010078010000", "v2");
        Assert.True(value["accepted"]!.GetValue<bool>());
        Assert.True(value["readPastEnd"]!.GetValue<bool>());
        Assert.False(SharpSide.Run(Script("000004a701a5010000030802040b07617468656c6c6f410005010100010102010078010000", "v2"))
            .Observations!["results"]!["value"]!["r"]!["value"]!["accepted"]!.GetValue<bool>(), "y-sharp rejects it (ADR 0005)");
    }

    [Fact, Covers("DIFF-05", "ENC-51")]
    public void ApplyForeign_AZeroLengthDeleteRangeForAnUnknownClient_IsRejectedByBoth()
    {
        // no structs, then a delete set holding client 5, clock 0, length 0: the shape of the Nightly find of TASK-4.28
        var value = Result("00010501" + "0000", "v1");
        Assert.False(value["accepted"]!.GetValue<bool>());
        Assert.Null(value["zeroLengthDeleteRange"]);
        Assert.False(SharpSide.Run(Script("00010501" + "0000", "v1"))
            .Observations!["results"]!["value"]!["r"]!["value"]!["accepted"]!.GetValue<bool>(), "y-sharp rejects the range (ENC-51, TASK-17.2)");
    }

    [Fact, Covers("DIFF-05", "ENC-51")]
    public void ApplyForeign_AZeroLengthDeleteRangeForAKnownClient_IsFlagged()
    {
        // "hi" from client 1, then a delete set holding client 1, clock 0, length 0: the oracle applies the range
        const string hex = "0101010004010174026869" + "0101010000";
        var value = Result(hex, "v1");
        Assert.True(value["accepted"]!.GetValue<bool>());
        Assert.True(value["zeroLengthDeleteRange"]!.GetValue<bool>());
        Assert.False(SharpSide.Run(Script(hex, "v1"))
            .Observations!["results"]!["value"]!["r"]!["value"]!["accepted"]!.GetValue<bool>(), "y-sharp rejects the range (ENC-51, TASK-17.2)");
    }

    [Fact, Covers("DIFF-05", "ENC-86")]
    public void ApplyForeign_OutOfOrderDeleteRangesParkedAsPending_AreFlagged()
    {
        // no structs, then client 119 with ranges (98, 99) and (40, 1): the shape of the Nightly find of TASK-4.28
        var value = Result("0001770262632801", "v1");
        Assert.True(value["accepted"]!.GetValue<bool>());
        Assert.True(value["unsortedPendingDeleteSet"]!.GetValue<bool>());
        Assert.Null(Result("0001770228016263", "v1")["unsortedPendingDeleteSet"]);
    }

    [Fact, Covers("DIFF-05", "Q15")]
    public void ApplyForeign_AParkedStructThatDependsOnItself_IsFlagged()
    {
        // client 5: "a" at clock 0 waits for client 1, then "b" at clock 1 with origin (5, 1), its own id (the shape of a Nightly find of TASK-4.28)
        const string hex = "0102050084010001618405010162" + "00";
        var value = Result(hex, "v1");
        Assert.True(value["accepted"]!.GetValue<bool>());
        Assert.True(value["selfDependentPending"]!.GetValue<bool>());
        Assert.False(SharpSide.Run(Script(hex, "v1")).Observations!["results"]!["value"]!["r"]!["value"]!["accepted"]!.GetValue<bool>(), "y-sharp rejects it (Q15, ADR 0005)");
        Assert.Null(Result("0102050084010001618405000162" + "00", "v1")["selfDependentPending"]);
    }

    [Fact, Covers("DIFF-05", "ENC-88")]
    public void ApplyForeign_AnItemOfLengthZero_IsFlagged()
    {
        // client 4: binary at root "", a GC, "" waiting on client 3, then a deleted struct whose parent is the binary item (the shape of the
        // SharpFuzz find of TASK-4.43)
        const string hex = "010404000301000000010406030000010404000100";
        var value = Result(hex, "v1");
        Assert.True(value["accepted"]!.GetValue<bool>());
        Assert.True(value["zeroLengthItem"]!.GetValue<bool>());
        Assert.False(SharpSide.Run(Script(hex, "v1")).Observations!["results"]!["value"]!["r"]!["value"]!["accepted"]!.GetValue<bool>(), "y-sharp rejects it (ENC-88, ADR 0005)");
        Assert.Null(Result("01010100040101740161" + "00", "v1")["zeroLengthItem"]);
    }

    [Fact, Covers("DIFF-05", "STR-71")]
    public void ApplyForeign_AStructWhoseParentIsNotAType_IsFlagged()
    {
        // client 102: "bc" in root array a at clock 0, then a deleted struct at clock 1 whose parent id is (102, 0), the Any item (the shape
        // of the Nightly find of TASK-4.33)
        const string hex = "0102660008010161017702626301006600" + "01" + "00";
        var value = Result(hex, "v1");
        Assert.True(value["accepted"]!.GetValue<bool>());
        Assert.True(value["parentNotAType"]!.GetValue<bool>());
        Assert.False(SharpSide.Run(Script(hex, "v1")).Observations!["results"]!["value"]!["r"]!["value"]!["accepted"]!.GetValue<bool>(), "y-sharp rejects the struct (STR-71, ADR 0005)");
        Assert.Null(Result("02016700080101610178016500080101740000", "v1")["parentNotAType"]);
    }

    [Fact, Covers("DIFF-05")]
    public void ApplyForeign_AnUpdateDecodedInBounds_CarriesNoFlag()
    {
        var value = Result("02016700080101610178016500080101740000", "v1");
        Assert.True(value["accepted"]!.GetValue<bool>());
        Assert.Null(value["readPastEnd"]);
    }
}

/// <summary>
/// The Nightly find of TASK-4.28: a flipped byte turns an Any array item into ContentDoc (ref 9), and rendering the array reaches the
/// subdocument. With spec 33 that content decodes to a subdocument and the update applies without a fault.
/// </summary>
[Requires(Feature.V1, Feature.Array, Feature.Map, Feature.Text, Feature.Events, Feature.Subdocs)]
public sealed class MutatedContentDocTests
{
    [Fact, Covers("DIFF-05")]
    public void MutatedUpdate_ReachingContentDoc_AppliesWithoutAFault()
    {
        const string hex = "0202660009010161027d00770262630401017404f09f9880046500040101740f782079e697a5e69cac7a68656c6c6f08010161017d2488650b027704f09f988078c8650b650c0477026263787e7d1800";
        var script = $$"""{"schema":1,"replicas":[{"name":"x","clientId":7000}],"steps":[{"step":"observe","doc":"x","target":{"root":"a","kind":"array"},"name":"o-x-a"},{"step":"applyForeign","doc":"x","bytes":{"$":"bytes","hex":"{{hex}}"},"encoding":"v1","name":"mutated"}]}""";
        var reply = SharpSide.Run(script);
        Assert.True(reply.Status == SharpStatus.Ok, $"{reply.Status}: {reply.Message} {reply.Exception}");
    }
}

/// <summary>
/// The Nightly find of TASK-4.44: an inserted byte gives a format item an origin inside array <c>a</c>, so it integrates into the array.
/// Building the array's event delta threw, the observer fault dropped the event, and the oracle's insert event was missing on our side.
/// Both sides now observe the same events (the format item inserts nothing, as <c>ContentFormat.getContent()</c> is <c>[]</c>).
/// </summary>
[Requires(Feature.V1, Feature.V2, Feature.UpdateFunctions, Feature.V2Functions, Feature.Array, Feature.Map, Feature.Text, Feature.TextFormat, Feature.Events)]
[NeedsNode]
[Tier(Tier.Smoke)]
public sealed class MutatedFormatInArrayTests(OracleHost host)
{
    [Fact, Covers("DIFF-05", "V-6")]
    public void MutatedUpdate_PuttingAFormatItemInAnArray_MatchesTheOracle()
    {
        host.RequireOrSkip();
        const string hex = "02046600080101610177017a88660003787e7d20c86600660104787e7d047703e282ac486600047d3077017a787e0565000401017401628465000763c3a9f09f98800101016103466500066974616c69630474727565c66586006501066974616c6963046e756c6c0165010503";
        var script = $$"""{"schema":1,"replicas":[{"name":"x","clientId":7000}],"steps":[{"step":"observe","doc":"x","target":{"root":"t","kind":"text"},"name":"o-x-t"},{"step":"observe","doc":"x","target":{"root":"a","kind":"array"},"name":"o-x-a"},{"step":"observe","doc":"x","target":{"root":"m","kind":"map"},"name":"o-x-m"},{"step":"applyForeign","doc":"x","bytes":{"$":"bytes","hex":"{{hex}}"},"encoding":"v1","name":"mutated"}]}""";
        var run = new DiffRun(host, "format-in-array") { Label = "mutated update with a format item in an array" };
        try { run.Run(script); }
        catch (DifferentialFailure f) { Assert.Fail(f.Persist(run.Directory)); }
    }
}

/// <summary>
/// The Nightly differential find of 2026-10-27 (tree-scripts, run 40547040408): peer c's text keeps search markers, and a delete midway
/// between two of them walked from the earlier one, where Yjs <c>findMarker</c> keeps the later one. The walk stopped on a's bold end with
/// no attributes, so the delete's format cleanup deleted it too, and every peer's update after that differed from the oracle's.
/// </summary>
[Requires(Feature.V1, Feature.V2, Feature.Text, Feature.TextFormat, Feature.Sync, Feature.Events)]
[NeedsNode]
[Tier(Tier.Smoke)]
public sealed class EquallyNearMarkersTests(OracleHost host)
{
    [Fact, Covers("DIFF-01", "A-14")]
    public void DeleteBetweenTwoEquallyNearMarkers_MatchesTheOracle()
    {
        host.RequireOrSkip();
        const string script = """{"schema":1,"replicas":[{"name":"a","clientId":101,"gc":true},{"name":"b","clientId":102,"gc":true},{"name":"c","clientId":103,"gc":true}],"steps":[{"step":"text.insert","doc":"a","target":{"root":"t","kind":"text"},"at":0,"value":"na\u00efvebc"},{"step":"transact","doc":"a","steps":[{"step":"text.format","doc":"a","target":{"root":"t","kind":"text"},"at":1,"len":1,"attributes":{"$":"obj","pairs":[["italic",true]]}},{"step":"text.format","doc":"a","target":{"root":"t","kind":"text"},"at":1,"len":3,"attributes":{"$":"obj","pairs":[["bold",true]]}}]},{"step":"sync","from":"a","to":"b"},{"step":"sync","from":"a","to":"c"},{"step":"text.insert","doc":"c","target":{"root":"t","kind":"text"},"at":4,"value":"\u65e5\u672c"},{"step":"text.insert","doc":"c","target":{"root":"t","kind":"text"},"at":7,"value":"\u20ac"},{"step":"text.delete","doc":"c","target":{"root":"t","kind":"text"},"at":4,"len":1},{"step":"sync","from":"b","to":"a"},{"step":"sync","from":"a","to":"c"},{"step":"sync","from":"c","to":"b"},{"step":"sync","from":"a","to":"b"},{"step":"sync","from":"a","to":"c"},{"step":"sync","from":"b","to":"a"},{"step":"sync","from":"b","to":"c"},{"step":"sync","from":"c","to":"a"},{"step":"sync","from":"c","to":"b"},{"step":"sync","from":"a","to":"b"},{"step":"sync","from":"a","to":"c"},{"step":"sync","from":"b","to":"a"},{"step":"sync","from":"b","to":"c"},{"step":"sync","from":"c","to":"a"},{"step":"sync","from":"c","to":"b"}]}""";
        var run = new DiffRun(host, "equally-near-markers") { Label = "a delete midway between two search markers" };
        try { run.Run(script, (oracle, sharp) => DiffRun.Convergence(oracle, "the oracle") ?? DiffRun.Convergence(sharp, "y-sharp"), DiffRun.PlanExtras); }
        catch (DifferentialFailure f) { Assert.Fail(f.Persist(run.Directory)); }
    }
}

/// <summary>The <c>applyForeign</c> step (ADR 0012, 0016) on the C# interpreter and its generated fixture. No Node needed.</summary>
[LibraryIndependent]
public sealed class ApplyForeignTests
{
    private static Fixture Load() => FixtureLoader.Load(Path.Combine(RepoRoot.Find(), "fixtures", "differential", "apply-foreign.json"), PinnedVersions.Read(RepoRoot.Find()));

    private static readonly OpsRegistry Registry = SharpSide.CreateRegistry();

    [Fact, Covers("DIFF-02")]
    public void ApplyForeign_StepKind_IsRegisteredWithItsSchemaFields()
    {
        Assert.True(Registry.TryGet("applyForeign", out var def));
        Assert.Equal(new[] { "doc", "bytes" }, def.Required);
        Assert.Equal(new[] { "encoding", "name" }, def.Optional);
        // the schema file is the normative vocabulary: same fields
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot.Find(), "tools", "fixture-gen", "schema", "ops.schema.json")))!;
        var entry = schema["$defs"]!["step"]!["oneOf"]!.AsArray().Single(s => s!["title"]!.GetValue<string>() == "applyForeign")!;
        Assert.Equal(new[] { "bytes", "doc", "encoding", "name", "step" }, entry["properties"]!.AsObject().Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "bytes", "doc", "step" }, entry["required"]!.AsArray().Select(r => r!.GetValue<string>()).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Theory, Covers("DIFF-02")]
    [InlineData("""{"schema":1,"replicas":[{"name":"a","clientId":1}],"steps":[{"step":"applyForeign","doc":"a","bytes":{"$":"bytes","hex":"00"},"bogus":1}]}""")]
    [InlineData("""{"schema":1,"replicas":[{"name":"a","clientId":1}],"steps":[{"step":"applyForeign","doc":"a"}]}""")]
    public void ApplyForeign_UnknownOrMissingFields_AreRejectedByTheInterpreter(string script) =>
        Assert.Throws<OpsScriptException>(() => OpsScript.Parse(script, Registry));

    [Fact, Covers("DIFF-02")]
    public void ApplyForeign_BadEncodingOrBytes_IsAScriptError()
    {
        var bad = SharpSide.Run("""{"schema":1,"replicas":[{"name":"a","clientId":1}],"steps":[{"step":"applyForeign","doc":"a","bytes":{"$":"bytes","hex":"00"},"encoding":"v3"}]}""");
        Assert.Equal(SharpStatus.ScriptError, bad.Status);
        var notBytes = SharpSide.Run("""{"schema":1,"replicas":[{"name":"a","clientId":1}],"steps":[{"step":"applyForeign","doc":"a","bytes":"00"}]}""");
        Assert.Equal(SharpStatus.ScriptError, notBytes.Status);
    }

    [Fact, Covers("DIFF-02")]
    [Requires(Feature.V1, Feature.V2, Feature.UpdateFunctions, Feature.V2Functions, Feature.Array, Feature.Map, Feature.Text, Feature.TextFormat, Feature.Events)]
    public void TheFixture_ReplaysOnYSharp_WithTheOraclesObservations()
    {
        var fixture = Load();
        Assert.Contains("DIFF-02", fixture.Header.Covers);
        var reply = SharpSide.Run(fixture.Script.GetRawText());
        Assert.True(reply.Status == SharpStatus.Ok, reply.Message);
        var expected = JsonNode.Parse(fixture.Observations.GetRawText())!.AsObject();
        var difference = ObservationDiff.Find(expected, reply.Observations!);
        Assert.True(difference == null, difference?.ToString());
        // the named outcomes: duplicate delivery and a V2 full state are accepted, the empty update is rejected
        var results = reply.Observations!["results"]!["value"]!;
        Assert.True(results["duplicate-v1"]!["value"]!["accepted"]!.GetValue<bool>());
        Assert.True(results["full-v2"]!["value"]!["accepted"]!.GetValue<bool>());
        Assert.False(results["empty"]!["value"]!["accepted"]!.GetValue<bool>());
    }

    [Fact, Covers("DIFF-02")]
    public void ApplyForeign_Fixture_RecordsTheOraclesOutcomes()
    {
        var results = Load().Observations.GetProperty("results").GetProperty("value");
        Assert.True(results.GetProperty("duplicate-v1").GetProperty("value").GetProperty("accepted").GetBoolean());
        Assert.False(results.GetProperty("empty").GetProperty("value").GetProperty("accepted").GetBoolean());
    }
}
