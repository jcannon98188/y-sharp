using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Simulation;

/// <summary>
/// The 41 time-repeated randomized upstream tests that run in the gating tier, and `testInsertAndDeleteAtRandomPositions`
/// (ADR 0017). Upstream repeats each one until 50 ms have passed with a fresh random seed; here each runs a committed seed list
/// `1..N` through the bit-exact PRNG, N by size class (up to 100 operations 200 seeds, 101 to 600 operations 40, 601 to 1800
/// operations 10), one data row per seed so a failure names its seed. Operation counts stay as upstream (the `10` map test really
/// runs 3). The sizes upstream only runs with `--production` and the 2Repeat variant are nightly (see <see cref="NightlyRandomizedTests"/>).
/// <para>
/// Faithfulness (ADR 0017): the draws match upstream draw for draw (`tools/fixture-gen/check-sim-draws.mjs`), the defects are
/// fixed: SIM-01 (replicas are compared with each other), SIM-02 (delete sets are asserted equal), SIM-03 (code-block key),
/// SIM-04 (the coin flip really selects V1 or V2) and SIM-05 (payload counters per run); see <see cref="SimDivergenceTests"/>.
/// One more upstream aliasing is not reproduced: the Quill generator shares three mark objects across a run and Yjs mutates them,
/// which no stored op log nor immutable attribute value can express; the check script re-creates it only to prove the rest matches.
/// </para>
/// </summary>
[Covers("SIM-11", "SIM-12", "SIM-13")]
public sealed class RepeatedRandomTests
{
    private const string Map = "y-map.tests.js:testRepeatGeneratingYmapTests";
    private const string Array = "y-array.tests.js:testRepeatGeneratingYarrayTests";
    private const string Text = "y-text.tests.js:testRepeatGenerateTextChanges";
    private const string Quill = "y-text.tests.js:testRepeatGenerateQuillChanges";
    internal const string InsertDelete = "y-text.tests.js:testInsertAndDeleteAtRandomPositions";

    private static void Gate(string key, uint seed) => Stub.Run(() => SimCases.RunChecked(SimCases.Get(key), seed));

    // ---- y-map.tests.js: testRepeatGeneratingYmapTests*
    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Map + "10", "SIM-13")]
    public void Ymap_Repeat10_ConvergesAcrossReplicas(uint seed) => Gate(Map + "10", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Map + "40", "SIM-13")]
    public void Ymap_Repeat40_ConvergesAcrossReplicas(uint seed) => Gate(Map + "40", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Map + "42", "SIM-13")]
    public void Ymap_Repeat42_ConvergesAcrossReplicas(uint seed) => Gate(Map + "42", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Map + "43", "SIM-13")]
    public void Ymap_Repeat43_ConvergesAcrossReplicas(uint seed) => Gate(Map + "43", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Map + "44", "SIM-13")]
    public void Ymap_Repeat44_ConvergesAcrossReplicas(uint seed) => Gate(Map + "44", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Map + "45", "SIM-13")]
    public void Ymap_Repeat45_ConvergesAcrossReplicas(uint seed) => Gate(Map + "45", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Map + "46", "SIM-13")]
    public void Ymap_Repeat46_ConvergesAcrossReplicas(uint seed) => Gate(Map + "46", seed);

    [Theory, MemberData(nameof(SeedLists.Up600), MemberType = typeof(SeedLists)), PortOf(Map + "300", "SIM-13")]
    public void Ymap_Repeat300_ConvergesAcrossReplicas(uint seed) => Gate(Map + "300", seed);

    [Theory, MemberData(nameof(SeedLists.Up600), MemberType = typeof(SeedLists)), PortOf(Map + "400", "SIM-13")]
    public void Ymap_Repeat400_ConvergesAcrossReplicas(uint seed) => Gate(Map + "400", seed);

    [Theory, MemberData(nameof(SeedLists.Up600), MemberType = typeof(SeedLists)), PortOf(Map + "500", "SIM-13")]
    public void Ymap_Repeat500_ConvergesAcrossReplicas(uint seed) => Gate(Map + "500", seed);

    [Theory, MemberData(nameof(SeedLists.Up600), MemberType = typeof(SeedLists)), PortOf(Map + "600", "SIM-13")]
    public void Ymap_Repeat600_ConvergesAcrossReplicas(uint seed) => Gate(Map + "600", seed);

    [Theory, MemberData(nameof(SeedLists.Up1800), MemberType = typeof(SeedLists)), PortOf(Map + "1000", "SIM-13")]
    public void Ymap_Repeat1000_ConvergesAcrossReplicas(uint seed) => Gate(Map + "1000", seed);

    [Theory, MemberData(nameof(SeedLists.Up1800), MemberType = typeof(SeedLists)), PortOf(Map + "1800", "SIM-13")]
    public void Ymap_Repeat1800_ConvergesAcrossReplicas(uint seed) => Gate(Map + "1800", seed);

    // ---- y-array.tests.js: testRepeatGeneratingYarrayTests*
    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Array + "6", "SIM-13")]
    public void Yarray_Repeat6_ConvergesAcrossReplicas(uint seed) => Gate(Array + "6", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Array + "40", "SIM-13")]
    public void Yarray_Repeat40_ConvergesAcrossReplicas(uint seed) => Gate(Array + "40", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Array + "42", "SIM-13")]
    public void Yarray_Repeat42_ConvergesAcrossReplicas(uint seed) => Gate(Array + "42", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Array + "43", "SIM-13")]
    public void Yarray_Repeat43_ConvergesAcrossReplicas(uint seed) => Gate(Array + "43", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Array + "44", "SIM-13")]
    public void Yarray_Repeat44_ConvergesAcrossReplicas(uint seed) => Gate(Array + "44", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Array + "45", "SIM-13")]
    public void Yarray_Repeat45_ConvergesAcrossReplicas(uint seed) => Gate(Array + "45", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Array + "46", "SIM-13")]
    public void Yarray_Repeat46_ConvergesAcrossReplicas(uint seed) => Gate(Array + "46", seed);

    [Theory, MemberData(nameof(SeedLists.Up600), MemberType = typeof(SeedLists)), PortOf(Array + "300", "SIM-13")]
    public void Yarray_Repeat300_ConvergesAcrossReplicas(uint seed) => Gate(Array + "300", seed);

    [Theory, MemberData(nameof(SeedLists.Up600), MemberType = typeof(SeedLists)), PortOf(Array + "400", "SIM-13")]
    public void Yarray_Repeat400_ConvergesAcrossReplicas(uint seed) => Gate(Array + "400", seed);

    [Theory, MemberData(nameof(SeedLists.Up600), MemberType = typeof(SeedLists)), PortOf(Array + "500", "SIM-13")]
    public void Yarray_Repeat500_ConvergesAcrossReplicas(uint seed) => Gate(Array + "500", seed);

    [Theory, MemberData(nameof(SeedLists.Up600), MemberType = typeof(SeedLists)), PortOf(Array + "600", "SIM-13")]
    public void Yarray_Repeat600_ConvergesAcrossReplicas(uint seed) => Gate(Array + "600", seed);

    [Theory, MemberData(nameof(SeedLists.Up1800), MemberType = typeof(SeedLists)), PortOf(Array + "1000", "SIM-13")]
    public void Yarray_Repeat1000_ConvergesAcrossReplicas(uint seed) => Gate(Array + "1000", seed);

    [Theory, MemberData(nameof(SeedLists.Up1800), MemberType = typeof(SeedLists)), PortOf(Array + "1800", "SIM-13")]
    public void Yarray_Repeat1800_ConvergesAcrossReplicas(uint seed) => Gate(Array + "1800", seed);

    // ---- y-text.tests.js: testRepeatGenerateTextChanges*
    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Text + "5", "SIM-13")]
    public void Text_Repeat5_ConvergesAcrossReplicas(uint seed) => Gate(Text + "5", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Text + "30", "SIM-13")]
    public void Text_Repeat30_ConvergesAcrossReplicas(uint seed) => Gate(Text + "30", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Text + "40", "SIM-13")]
    public void Text_Repeat40_ConvergesAcrossReplicas(uint seed) => Gate(Text + "40", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Text + "50", "SIM-13")]
    public void Text_Repeat50_ConvergesAcrossReplicas(uint seed) => Gate(Text + "50", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Text + "70", "SIM-13")]
    public void Text_Repeat70_ConvergesAcrossReplicas(uint seed) => Gate(Text + "70", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Text + "90", "SIM-13")]
    public void Text_Repeat90_ConvergesAcrossReplicas(uint seed) => Gate(Text + "90", seed);

    [Theory, MemberData(nameof(SeedLists.Up600), MemberType = typeof(SeedLists)), PortOf(Text + "300", "SIM-13")]
    public void Text_Repeat300_ConvergesAcrossReplicas(uint seed) => Gate(Text + "300", seed);

    // ---- y-text.tests.js: testRepeatGenerateQuillChanges*
    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Quill + "1", "SIM-13")]
    public void Quill_Repeat1_ConvergesAcrossReplicas(uint seed) => Gate(Quill + "1", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Quill + "2", "SIM-13")]
    public void Quill_Repeat2_ConvergesAcrossReplicas(uint seed) => Gate(Quill + "2", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Quill + "3", "SIM-13")]
    public void Quill_Repeat3_ConvergesAcrossReplicas(uint seed) => Gate(Quill + "3", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Quill + "30", "SIM-13")]
    public void Quill_Repeat30_ConvergesAcrossReplicas(uint seed) => Gate(Quill + "30", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Quill + "40", "SIM-13")]
    public void Quill_Repeat40_ConvergesAcrossReplicas(uint seed) => Gate(Quill + "40", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Quill + "70", "SIM-13")]
    public void Quill_Repeat70_ConvergesAcrossReplicas(uint seed) => Gate(Quill + "70", seed);

    [Theory, MemberData(nameof(SeedLists.Up100), MemberType = typeof(SeedLists)), PortOf(Quill + "100", "SIM-13")]
    public void Quill_Repeat100_ConvergesAcrossReplicas(uint seed) => Gate(Quill + "100", seed);

    [Theory, MemberData(nameof(SeedLists.Up600), MemberType = typeof(SeedLists)), PortOf(Quill + "300", "SIM-13")]
    public void Quill_Repeat300_ConvergesAcrossReplicas(uint seed) => Gate(Quill + "300", seed);

    // ---- y-text.tests.js: testInsertAndDeleteAtRandomPositions (2000 operations over 10 seeds; 100000 operations nightly)
    [Theory, MemberData(nameof(SeedLists.Up1800), MemberType = typeof(SeedLists)), PortOf(InsertDelete, "SIM-13")]
    public void InsertAndDelete_RandomPositions_MatchesAMirror(uint seed) => Gate(InsertDelete, seed);

    // ---- regression seeds: a nightly failure adds its seed to regression-seeds.json and it runs here forever

    [Fact]
    [Covers("SIM-13")]
    public void RegressionSeeds_AreWellFormed_AndRunInGating()
    {
        var entries = RegressionSeeds.Load();
        foreach (var e in entries)
        {
            Assert.Contains(SimCases.All, c => c.Key == e.Test);
            Assert.False(string.IsNullOrWhiteSpace(e.Reason), $"regression seed {e.Test}/{e.Seed} needs a reason");
        }
        Assert.Equal(entries.Count, entries.Select(e => (e.Test, e.Seed)).Distinct().Count());
        Stub.Run(() =>
        {
            foreach (var e in entries) SimCases.RunChecked(SimCases.Get(e.Test), e.Seed);
        });
    }

    // ---- the seed lists themselves

    [Fact]
    [Covers("SIM-13")]
    public void SeedLists_FollowTheSizeClasses_AndEveryGatingCaseHasItsTest()
    {
        if (YSharp.TestKit.Tiers.YSharpEnv.Seed == null)
        {
            Assert.Equal(200, SeedLists.Up100().Count());
            Assert.Equal(40, SeedLists.Up600().Count());
            Assert.Equal(10, SeedLists.Up1800().Count());
            Assert.Equal(Enumerable.Range(1, 200).Select(i => (uint)i), SeedLists.Up100().Select(r => (uint)r[0]));
        }
        Assert.All(SimCases.All.Where(c => c.Production), c => Assert.Equal(0, c.GatingSeeds));

        var tested = typeof(RepeatedRandomTests).GetMethods()
            .SelectMany(m => m.GetCustomAttributes<PortOfAttribute>().Select(p => (p.Upstream, Method: m)))
            .ToDictionary(x => x.Upstream, x => x.Method);
        foreach (var c in SimCases.All.Where(c => !c.Production))
        {
            Assert.True(tested.TryGetValue(c.Key, out var method), $"{c.Key} has no gating test");
            var source = method!.GetCustomAttribute<MemberDataAttribute>()!.MemberName;
            var expected = c.GatingSeeds switch { 200 => nameof(SeedLists.Up100), 40 => nameof(SeedLists.Up600), _ => nameof(SeedLists.Up1800) };
            Assert.True(source == expected, $"{c.Key}: seed list {source}, size class says {expected}");
        }
    }
}
