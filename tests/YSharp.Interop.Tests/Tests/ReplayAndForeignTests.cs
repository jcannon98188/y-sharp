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
    public void ApplyForeign_AZeroLengthDeleteRangeForAnUnknownClient_IsFlagged()
    {
        // no structs, then a delete set holding client 5, clock 0, length 0: the shape of the Nightly find of TASK-4.28
        var value = Result("00010501" + "0000", "v1");
        Assert.False(value["accepted"]!.GetValue<bool>());
        Assert.True(value["zeroLengthDeleteRange"]!.GetValue<bool>());
        Assert.True(SharpSide.Run(Script("00010501" + "0000", "v1"))
            .Observations!["results"]!["value"]!["r"]!["value"]!["accepted"]!.GetValue<bool>(), "y-sharp ignores the range (ENC-51)");
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
