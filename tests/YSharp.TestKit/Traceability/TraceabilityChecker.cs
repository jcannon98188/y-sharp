using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace YSharp.TestKit.Traceability;

/// <summary>One behaviour in docs/inventory/*.yaml (ADR 0010). Property names map to the YAML keys in camelCase.</summary>
public sealed class RegistryEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<string> Layers { get; set; } = new();
    public bool Wire { get; set; }
    public List<string> Quirk { get; set; } = new();
    public List<string> Adr { get; set; } = new();
    public int Owner { get; set; }
    public string Status { get; set; } = "";
    public string? Exclusion { get; set; }
    public string? Ref { get; set; }
    public string? Note { get; set; }
}

/// <summary>One catalogued upstream JS test in docs/inventory/ledger.yaml.</summary>
public sealed class LedgerRow
{
    public string Key { get; set; } = "";
    public string File { get; set; } = "";
    public int Line { get; set; }
    public string Module { get; set; } = "";
    public bool Randomized { get; set; }
    public int Owner { get; set; }
    public string Outcome { get; set; } = "";
    public string? Reason { get; set; }
    public string? Target { get; set; }
    public string? Fixture { get; set; }
    public string? Note { get; set; }
}

/// <summary>What one C# test declares through <see cref="CoversAttribute"/>, <see cref="PortOfAttribute"/> and <see cref="DivergesAttribute"/>.</summary>
public sealed record TestTag(string Test, CoverLayer Layer, IReadOnlyList<string> Covers, IReadOnlyList<string> Diverges, IReadOnlyList<string> PortOf);

/// <summary>The <c>covers</c> list in a fixture header.</summary>
public sealed record FixtureTag(string Path, IReadOnlyList<string> Covers);

public sealed record PendingMark(int Registry, int Ledger);

public sealed record Violation(string Code, string Message);

public sealed class MatrixRow
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Kind { get; init; } = "";
    public int Owner { get; init; }
    public string Status { get; init; } = "";
    public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CoveredLayers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingReportedLayers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Tests { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Fixtures { get; init; } = Array.Empty<string>();
}

public sealed class TraceabilityInput
{
    public List<RegistryEntry> Entries { get; set; } = new();
    public List<LedgerRow> Ledger { get; set; } = new();
    public List<TestTag> Tests { get; set; } = new();
    public List<FixtureTag> Fixtures { get; set; } = new();
    /// <summary>Ledger keys of the tests found in the pinned upstream sources; null skips the gap check.</summary>
    public HashSet<string>? UpstreamKeys { get; set; }
    public PendingMark? PendingMark { get; set; }
    public bool ReleaseGate { get; set; }
    /// <summary>Every ID ever frozen; none may disappear (retire instead).</summary>
    public HashSet<string>? LockedIds { get; set; }
    /// <summary>Lines in a repo-relative file, or -1 when missing; null skips the submodule source check.</summary>
    public Func<string, int>? LineCount { get; set; }
}

public sealed class TraceabilityReport
{
    public List<Violation> Violations { get; } = new();
    public List<MatrixRow> Matrix { get; } = new();
    public int PendingEntries { get; set; }
    public int PendingLedgerRows { get; set; }
}

/// <summary>The ADR 0010 check, as a pure function over the registry, ledger and the tags found in tests and fixtures.</summary>
public static class TraceabilityChecker
{
    public static readonly string[] Kinds = { "match", "diverge", "y-sharp-own", "port-flag", "unsafe-excluded" };
    public static readonly string[] Statuses = { "active", "pending", "excluded", "retired" };
    public static readonly string[] LayerNames = { "unit", "port", "fixture", "differential", "fuzz" };
    public static readonly string[] Exclusions = { "js-undefined", "js-crash", "upstream-defect", "not-applicable" };
    public static readonly string[] Outcomes = { "pending", "ported", "merged-into", "replaced-by-fixture", "skipped" };

    private static readonly Regex IdFormat = new(@"^(?:Q\d{2}|[A-Z]{1,5}-(?:E?\d+(?:\.\d+)?|[A-Z]+))$", RegexOptions.Compiled);
    private static readonly Regex SourceFormat = new(@"^(?<path>.+):(?<line>\d+)$", RegexOptions.Compiled);

    public static bool IsRandomizedTestName(string name) =>
        name.StartsWith("testRepeat", StringComparison.Ordinal) || name == "testInsertAndDeleteAtRandomPositions";

    public static TraceabilityReport Check(TraceabilityInput input)
    {
        var r = new TraceabilityReport();
        void Fail(string code, string msg) => r.Violations.Add(new Violation(code, msg));

        // Registry shape and duplicate IDs.
        var byId = new Dictionary<string, RegistryEntry>(StringComparer.Ordinal);
        foreach (var e in input.Entries)
        {
            if (!byId.TryAdd(e.Id, e)) Fail("TR-DUPLICATE-ID", $"Registry ID {e.Id} is defined more than once.");
            ValidateEntry(e, input.LineCount, Fail);
        }

        if (input.LockedIds != null)
            foreach (var id in input.LockedIds.Where(id => !byId.ContainsKey(id)).OrderBy(x => x, StringComparer.Ordinal))
                Fail("TR-ID-REMOVED", $"Frozen ID {id} is gone from the registry; IDs are never removed or renumbered, set status: retired instead.");

        // Tags: every ID must be a live registry ID.
        bool Live(string id) => byId.TryGetValue(id, out var e) && e.Status != "retired";
        var ledgerKeys = new HashSet<string>(input.Ledger.Select(l => l.Key), StringComparer.Ordinal);
        foreach (var t in input.Tests)
        {
            foreach (var id in t.Covers.Concat(t.Diverges).Distinct())
                if (!Live(id)) Fail("TR-UNKNOWN-TAG", $"Test {t.Test} tags {id}, which is {(byId.ContainsKey(id) ? "retired" : "not in the registry")}.");
            foreach (var k in t.PortOf)
                if (!ledgerKeys.Contains(k)) Fail("TR-UNKNOWN-TAG", $"Test {t.Test} is [PortOf] {k}, which is not a ledger key.");
        }
        foreach (var f in input.Fixtures)
            foreach (var id in f.Covers.Distinct())
                if (!Live(id)) Fail("TR-UNKNOWN-TAG", $"Fixture {f.Path} covers {id}, which is {(byId.ContainsKey(id) ? "retired" : "not in the registry")}.");

        // Coverage maps.
        var testsFor = new Dictionary<string, List<TestTag>>(StringComparer.Ordinal);
        foreach (var t in input.Tests)
            foreach (var id in t.Covers.Concat(t.Diverges).Distinct())
                (testsFor.TryGetValue(id, out var l) ? l : testsFor[id] = new()).Add(t);
        var fixturesFor = new Dictionary<string, List<FixtureTag>>(StringComparer.Ordinal);
        foreach (var f in input.Fixtures)
            foreach (var id in f.Covers.Distinct())
                (fixturesFor.TryGetValue(id, out var l) ? l : fixturesFor[id] = new()).Add(f);

        foreach (var e in byId.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            var tests = testsFor.GetValueOrDefault(e.Id) ?? new List<TestTag>();
            var fixtures = fixturesFor.GetValueOrDefault(e.Id) ?? new List<FixtureTag>();
            var covered = new List<string>();
            if (tests.Any(t => t.Layer is CoverLayer.Unit or CoverLayer.Port)) covered.Add("unit");
            if (tests.Any(t => t.Layer == CoverLayer.Port)) covered.Add("port");
            if (fixtures.Count > 0) covered.Add("fixture");
            if (tests.Any(t => t.Layer == CoverLayer.Differential)) covered.Add("differential");
            if (tests.Any(t => t.Layer == CoverLayer.Fuzz)) covered.Add("fuzz");

            if (e.Status == "active")
            {
                if (tests.Count == 0 && fixtures.Count == 0)
                    Fail("TR-UNCOVERED", $"Active entry {e.Id} has no covering test or fixture.");
                else
                {
                    var need = new List<string>();
                    if (e.Kind is "match" || e.Layers.Contains("unit") || e.Layers.Contains("port")) need.Add("unit");
                    if (e.Layers.Contains("port")) need.Add("port");
                    if (e.Layers.Contains("fixture") || e.Wire) need.Add("fixture");
                    foreach (var layer in need.Distinct().Where(n => !covered.Contains(n)))
                        Fail("TR-MISSING-LAYER", $"Active entry {e.Id} needs a {layer} test but has none.");
                }
                if (e.Kind == "diverge" && !tests.Any(t => t.Diverges.Contains(e.Id)))
                    Fail("TR-NO-DIVERGES", $"Active diverge entry {e.Id} has no [Diverges] test.");
            }

            r.Matrix.Add(new MatrixRow
            {
                Id = e.Id, Title = e.Title, Kind = e.Kind, Owner = e.Owner, Status = e.Status, Layers = e.Layers,
                CoveredLayers = covered,
                MissingReportedLayers = e.Layers.Where(x => x is "differential" or "fuzz" && !covered.Contains(x)).ToArray(),
                Tests = tests.Select(t => t.Test).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                Fixtures = fixtures.Select(f => f.Path).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            });
        }

        CheckLedger(input, ledgerKeys, Fail);

        r.PendingEntries = input.Entries.Count(e => e.Status == "pending");
        r.PendingLedgerRows = input.Ledger.Count(l => l.Outcome == "pending");
        if (input.PendingMark is { } mark)
        {
            if (r.PendingEntries > mark.Registry)
                Fail("TR-PENDING-GREW", $"{r.PendingEntries} pending registry entries exceed the committed high-water mark of {mark.Registry}; the count may only fall.");
            if (r.PendingLedgerRows > mark.Ledger)
                Fail("TR-PENDING-GREW", $"{r.PendingLedgerRows} pending ledger rows exceed the committed high-water mark of {mark.Ledger}; the count may only fall.");
        }
        if (input.ReleaseGate && (r.PendingEntries > 0 || r.PendingLedgerRows > 0))
            Fail("TR-RELEASE-GATE", $"Release gate: {r.PendingEntries} pending entries and {r.PendingLedgerRows} pending ledger rows remain; zero is required.");
        return r;
    }

    private static void ValidateEntry(RegistryEntry e, Func<string, int>? lineCount, Action<string, string> fail)
    {
        void Bad(string why) => fail("TR-INVALID-ENTRY", $"Entry '{e.Id}': {why}");
        if (!IdFormat.IsMatch(e.Id)) Bad("ID does not match the ID format.");
        if (string.IsNullOrWhiteSpace(e.Title)) Bad("title is empty.");
        if (!Kinds.Contains(e.Kind)) Bad($"kind '{e.Kind}' is not one of {string.Join(", ", Kinds)}.");
        if (!Statuses.Contains(e.Status)) Bad($"status '{e.Status}' is not one of {string.Join(", ", Statuses)}.");
        if (e.Owner is < 1 or > 14) Bad($"owner {e.Owner} is not a spec number 1..14.");
        foreach (var l in e.Layers.Where(l => !LayerNames.Contains(l))) Bad($"layer '{l}' is unknown.");
        if (e.Layers.Count == 0) Bad("no layers declared.");
        if (e.Status == "excluded" && (e.Exclusion == null || !Exclusions.Contains(e.Exclusion)))
            Bad($"an excluded entry needs exclusion: one of {string.Join(", ", Exclusions)}.");
        var m = SourceFormat.Match(e.Source ?? "");
        if (!m.Success) { Bad("source must be file:line."); return; }
        var path = m.Groups["path"].Value;
        if (lineCount != null && (path.StartsWith("yjs/", StringComparison.Ordinal) || path.StartsWith("lib0/", StringComparison.Ordinal) || path.StartsWith("y-protocols/", StringComparison.Ordinal)))
        {
            var n = lineCount(path);
            var line = int.Parse(m.Groups["line"].Value);
            if (n < 0) Bad($"source file {path} does not exist.");
            else if (line < 1 || line > n) Bad($"source line {line} is outside {path} ({n} lines).");
        }
    }

    private static void CheckLedger(TraceabilityInput input, HashSet<string> ledgerKeys, Action<string, string> fail)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var portKeys = new HashSet<string>(input.Tests.SelectMany(t => t.PortOf), StringComparer.Ordinal);
        foreach (var row in input.Ledger)
        {
            if (!seen.Add(row.Key)) fail("TR-DUPLICATE-ID", $"Ledger key {row.Key} appears more than once.");
            void Bad(string why) => fail("TR-LEDGER-ROW", $"Ledger row {row.Key}: {why}");
            if (!Outcomes.Contains(row.Outcome)) { Bad($"outcome '{row.Outcome}' is unknown."); continue; }
            if (row.Owner is < 1 or > 14) Bad($"owner {row.Owner} is not a spec number 1..14.");
            var name = row.Key[(row.Key.IndexOf(':') + 1)..];
            if (IsRandomizedTestName(name) != row.Randomized) Bad("randomized flag does not match the test name.");
            if (row.Randomized && row.Owner != 10) Bad("every randomized test belongs to spec 10 (ADR 0019).");
            switch (row.Outcome)
            {
                case "skipped" when string.IsNullOrWhiteSpace(row.Reason): Bad("skipped needs a reason."); break;
                case "merged-into" when row.Target == null || !ledgerKeys.Contains(row.Target): Bad("merged-into needs a target that is a ledger key."); break;
                case "replaced-by-fixture" when string.IsNullOrWhiteSpace(row.Fixture): Bad("replaced-by-fixture needs the fixture path."); break;
                case "ported" when !portKeys.Contains(row.Key): Bad("ported but no test carries [PortOf] for it."); break;
            }
        }
        if (input.UpstreamKeys != null)
        {
            foreach (var k in input.UpstreamKeys.Where(k => !ledgerKeys.Contains(k)).OrderBy(x => x, StringComparer.Ordinal))
                fail("TR-LEDGER-GAP", $"Upstream test {k} has no ledger row.");
            foreach (var k in ledgerKeys.Where(k => !input.UpstreamKeys.Contains(k)).OrderBy(x => x, StringComparer.Ordinal))
                fail("TR-LEDGER-GAP", $"Ledger row {k} matches no upstream test.");
        }
    }
}
