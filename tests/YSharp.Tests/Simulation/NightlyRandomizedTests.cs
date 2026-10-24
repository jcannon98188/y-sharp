using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Simulation;

/// <summary>How long each nightly randomized test may run (ADR 0017: about 10 s each, shrunk when the total would overrun).</summary>
internal static class NightlyBudget
{
    public const double BoxSeconds = 10;

    /// <summary>The randomized work's share of the nightly job's 20 minutes; the rest is for the differential and fuzz tiers.</summary>
    public const double DefaultBudgetSeconds = 600;

    public static double BudgetSeconds =>
        double.TryParse(Environment.GetEnvironmentVariable("YSHARP_RANDOM_BUDGET_SECONDS"), out var s) && s > 0 ? s : DefaultBudgetSeconds;

    /// <summary>The box per test: <see cref="BoxSeconds"/> unless <paramref name="tests"/> of them would overrun the budget.</summary>
    public static double PerTest(int tests, double budgetSeconds, double boxSeconds = BoxSeconds) =>
        tests <= 0 ? boxSeconds : Math.Min(boxSeconds, budgetSeconds / tests);
}

/// <summary>
/// The nightly half of ADR 0017: every randomized test also runs with fresh seeds for about 10 seconds (upstream's
/// <c>--repetition-time 10000</c>), at least once, and the sizes upstream only runs with <c>--production</c> are their own tests with the
/// same box. This is also where <c>testRepeatGenerateQuillChanges2Repeat</c> lives on: its 1000 inner iterations are just more seeds of
/// the Quill-2 test. The start seed is logged; a failure names its seed, saves the expanded op log as a fixture candidate with a
/// <c>repro</c> header under <c>failing-seeds/</c> (uploaded by the nightly job), and the seed belongs in <c>regression-seeds.json</c>.
/// Runs only when <c>YSHARP_TIER=Nightly</c>.
/// </summary>
[Requires(Feature.Sim)]
[Tier(Tier.Nightly)]
[Covers("SIM-11", "SIM-12", "SIM-13")]
public sealed class NightlyRandomizedTests
{
    private const string Map = "y-map.tests.js:testRepeatGeneratingYmapTests";
    private const string Array = "y-array.tests.js:testRepeatGeneratingYarrayTests";

    public static IEnumerable<object[]> Cases() => SimCases.All.Where(c => !c.Production).Select(c => new object[] { c.Key });

    [Theory, MemberData(nameof(Cases))]
    public void Nightly_FreshSeedsInATimeBox_ConvergeAcrossReplicas(string key) => Box(key);

    [Fact, PortOf(Map + "5000", "SIM-13")]
    public void Ymap_Repeat5000_ConvergesAcrossReplicas() => Box(Map + "5000");

    [Fact, PortOf(Map + "10000", "SIM-13")]
    public void Ymap_Repeat10000_ConvergesAcrossReplicas() => Box(Map + "10000");

    [Fact, PortOf(Map + "100000", "SIM-13")]
    public void Ymap_Repeat100000_ConvergesAcrossReplicas() => Box(Map + "100000");

    [Fact, PortOf(Array + "3000", "SIM-13")]
    public void Yarray_Repeat3000_ConvergesAcrossReplicas() => Box(Array + "3000");

    [Fact, PortOf(Array + "5000", "SIM-13")]
    public void Yarray_Repeat5000_ConvergesAcrossReplicas() => Box(Array + "5000");

    [Fact, PortOf(Array + "30000", "SIM-13")]
    public void Yarray_Repeat30000_ConvergesAcrossReplicas() => Box(Array + "30000");

    private static void Box(string key)
    {
        if (YSharpEnv.CurrentTier != Tier.Nightly) Assert.Skip("The time-boxed randomized runs belong to the Nightly tier (YSHARP_TIER=Nightly).");
        var c = SimCases.Get(key);
        var seconds = NightlyBudget.PerTest(SimCases.All.Count, NightlyBudget.BudgetSeconds);
        var start = YSharpEnv.Seed ?? (uint)Random.Shared.NextInt64(1_000_000, int.MaxValue);
        var context = TestContext.Current;
        context.SendDiagnosticMessage($"{key}: start seed {start}, box {seconds:0.##} s" +
            (seconds < NightlyBudget.BoxSeconds ? $" (shrunk from {NightlyBudget.BoxSeconds:0} s: {SimCases.All.Count} tests would overrun the {NightlyBudget.BudgetSeconds:0} s budget)" : ""));
        {
            var clock = Stopwatch.StartNew();
            var runs = 0;
            var seed = start;
            do
            {
                SimCases.RunChecked(c, seed, nightly: true);
                runs++;
                seed++;
            } while (clock.Elapsed.TotalSeconds < seconds);
            context.SendDiagnosticMessage($"{key}: {runs} runs from seed {start}");
        }
    }
}

[LibraryIndependent]
public sealed class NightlyBudgetTests
{
    [Fact]
    [Covers("SIM-13")]
    public void Box_IsTenSeconds_AndShrinksWhenTheTotalWouldOverrunTheBudget()
    {
        Assert.Equal(10, NightlyBudget.PerTest(48, 600));
        Assert.Equal(5, NightlyBudget.PerTest(120, 600));
        Assert.Equal(10, NightlyBudget.PerTest(0, 600));
        Assert.True(NightlyBudget.PerTest(48, NightlyBudget.DefaultBudgetSeconds) >= NightlyBudget.BoxSeconds, "the default budget fits the 48 randomized tests unshrunk");
    }

    [Fact]
    [Covers("SIM-13")]
    public void ProductionSizes_AreNightlyOnly_AndThereAreSix()
    {
        var production = SimCases.All.Where(c => c.Production).Select(c => c.Key.Replace("y-map.tests.js:testRepeatGeneratingYmapTests", "map ").Replace("y-array.tests.js:testRepeatGeneratingYarrayTests", "array ")).ToList();
        Assert.Equal(new[] { "map 5000", "map 10000", "map 100000", "array 3000", "array 5000", "array 30000" }, production);
        Assert.Equal(48, SimCases.All.Count);
    }
}
