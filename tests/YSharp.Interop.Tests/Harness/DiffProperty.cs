using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using Xunit;
using YSharp.TestKit.Tiers;

namespace YSharp.Interop;

/// <summary>What a property run covered; written to the test diagnostics so a capped nightly run reports the seeds it got through (ADR 0012 "Hangs").</summary>
public sealed record DiffSummary(string Property, int Cases, int Discarded, int Misbehaved, IReadOnlyList<string> Seeds, bool CappedByWallClock, TimeSpan Elapsed)
{
    public override string ToString() =>
        $"{Property}: {Cases} cases, {Discarded} discarded, {Misbehaved} oracle-misbehaved{(CappedByWallClock ? ", stopped by the wall-clock cap" : "")} in {Elapsed.TotalSeconds:0.0}s; seeds {string.Join(" ", Seeds)}";
}

/// <summary>
/// Runs one differential property under FsCheck (ADR 0012 "Tiers"). Smoke: fixed seed, a fixed number of cases (about a minute for the whole
/// tier). Nightly: random seeds in chunks until the time box ends; the chunk seeds are recorded and reported. A failure is reported with the
/// FsCheck replay seed and the shrunk script and is written as a fixture candidate; there is no retry (ADR 0006).
/// </summary>
public static class DiffProperty
{
    private const ulong FixedSeed = 0x5953_4448_4946ul;   // "YSDHIF"
    private const ulong FixedGamma = 0x9E37_79B9_7F4A_7C15ul;

    public static int SmokeCases => OracleOptions.EnvMs("YSHARP_DIFF_CASES", 120);

    public static TimeSpan NightlyBox => TimeSpan.FromSeconds(OracleOptions.EnvMs("YSHARP_DIFF_NIGHTLY_SECONDS", 240));

    public static bool IsNightly => YSharpEnv.CurrentTier == Tier.Nightly;

    /// <param name="cases">Smoke cases for this property; the nightly tier runs random chunks within <see cref="NightlyBox"/>.</param>
    public static DiffSummary Check<T>(OracleHost host, string property, Arbitrary<T> arbitrary, Action<DiffRun, T> body, int? cases = null, string? directory = null, bool ignoreTier = false, Func<DiffRun>? runFactory = null, bool? nightly = null, TimeSpan? box = null)
    {
        host.RequireOrSkip(ignoreTier);

        // y-sharp may still be the stub: probe one generated case first so that the run skips instead of failing inside FsCheck
        var probe = runFactory?.Invoke() ?? new DiffRun(host, property, directory);
        try { body(probe, Gen.Sample(arbitrary.Generator, 1, 8)[0]); }
        catch (NotImplementedException) { Assert.Skip("YSharp is still the API stub"); }
        catch (DifferentialFailure f) { Assert.Fail(f.Persist(probe.Directory)); }

        var run = runFactory?.Invoke() ?? new DiffRun(host, property, directory);
        var seeds = new List<string>();
        var watch = Stopwatch.StartNew();
        var capped = false;
        DifferentialFailure? last = null;

        var prop = Prop.ForAll(arbitrary, (T x) =>
        {
            try { body(run, x); }
            catch (DifferentialFailure f) { last = f; throw; }
        });

        void Chunk(ulong seed, int count)
        {
            run.ReplaySeed = $"{seed},{FixedGamma}";
            run.CaseSeed = (long)(seed & 0x7FFF_FFFF);
            seeds.Add(run.ReplaySeed);
            try { prop.Check(Config.QuickThrowOnFailure.WithMaxTest(count).WithReplay(seed, FixedGamma)); }
            catch (Exception e) when (last != null)
            {
                Assert.Fail(last.Persist(run.Directory) + Environment.NewLine + "FsCheck: " + e.Message);
            }
        }

        if (nightly ?? IsNightly)
        {
            var limit = box ?? NightlyBox;
            while (watch.Elapsed < limit) Chunk((ulong)Random.Shared.NextInt64() | 1, cases ?? 100);
            capped = true;
        }
        else Chunk(FixedSeed, cases ?? SmokeCases);

        var summary = new DiffSummary(property, run.Cases, run.Discarded, run.Misbehaved, seeds, capped, watch.Elapsed);
        TestContext.Current.SendDiagnosticMessage(summary.ToString());
        foreach (var s in run.MisbehavedSaved)
            TestContext.Current.SendDiagnosticMessage($"oracle-misbehaved script saved: {s.Path}");
        return summary;
    }
}
