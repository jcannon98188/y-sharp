using System;
using System.Diagnostics;
using FsCheck;
using FsCheck.Fluent;
using Xunit;
using YSharp.Fuzz.Guards;

namespace YSharp.Fuzz;

/// <summary>
/// Runs one FsCheck property for a layer. Gating: fixed seed, <see cref="GatingCases"/> cases (about 500, ADR 0013). Nightly: random seeds
/// in chunks until the case count or the time box is reached; a failing nightly seed is a real bug and is never retried (ADR 0006).
/// A failure writes a corpus candidate file from the last failing (shrunk) input.
/// </summary>
internal static class FuzzRunner
{
    public static int GatingCases => EnvInt("YSHARP_FUZZ_CASES", 500);

    public static int NightlyCases => EnvInt("YSHARP_FUZZ_NIGHTLY_CASES", 100_000);

    public static TimeSpan NightlyBox => TimeSpan.FromSeconds(EnvInt("YSHARP_FUZZ_NIGHTLY_SECONDS", 90));

    private const ulong FixedSeed = 0x5953_4841_524Dul;   // "YSHARM": any odd gamma works
    private const ulong FixedGamma = 0x9E37_79B9_7F4A_7C15ul;

    /// <param name="hosClass">The HOS class named in the candidate file.</param>
    /// <param name="cases">Gating cases for this property (default about 500); the nightly tier ignores it and runs <see cref="NightlyCases"/> within the time box.</param>
    /// <param name="candidate">The bytes to save for an input, or null when the input is not a single update.</param>
    public static void Check<T>(string hosClass, Arbitrary<T> arbitrary, Action<T> body, Func<T, (byte[] Bytes, UpdateFormat Format)?>? candidate = null, int? cases = null)
    {
        // a stub run must skip, not fail inside FsCheck: probe the body once with a generated value first
        var probe = Gen.Sample(arbitrary.Generator, 1, 8);
        Stub.Run(() => body(probe[0]));

        T? lastFailing = default;
        var failed = false;
        var property = Prop.ForAll(arbitrary, (T x) =>
        {
            try { body(x); }
            catch (Exception) { lastFailing = x; failed = true; throw; }
        });

        try
        {
            if (NightlyGate.IsNightly) RunNightly(property);
            else
            {
                var config = Config.QuickThrowOnFailure.WithMaxTest(cases ?? GatingCases).WithReplay(FixedSeed, FixedGamma);
                property.Check(config);
            }
        }
        catch (Exception e) when (failed)
        {
            var where = "";
            if (candidate != null && lastFailing != null && candidate(lastFailing) is { } c)
                where = $"{Environment.NewLine}Corpus candidate: {Hostile.WriteCandidate(hosClass, c.Bytes, c.Format, (long)FixedSeed, e.GetType().Name)}";
            Assert.Fail($"{hosClass} property failed. Last failing input: {lastFailing}{where}{Environment.NewLine}{e.Message}");
        }
    }

    private static void RunNightly(Property property)
    {
        var total = NightlyCases;
        var watch = Stopwatch.StartNew();
        var done = 0;
        while (done < total && watch.Elapsed < NightlyBox)
        {
            var chunk = Math.Min(2000, total - done);
            var seed = (ulong)Random.Shared.NextInt64() | 1;
            property.Check(Config.QuickThrowOnFailure.WithMaxTest(chunk).WithReplay(seed, FixedGamma));
            done += chunk;
        }
    }

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
}
