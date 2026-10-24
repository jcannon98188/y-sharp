using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace YSharp.Benchmarks.Comparison;

public enum ChangeKind { Same, Regression, Improvement, New, Missing }

public sealed record BenchmarkChange(string Name, double? BaselineNs, double? CurrentNs, ChangeKind Kind)
{
    /// <summary>Current divided by baseline; above 1 is slower. Null when either side is missing.</summary>
    public double? Ratio => BaselineNs is > 0 && CurrentNs is { } c ? c / BaselineNs : null;
}

/// <summary>
/// The nightly comparison of ADR 0009: mean times from a BenchmarkDotNet JSON export against the last stored run. Regressions are
/// reported, never an error: the build stays green, the report is the signal. This file has no dependency beyond the base library so the
/// gating tests can compile it too.
/// </summary>
public static class BenchmarkComparison
{
    public const double DefaultThresholdPercent = 10;

    /// <summary>Mean nanoseconds per benchmark (full name) from the <c>*-report*.json</c> files of a BenchmarkDotNet run, or from one such file.</summary>
    public static Dictionary<string, double> LoadMeans(string pathOrDirectory)
    {
        var files = Directory.Exists(pathOrDirectory)
            ? Directory.EnumerateFiles(pathOrDirectory, "*-report*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : new[] { pathOrDirectory };
        var all = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var file in files)
            foreach (var kv in ParseMeans(File.ReadAllText(file))) all[kv.Key] = kv.Value;
        return all;
    }

    public static Dictionary<string, double> ParseMeans(string json)
    {
        var means = new Dictionary<string, double>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Benchmarks", out var benchmarks)) return means;
        foreach (var b in benchmarks.EnumerateArray())
        {
            if (!b.TryGetProperty("Statistics", out var stats) || stats.ValueKind != JsonValueKind.Object) continue;
            if (!stats.TryGetProperty("Mean", out var mean) || mean.ValueKind != JsonValueKind.Number) continue;
            var name = b.TryGetProperty("FullName", out var n) ? n.GetString() : null;
            if (!string.IsNullOrEmpty(name)) means[name] = mean.GetDouble();
        }

        return means;
    }

    public static IReadOnlyList<BenchmarkChange> Compare(IReadOnlyDictionary<string, double> baseline, IReadOnlyDictionary<string, double> current, double thresholdPercent = DefaultThresholdPercent)
    {
        var limit = 1 + thresholdPercent / 100;
        var changes = new List<BenchmarkChange>();
        foreach (var name in baseline.Keys.Union(current.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var hasBase = baseline.TryGetValue(name, out var b);
            var hasCur = current.TryGetValue(name, out var c);
            if (!hasBase) changes.Add(new(name, null, c, ChangeKind.New));
            else if (!hasCur) changes.Add(new(name, b, null, ChangeKind.Missing));
            else if (b <= 0) changes.Add(new(name, b, c, ChangeKind.Same));
            else if (c / b > limit) changes.Add(new(name, b, c, ChangeKind.Regression));
            else if (c / b < 1 / limit) changes.Add(new(name, b, c, ChangeKind.Improvement));
            else changes.Add(new(name, b, c, ChangeKind.Same));
        }

        return changes;
    }

    public static string Markdown(IReadOnlyList<BenchmarkChange> changes, double thresholdPercent = DefaultThresholdPercent)
    {
        var sb = new StringBuilder();
        var regressions = changes.Where(c => c.Kind == ChangeKind.Regression).ToList();
        sb.AppendLine("# Benchmark comparison");
        sb.AppendLine();
        sb.AppendLine($"Threshold: {thresholdPercent:0.#}% slower than the last stored run. This report never fails the build.");
        sb.AppendLine();
        sb.AppendLine($"{regressions.Count} regression(s), {changes.Count(c => c.Kind == ChangeKind.Improvement)} improvement(s), {changes.Count(c => c.Kind == ChangeKind.Same)} unchanged, {changes.Count(c => c.Kind == ChangeKind.New)} new, {changes.Count(c => c.Kind == ChangeKind.Missing)} missing.");
        sb.AppendLine();
        if (changes.Count == 0) return sb.ToString();
        sb.AppendLine("| Benchmark | Baseline | Current | Ratio | Result |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var c in changes.OrderBy(c => c.Kind == ChangeKind.Regression ? 0 : 1).ThenBy(c => c.Name, StringComparer.Ordinal))
            sb.AppendLine($"| {c.Name} | {Time(c.BaselineNs)} | {Time(c.CurrentNs)} | {(c.Ratio is { } r ? r.ToString("0.00") + "x" : "")} | {c.Kind} |");
        return sb.ToString();
    }

    private static string Time(double? ns) =>
        ns is not { } v ? "" : v >= 1e9 ? $"{v / 1e9:0.###} s" : v >= 1e6 ? $"{v / 1e6:0.###} ms" : v >= 1e3 ? $"{v / 1e3:0.###} us" : $"{v:0.###} ns";

    /// <summary>
    /// The <c>compare</c> command: always returns 0. With no stored baseline (the first nightly) it reports that and stops.
    /// Usage: <c>compare &lt;baseline&gt; &lt;current&gt; [--threshold N] [--out report.md]</c>.
    /// </summary>
    public static int Run(string[] args, TextWriter output)
    {
        var positional = new List<string>();
        var threshold = DefaultThresholdPercent;
        string? outPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--threshold" && i + 1 < args.Length) threshold = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
            else positional.Add(args[i]);
        }

        string report;
        try
        {
            if (positional.Count != 2) throw new ArgumentException("usage: compare <baseline> <current> [--threshold N] [--out report.md]");
            if (!Directory.Exists(positional[0]) && !File.Exists(positional[0]))
                report = "# Benchmark comparison\n\nNo stored baseline yet (first run); nothing to compare.\n";
            else
                report = Markdown(Compare(LoadMeans(positional[0]), LoadMeans(positional[1]), threshold), threshold);
        }
        catch (Exception e) when (e is ArgumentException or IOException or JsonException or FormatException)
        {
            report = "# Benchmark comparison\n\nNo comparison could be made: " + e.Message + "\n";
        }

        output.WriteLine(report);
        if (outPath != null) File.WriteAllText(outPath, report);
        return 0;
    }
}
