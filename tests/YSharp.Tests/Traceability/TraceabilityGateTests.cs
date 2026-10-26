using System;
using System.Linq;
using Xunit;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Traceability;

/// <summary>The ADR 0010 gate: reads the real registry, ledger, fixtures, upstream sources and built test assemblies.</summary>
[LibraryIndependent]
public class TraceabilityGateTests
{
    [Fact]
    public void Registry_Ledger_And_Tags_AreConsistent_AndMatrixIsWritten()
    {
        var root = RepoRoot.Find();
        var (assemblies, missing) = TestScanner.FindSolutionTestAssemblies(typeof(TraceabilityGateTests).Assembly, root);
        var upstream = InventoryLoader.ScanUpstreamKeys(root);
        Assert.True(upstream != null, "The yjs and y-protocols submodules must be checked out (git submodule update --init --recursive).");

        var input = new TraceabilityInput
        {
            Entries = InventoryLoader.LoadEntries(root),
            Ledger = InventoryLoader.LoadLedger(root),
            Tests = TestScanner.Scan(assemblies),
            Gates = TestScanner.ScanGates(assemblies),
            Landed = Features.Landed.Select(f => f.ToString()).ToList(),
            Fixtures = InventoryLoader.LoadFixtureTags(root),
            UpstreamKeys = upstream,
            PendingMark = InventoryLoader.LoadHighWater(root),
            LockedIds = InventoryLoader.LoadIdLock(root),
            LineCount = InventoryLoader.LineCounter(root),
            ReleaseGate = Environment.GetEnvironmentVariable("YSHARP_RELEASE_GATE") == "1",
        };
        var report = TraceabilityChecker.Check(input);

        var notes = missing.Select(m => $"{m} is not built next to this test assembly; its tests were not scanned.").ToList();
        var (md, _) = MatrixWriter.Write(MatrixWriter.DefaultDir(root), report, notes);

        Assert.True(report.Violations.Count == 0,
            $"{report.Violations.Count} traceability violation(s) (matrix: {md}):{Environment.NewLine}" +
            string.Join(Environment.NewLine, report.Violations.Take(40).Select(v => $"  {v.Code}: {v.Message}")) +
            (missing.Count > 0 ? Environment.NewLine + "  note: " + string.Join("; ", notes) : ""));
    }

    [Fact]
    public void Inventory_AnyArea_HasExpectedShape()
    {
        var root = RepoRoot.Find();
        var entries = InventoryLoader.LoadEntries(root);
        var ledger = InventoryLoader.LoadLedger(root);

        // ADR 0019 reconcile: 209 yjs test exports + 1 y-protocols test = 210 ledger rows (ADR 0010's 209 is the yjs suite alone).
        Assert.Equal(210, ledger.Count);
        Assert.Equal(209, ledger.Count(l => l.File.StartsWith("yjs/tests/", StringComparison.Ordinal)));
        Assert.Equal(1, ledger.Count(l => l.File.StartsWith("y-protocols/", StringComparison.Ordinal)));
        Assert.Equal(49, ledger.Count(l => l.Randomized));
        Assert.All(ledger.Where(l => l.Randomized), l => Assert.Equal(10, l.Owner));

        foreach (var prefix in new[] { "STR-", "ENC-", "ERR-" })
            Assert.Contains(entries, e => e.Id.StartsWith(prefix, StringComparison.Ordinal));
        Assert.All(entries.Where(e => e.Id.StartsWith("STR-", StringComparison.Ordinal)), e => Assert.Equal(6, e.Owner));
    }
}
