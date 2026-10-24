using System.IO;
using System.Linq;
using Xunit;
using YSharp.Benchmarks.Comparison;
using YSharp.TestKit.Tiers;

namespace YSharp.Tests.Allocation;

/// <summary>The nightly comparison (ADR 0009): it reports regressions and never fails the build.</summary>
[Tier(Tier.Gating)]
public sealed class BenchmarkComparisonTests
{
    private static string Report(params (string Name, double Mean)[] rows) =>
        "{\"Benchmarks\":[" + string.Join(",", rows.Select(r => $"{{\"FullName\":\"{r.Name}\",\"Statistics\":{{\"Mean\":{r.Mean.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}}}")) + "]}";

    private static ChangeKind KindOf(string baseline, string current, string name, double threshold = 10) =>
        BenchmarkComparison.Compare(BenchmarkComparison.ParseMeans(baseline), BenchmarkComparison.ParseMeans(current), threshold).Single(c => c.Name == name).Kind;

    [Fact]
    public void ParseMeans_ReadsFullNameAndMean_AndSkipsBenchmarksWithoutStatistics()
    {
        var json = "{\"Benchmarks\":[{\"FullName\":\"A.One\",\"Statistics\":{\"Mean\":12.5}},{\"FullName\":\"A.Failed\",\"Statistics\":null},{\"FullName\":\"A.NoStats\"}]}";
        Assert.Equal(new[] { "A.One" }, BenchmarkComparison.ParseMeans(json).Keys.ToArray());
        Assert.Equal(12.5, BenchmarkComparison.ParseMeans(json)["A.One"]);
    }

    [Fact]
    public void Compare_FlagsSlowerThanThreshold_AsRegression() => Assert.Equal(ChangeKind.Regression, KindOf(Report(("A", 100)), Report(("A", 111)), "A"));

    [Fact]
    public void Compare_WithinThreshold_IsSame() => Assert.Equal(ChangeKind.Same, KindOf(Report(("A", 100)), Report(("A", 109)), "A"));

    [Fact]
    public void Compare_FasterThanThreshold_IsImprovement() => Assert.Equal(ChangeKind.Improvement, KindOf(Report(("A", 100)), Report(("A", 80)), "A"));

    [Fact]
    public void Compare_NewAndMissingBenchmarks_AreReportedNotRegressions()
    {
        Assert.Equal(ChangeKind.New, KindOf(Report(("A", 1)), Report(("A", 1), ("B", 5)), "B"));
        Assert.Equal(ChangeKind.Missing, KindOf(Report(("A", 1), ("B", 5)), Report(("A", 1)), "B"));
    }

    [Fact]
    public void Markdown_ListsRegressionsFirst_AndCountsThem()
    {
        var changes = BenchmarkComparison.Compare(
            BenchmarkComparison.ParseMeans(Report(("A", 100), ("B", 100))), BenchmarkComparison.ParseMeans(Report(("A", 100), ("B", 300))));
        var md = BenchmarkComparison.Markdown(changes);
        Assert.Contains("1 regression(s)", md);
        Assert.True(md.IndexOf("| B ", System.StringComparison.Ordinal) < md.IndexOf("| A ", System.StringComparison.Ordinal));
        Assert.Contains("3.00x", md);
    }

    [Fact]
    public void Run_WithARegression_StillReturnsZero_AndWritesTheReport()
    {
        var dir = Directory.CreateTempSubdirectory("bench-cmp").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "base.json"), Report(("A", 100)));
            File.WriteAllText(Path.Combine(dir, "cur.json"), Report(("A", 500)));
            var output = new StringWriter();
            var exit = BenchmarkComparison.Run(new[] { Path.Combine(dir, "base.json"), Path.Combine(dir, "cur.json"), "--out", Path.Combine(dir, "r.md") }, output);
            Assert.Equal(0, exit);
            Assert.Contains("1 regression(s)", File.ReadAllText(Path.Combine(dir, "r.md")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Run_WithNoStoredBaseline_ReportsItAndReturnsZero()
    {
        var output = new StringWriter();
        Assert.Equal(0, BenchmarkComparison.Run(new[] { Path.Combine(Path.GetTempPath(), "no-such-baseline-" + System.Guid.NewGuid()), "irrelevant" }, output));
        Assert.Contains("No stored baseline", output.ToString());
    }

    [Fact]
    public void Run_WithUnreadableInput_ReportsItAndReturnsZero()
    {
        var dir = Directory.CreateTempSubdirectory("bench-cmp").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.json"), "not json");
            var output = new StringWriter();
            Assert.Equal(0, BenchmarkComparison.Run(new[] { Path.Combine(dir, "bad.json"), Path.Combine(dir, "bad.json") }, output));
            Assert.Contains("No comparison could be made", output.ToString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BenchmarksProject_AnyBuild_HasTheAdr0009Benchmarks()
    {
        var source = File.ReadAllText(Path.Combine(YSharp.TestKit.Fixtures.RepoRoot.Find(), "bench", "YSharp.Benchmarks", "Benchmarks.cs"));
        foreach (var name in new[] { "ThroughputBenchmarks", "MergeDiffBenchmarks", "BuildLargeDocBenchmarks", "LockCostBenchmarks", "YjsComparisonBenchmarks", "BytesPerItemTable" })
            Assert.Contains("class " + name, source);
    }
}
