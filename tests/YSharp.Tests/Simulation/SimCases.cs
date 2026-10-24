using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Sdk;
using YSharp.Tests.Structs;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Rng;
using YSharp.TestKit.Sim;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Stubs;

namespace YSharp.Tests.Simulation;

/// <summary>
/// One upstream randomized test (ADR 0017). <see cref="Key"/> is its ledger key. <see cref="Kind"/> is the generator
/// (<c>map</c>, <c>array</c>, <c>text</c>, <c>quill</c>) or <c>insertdelete</c>; <see cref="Iterations"/> is the upstream operation
/// count (kept as upstream, so <c>testRepeatGeneratingYmapTests10</c> really runs 3). <see cref="Production"/> marks the sizes
/// upstream only runs with <c>--production</c>: no seed list, nightly only.
/// </summary>
internal sealed record SimCase(string Key, string Kind, int Iterations, bool Production = false, bool Cleanup = false, int NightlyIterations = 0)
{
    public string Name => Key[(Key.IndexOf(':') + 1)..];

    /// <summary>
    /// ADR 0017 size classes: up to 100 operations 200 seeds, 101 to 600 operations 40, 601 to 1800 operations 10. The 2000-operation
    /// gating variant of testInsertAndDeleteAtRandomPositions runs 10 seeds.
    /// </summary>
    public int GatingSeeds => Production ? 0 : Kind == "insertdelete" ? 10 : Iterations <= 100 ? 200 : Iterations <= 600 ? 40 : Iterations <= 1800 ? 10 : 0;
}

/// <summary>The 48 <c>testRepeat*</c> tests and <c>testInsertAndDeleteAtRandomPositions</c> (2Repeat is merged into Quill2).</summary>
internal static class SimCases
{
    private static IEnumerable<SimCase> Family(string file, string prefix, string kind, (string Suffix, int Ops, bool Production)[] sizes, Func<string, bool> cleanup) =>
        sizes.Select(s => new SimCase($"{file}:{prefix}{s.Suffix}", kind, s.Ops, s.Production, cleanup(s.Suffix)));

    private static (string, int, bool)[] Same(int[] ops, int[] production) =>
        ops.Select(n => (n.ToString(), n, false)).Concat(production.Select(n => (n.ToString(), n, true))).ToArray();

    public static readonly IReadOnlyList<SimCase> All = new[]
    {
        // testRepeatGeneratingYmapTests10 passes 3 iterations (the name and the argument disagree upstream)
        Family("y-map.tests.js", "testRepeatGeneratingYmapTests", "map",
            new[] { ("10", 3, false) }.Concat(Same(new[] { 40, 42, 43, 44, 45, 46, 300, 400, 500, 600, 1000, 1800 }, new[] { 5000, 10000, 100000 })).ToArray(), _ => false),
        Family("y-array.tests.js", "testRepeatGeneratingYarrayTests", "array",
            Same(new[] { 6, 40, 42, 43, 44, 45, 46, 300, 400, 500, 600, 1000, 1800 }, new[] { 3000, 5000, 30000 }), _ => false),
        Family("y-text.tests.js", "testRepeatGenerateTextChanges", "text", Same(new[] { 5, 30, 40, 50, 70, 90, 300 }, Array.Empty<int>()), _ => true),
        // 2Repeat is merged into Quill2 (ledger): its 1000 inner loops are subsumed by the seed list and the nightly box
        Family("y-text.tests.js", "testRepeatGenerateQuillChanges", "quill", Same(new[] { 1, 2, 3, 30, 40, 70, 100, 300 }, Array.Empty<int>()), s => s is "1" or "2"),
        new[] { new SimCase("y-text.tests.js:testInsertAndDeleteAtRandomPositions", "insertdelete", 2000, NightlyIterations: 100000) },
    }.SelectMany(x => x).ToArray();

    public static SimCase Get(string key) =>
        All.FirstOrDefault(c => c.Key == key) ?? throw new ArgumentException($"No randomized test '{key}'.");

    // ------------------------------------------------------------------ running

    /// <summary>Runs one seed of a case. With a <paramref name="log"/> the op log of the run is recorded.</summary>
    public static void Run(SimCase c, uint seed, ISimRecorder? log = null, bool nightly = false)
    {
        var gen = Prng.Create(seed);
        if (c.Kind == "insertdelete")
        {
            RunInsertAndDelete(gen, nightly && c.NightlyIterations > 0 ? c.NightlyIterations : c.Iterations);
            return;
        }
        var checkText = c.Kind is "text" or "quill";
        SimHarness.ApplyRandomTests<object?>(
            gen, SimDrive.Mods(c.Kind), c.Iterations, initTestObject: null, internals: new StructsSimInternals(), recorder: log,
            beforeDestroy: checkText ? result => SimChecks.CheckResult(result.Users, c.Cleanup) : null);
    }

    /// <summary>
    /// <c>testInsertAndDeleteAtRandomPositions</c>. Upstream asserts nothing (its mirror string is commented out); the port restores
    /// the mirror check: the text must equal the string the same edits produce.
    /// </summary>
    private static void RunInsertAndDelete(IPrng gen, int n)
    {
        var sim = SimHarness.Init(gen, users: 1);
        var text = sim.Users[0].Doc.GetText("text");
        var mirror = new StringBuilder(Prng.Word(gen, n / 2, n / 2));
        text.Insert(0, mirror.ToString());
        for (var i = 0; i < n; i++)
        {
            var pos = (int)Prng.Uint32(gen, 0, text.Length);
            if (Prng.Bool(gen))
            {
                var len = (int)Prng.Uint32(gen, 1, 5);
                var word = Prng.Word(gen, 0, len);
                text.Insert(pos, word);
                mirror.Insert(pos, word);
            }
            else
            {
                var len = (int)Prng.Uint32(gen, 0, Math.Min(3, text.Length - pos));
                text.Delete(pos, len);
                mirror.Remove(pos, len);
            }
        }
        Assert.Equal(mirror.ToString(), text.ToString());
    }

    /// <summary>
    /// A seed is a real bug (ADR 0006): no retry, no quarantine. A failure is re-run once with the op log recorded, the log is saved
    /// as a fixture candidate with a <c>repro</c> header under <c>failing-seeds/</c> (the CI artifact path), and the exception names the seed.
    /// </summary>
    public static void RunChecked(SimCase c, uint seed, bool nightly = false)
    {
        try { Run(c, seed, nightly: nightly); }
        catch (Exception e) when (e is not NotImplementedException || !ApiStub.IsStub)
        {
            var saved = SaveRepro(c, seed, nightly, e);
            throw new XunitException($"{c.Key} failed for seed {seed}{(nightly ? " (nightly)" : "")}: {e.GetType().Name}: {e.Message}{Environment.NewLine}repro: {saved ?? "none"}", e);
        }
    }

    private static string? SaveRepro(SimCase c, uint seed, bool nightly, Exception error)
    {
        try
        {
            var log = new SimLog(5);
            try { Run(c, seed, log, nightly); } catch (Exception) { /* the same failure again; the log is what we came for */ }
            var repro = new JsonObject
            {
                ["repro"] = new JsonObject
                {
                    ["test"] = c.Key,
                    ["seed"] = seed,
                    ["nightly"] = nightly,
                    ["encoding"] = log.Format.ToString().ToLowerInvariant(),
                    ["error"] = error.Message,
                    ["replay"] = $"YSHARP_SEED={seed} dotnet test --solution YSharp.slnx --filter-method *{c.Name}",
                },
            };
            if (c.Kind != "insertdelete") repro["script"] = log.ToScript();
            var dir = Path.Combine(AppContext.BaseDirectory, "failing-seeds");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{c.Name}-{seed}.json");
            File.WriteAllText(path, repro.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch (Exception) { return null; }
    }
}

/// <summary>The committed seed lists (ADR 0017): seeds <c>1..N</c> by size class, or the one seed <c>YSHARP_SEED</c> pins.</summary>
public static class SeedLists
{
    private static IEnumerable<object[]> Rows(int n) =>
        YSharpEnv.Seed is { } pinned ? new[] { new object[] { pinned } } : Enumerable.Range(1, n).Select(i => new object[] { (uint)i });

    /// <summary>Up to 100 operations.</summary>
    public static IEnumerable<object[]> Up100() => Rows(200);

    /// <summary>101 to 600 operations.</summary>
    public static IEnumerable<object[]> Up600() => Rows(40);

    /// <summary>601 to 1800 operations (also the 2000-operation gating variant of testInsertAndDeleteAtRandomPositions).</summary>
    public static IEnumerable<object[]> Up1800() => Rows(10);
}

/// <summary>A nightly failure adds its seed here (<c>regression-seeds.json</c>) and it runs in gating forever (ADR 0017).</summary>
internal static class RegressionSeeds
{
    public static string FilePath => Path.Combine(RepoRoot.Find(), "tests", "YSharp.Tests", "Simulation", "regression-seeds.json");

    public sealed record Entry(string Test, uint Seed, string Reason);

    public static IReadOnlyList<Entry> Load(string? path = null)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path ?? FilePath));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("regression-seeds.json must be an array of {test, seed, reason}.");
        return doc.RootElement.EnumerateArray().Select(e => new Entry(
            e.GetProperty("test").GetString() ?? throw new FormatException("test is required"),
            e.GetProperty("seed").GetUInt32(),
            e.GetProperty("reason").GetString() ?? throw new FormatException("reason is required"))).ToList();
    }
}
