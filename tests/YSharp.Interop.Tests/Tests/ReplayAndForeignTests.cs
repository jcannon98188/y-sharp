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
