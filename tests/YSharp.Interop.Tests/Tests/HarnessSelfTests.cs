using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using YSharp.Interop.Generation;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;

namespace YSharp.Interop.Tests;

/// <summary>
/// Tests of the harness itself (ADR 0012), runnable before y-sharp exists: the y-sharp side is replaced by a second run of the oracle,
/// optionally tampered with or made to hang. They prove the generator's scripts run on the real oracle, that a planted difference is
/// found, shrunk, reported and saved, and that the hang, crash and misbehaviour rules hold. Smoke tier (needs Node).
/// </summary>
[NeedsNode]
[Tier(Tier.Smoke)]
public sealed class HarnessSelfTests : IDisposable
{
    private readonly OracleHost _host;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ysharp-selftest-" + Guid.NewGuid().ToString("N"));

    public HarnessSelfTests(OracleHost host)
    {
        _host = host;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>"y-sharp" is the oracle again; <paramref name="tamper"/> plants a difference in what it observed.</summary>
    private Func<string, SharpReply> OracleAsSharp(Action<string, JsonObject>? tamper = null) => script =>
    {
        var r = _host.RunAsync(script).GetAwaiter().GetResult();
        return r.Status switch
        {
            OracleStatus.Ok => Tamper(script, r.Observations!, tamper),
            OracleStatus.ScriptError => new SharpReply(SharpStatus.ScriptError, null, r.Message),
            _ => new SharpReply(SharpStatus.Fault, null, "stand-in could not run: " + r.Message),
        };
    };

    private static SharpReply Tamper(string script, JsonObject obs, Action<string, JsonObject>? tamper)
    {
        var clone = obs.DeepClone().AsObject();
        tamper?.Invoke(script, clone);
        return new SharpReply(SharpStatus.Ok, clone, null);
    }

    private DiffRun NewRun(Func<string, SharpReply>? sharp = null)
    {
        _host.RequireOrSkip();
        return new DiffRun(_host, "self-test", _dir) { SharpOverride = sharp ?? OracleAsSharp() };
    }

    [Fact, Covers("DIFF-01", "DIFF-03", "DIFF-10")]
    public void GeneratedScripts_RunOnTheRealOracle_WithoutScriptErrors_AndConverge()
    {
        var run = NewRun();
        for (var seed = 0; seed < 150; seed++)
        {
            run.CaseSeed = seed;
            Assert.True(run.CheckPlan(ScriptPlan.Generate(seed)) != null, $"seed {seed} was discarded: {run.LastDiscard}");
        }
        // every update index a delivery schedule names exists: nothing was discarded as a script error
        Assert.Equal(0, run.Discarded);
        Assert.Equal(0, run.Misbehaved);
    }

    [Fact, Covers("DIFF-02", "DIFF-03")]
    public void ForeignAndPureChecks_RunOnTheRealOracle_WithItsOwnBytes()
    {
        var run = NewRun();
        for (var seed = 0; seed < 40; seed++)
        {
            var plan = ScriptPlan.Generate(seed);
            var basis = run.CheckPlan(plan);
            Assert.True(basis != null, $"seed {seed} was discarded: {run.LastDiscard}");
            run.CheckForeign(plan, basis!.Value);
            run.CheckPure(plan, basis.Value.Oracle);
        }
        Assert.Equal(0, run.Discarded);
    }

    [Fact, Covers("DIFF-06")]
    public void ADifferenceInBytes_IsReported_WithTheOffset_AndSavedAsACandidate()
    {
        var run = NewRun(OracleAsSharp((_, obs) =>
        {
            var bytes = obs["state"]!["value"]!.AsObject().First().Value!["v1"]!["full"]!;
            var hex = bytes["hex"]!.GetValue<string>();
            bytes["hex"] = hex[..2] + (hex[2] == 'f' ? '0' : 'f') + hex[3..];
        }));
        var plan = ScriptPlan.Generate(5);
        run.Label = plan.ToString();
        var failure = Assert.Throws<DifferentialFailure>(() => run.CheckPlan(plan));
        Assert.Equal(FailureReason.Mismatch, failure.Reason);
        Assert.Equal("state", failure.Details.First!.Observation);
        Assert.Contains("First difference at offset 1", failure.Details.First.Detail);
        var report = failure.Persist(_dir);
        Assert.Contains("fixture candidate:", report);
        Assert.True(File.Exists(failure.Saved!.Path));
    }

    [Fact, Covers("DIFF-06")]
    public void ASavedCandidate_ReplaysWithoutFsCheck_AndReproducesTheDifference()
    {
        Func<string, SharpReply> planted = OracleAsSharp((_, obs) => obs["sv"]!["value"]!.AsObject().First().Value!["v1"]!["hex"] = "ff");
        var run = NewRun(planted);
        var plan = ScriptPlan.Generate(11);
        var failure = Assert.Throws<DifferentialFailure>(() => run.CheckPlan(plan));
        failure.Persist(_dir);

        // the YSHARP_REPLAY path: load the saved file, run the one script, no generator
        var script = Reporting.LoadScript(failure.Saved!.Path);
        var again = NewRun(planted);
        var replayed = Assert.Throws<DifferentialFailure>(() => again.Run(script));
        Assert.Equal(failure.Details.First!.Path, replayed.Details.First!.Path);

        // and the same file against an honest stand-in passes
        Assert.NotNull(NewRun().Run(script));
    }

    [Fact, Covers("DIFF-06", "DIFF-11")]
    public void AFailingProperty_IsShrunk_ReportsSeedScriptAndCandidate_AndIsNotRetried()
    {
        // plant a difference whenever a script touches a map: the minimal failing plan has a single map.set
        var planted = OracleAsSharp((script, obs) =>
        {
            if (script.Contains("map.set", StringComparison.Ordinal)) obs["sv"]!["value"]!.AsObject().First().Value!["v1"]!["hex"] = "ff";
        });
        DiffRun? last = null;
        var ex = Assert.ThrowsAny<Exception>(() => DiffProperty.Check(_host, "planted-map-bug", ScriptPlan.Arbitrary(), (run, plan) => run.CheckPlan(plan),
            cases: 200, directory: _dir, ignoreTier: true, runFactory: () => last = new DiffRun(_host, "planted-map-bug", _dir) { SharpOverride = planted }));
        Assert.Contains("Differential failure (Mismatch) in planted-map-bug", ex.Message);
        Assert.Contains("FsCheck replay:", ex.Message);
        Assert.Contains("fixture candidate:", ex.Message);
        Assert.Contains("YSHARP_REPLAY=", ex.Message);
        var shrunk = ex.Message.Split('\n').SkipWhile(l => !l.Contains("shrunk script:", StringComparison.Ordinal)).Skip(1).First().Trim();
        var steps = JsonNode.Parse(shrunk)!["steps"]!.AsArray();
        Assert.Contains(steps, s => s!["step"]!.GetValue<string>() == "map.set");
        Assert.True(steps.Count(s => s!["step"]!.GetValue<string>() is not "sync" and not "deliver") <= 2, "the shrunk script keeps at most the map.set and one companion edit");
        Assert.Single(Directory.GetFiles(_dir), f => Path.GetFileName(f).StartsWith("mismatch-", StringComparison.Ordinal));
        Assert.NotNull(last);
    }

    [Fact, Covers("DIFF-07")]
    public void AnOracleMisbehaving_IsCountedAndSaved_NotAFailure()
    {
        const string script = """{"schema":1,"replicas":[{"name":"a","clientId":1}],"steps":[{"step":"applyForeign","doc":"a","bytes":{"$":"bytes","hex":""}}]}""";
        var run = NewRun(_ => new SharpReply(SharpStatus.Ok, new JsonObject(), null));
        Assert.Null(run.Run(script));
        Assert.Equal(1, run.Misbehaved);
        var saved = run.MisbehavedSaved.Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(saved.Path));
        Assert.Equal("oracle-misbehaved", doc.RootElement.GetProperty("header").GetProperty("repro").GetProperty("reason").GetString());
    }

    [Theory, Covers("DIFF-07")]
    [InlineData(SharpStatus.Hang)]
    [InlineData(SharpStatus.Fault)]
    public void YSharpHangingOrCrashing_IsAFailure_EvenWhenTheOracleMisbehaves(SharpStatus status)
    {
        const string script = """{"schema":1,"replicas":[{"name":"a","clientId":1}],"steps":[{"step":"applyForeign","doc":"a","bytes":{"$":"bytes","hex":""}}]}""";
        var run = NewRun(_ => new SharpReply(status, null, "stuck"));
        var failure = Assert.Throws<DifferentialFailure>(() => run.Run(script));
        Assert.Equal(FailureReason.SharpFault, failure.Reason);
        Assert.Equal(0, run.Misbehaved);
    }

    [Fact, Covers("DIFF-07")]
    public void BothSidesCallingAScriptInvalid_DiscardsTheCase_ButOneSideAloneIsAMismatch()
    {
        const string bad = """{"schema":1,"replicas":[{"name":"a","clientId":1}],"steps":[{"step":"text.insert","doc":"zz","target":{"root":"t","kind":"text"},"at":0,"value":"x"}]}""";
        var both = NewRun();
        Assert.Null(both.Run(bad));
        Assert.Equal(1, both.Discarded);

        var onlyOracle = NewRun(_ => new SharpReply(SharpStatus.Ok, new JsonObject(), null));
        Assert.Equal(FailureReason.Mismatch, Assert.Throws<DifferentialFailure>(() => onlyOracle.Run(bad)).Reason);

        var onlySharp = NewRun(_ => new SharpReply(SharpStatus.ScriptError, null, "refused"));
        Assert.Equal(FailureReason.Mismatch, Assert.Throws<DifferentialFailure>(() => onlySharp.Run(ScriptPlan.Generate(1).ToScript().ToJsonString())).Reason);
    }

    [Fact, Covers("DIFF-07", "DIFF-11")]
    public void ANightlyRun_StopsAtTheWallClockCap_AndReportsTheSeedsItCovered()
    {
        var summary = DiffProperty.Check(_host, "capped", ScriptPlan.Arbitrary(), (run, plan) => run.CheckPlan(plan),
            cases: 4, directory: _dir, ignoreTier: true, nightly: true, box: TimeSpan.FromSeconds(1.5), runFactory: () => new DiffRun(_host, "capped", _dir) { SharpOverride = OracleAsSharp() });
        Assert.True(summary.CappedByWallClock);
        Assert.NotEmpty(summary.Seeds);
        Assert.Equal(summary.Seeds.Count, summary.Seeds.Distinct().Count());
        Assert.True(summary.Cases >= 4);
        Assert.InRange(summary.Elapsed.TotalSeconds, 1.5, 30);
        Assert.Contains("stopped by the wall-clock cap", summary.ToString());
    }

    [Fact, Covers("DIFF-04", "DIFF-05")]
    public void TruncationSweep_EveryCut_IsBothRejectOrClaimedByTheLenientNormaliser()
    {
        var run = NewRun(OracleAsSharp((script, obs) =>
        {
            // a stand-in y-sharp that always rejects the mutated update and stays unchanged
            if (obs["results"]?["value"]?["mutated"] is null) return;
            obs["results"]!["value"]!["mutated"]!["value"]!["accepted"] = false;
            obs["sv"]!["value"]!["x"]!["v1"]!["hex"] = "00";
        }));
        var plan = ScriptPlan.Generate(21) with { Exchange = Exchange.Sync };
        var basis = run.CheckPlan(plan);
        var valid = Convert.FromHexString(basis!.Value.Oracle.Observations!["state"]!["value"]!["a"]!["v1"]!["full"]!["hex"]!.GetValue<string>());
        var claimed = 0;
        foreach (var cut in Mutator.CutOffsets(valid.Length, 25))
        {
            var claim = run.CheckMutation(valid, valid[..cut], MutationKind.Truncate, cut, UpdateFormat.V1);
            if (claim != null) claimed++;
        }
        TestContext.Current.SendDiagnosticMessage($"truncation sweep: {claimed} cuts claimed as the recorded lenient-EOF divergence");
    }

    [Fact, Covers("DIFF-04", "DIFF-05")]
    public void AMutationAcceptedByTheOracle_ButRejectedByYSharp_IsABug_WhenNoDivergenceCoversIt()
    {
        var run = NewRun(OracleAsSharp((script, obs) =>
        {
            if (obs["results"]?["value"]?["mutated"] is null) return;
            obs["results"]!["value"]!["mutated"]!["value"]!["accepted"] = false;
            obs["sv"]!["value"]!["x"]!["v1"]!["hex"] = "00";
        }));
        var plan = ScriptPlan.Generate(33) with { Exchange = Exchange.Sync };
        var valid = Convert.FromHexString(run.CheckPlan(plan)!.Value.Oracle.Observations!["state"]!["value"]!["a"]!["v1"]!["full"]!["hex"]!.GetValue<string>());
        // flip bytes until the oracle accepts one (a flipped text byte stays valid): y-sharp "rejecting" it has no recorded divergence
        DifferentialFailure? failure = null;
        for (var seed = 0; seed < 400 && failure == null; seed++)
        {
            var (mutated, at) = Mutator.Apply(valid, MutationKind.FlipByte, seed * 7919);
            try { run.CheckMutation(valid, mutated, MutationKind.FlipByte, at, UpdateFormat.V1); }
            catch (DifferentialFailure f) { failure = f; }
        }
        Assert.NotNull(failure);
        Assert.Equal(FailureReason.OutcomeClass, failure!.Reason);
        Assert.Contains("no recorded divergence covers it", failure.Message);
    }

    [Fact, Covers("DIFF-04")]
    public void BothRejecting_OracleAndYSharp_RequiresYSharpToBeUnchanged()
    {
        var run = NewRun(OracleAsSharp((script, obs) =>
        {
            if (obs["results"]?["value"]?["mutated"] is null) return;
            obs["results"]!["value"]!["mutated"]!["value"]!["accepted"] = false;
            obs["sv"]!["value"]!["x"]!["v1"]!["hex"] = "0101"; // y-sharp rejected but changed something
        }));
        var empty = Array.Empty<byte>();
        var failure = Assert.Throws<DifferentialFailure>(() => run.CheckMutation(new byte[] { 1, 2, 3 }, empty, MutationKind.Truncate, 0, UpdateFormat.V1));
        Assert.Contains("atomic", failure.Message);
    }
}
