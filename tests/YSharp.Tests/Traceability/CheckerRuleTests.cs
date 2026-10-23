using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Traceability;

/// <summary>One test per ADR 0010 failure condition, against synthetic registries.</summary>
public class CheckerRuleTests
{
    private static RegistryEntry E(string id, string status = "active", string kind = "match", string[]? layers = null, bool wire = false, int owner = 5) =>
        new() { Id = id, Title = "t " + id, Source = "docs/x.md:1", Kind = kind, Layers = (layers ?? new[] { "unit" }).ToList(), Wire = wire, Owner = owner, Status = status };

    private static TestTag T(string name, CoverLayer layer, params string[] covers) => new(name, layer, covers, new string[0], new string[0]);

    private static LedgerRow L(string key, int owner = 7, string outcome = "pending", bool randomized = false) =>
        new() { Key = key, File = "yjs/tests/" + key.Split(':')[0], Line = 1, Module = "m", Randomized = randomized, Owner = owner, Outcome = outcome };

    private static TraceabilityInput Input(IEnumerable<RegistryEntry> entries, IEnumerable<TestTag>? tests = null, IEnumerable<LedgerRow>? ledger = null) =>
        new() { Entries = entries.ToList(), Tests = (tests ?? new TestTag[0]).ToList(), Ledger = (ledger ?? new LedgerRow[0]).ToList(), Fixtures = new List<FixtureTag>() };

    private static string[] Codes(TraceabilityInput i) => TraceabilityChecker.Check(i).Violations.Select(v => v.Code).Distinct().ToArray();

    [Fact]
    public void CleanRegistry_HasNoViolations_AndMatrixRowShowsTests()
    {
        var report = TraceabilityChecker.Check(Input(new[] { E("A-1") }, new[] { T("Ns.C.M", CoverLayer.Unit, "A-1") }));
        Assert.Empty(report.Violations);
        Assert.Equal(new[] { "Ns.C.M" }, report.Matrix.Single().Tests);
    }

    [Fact]
    public void DuplicateId_Fails() => Assert.Contains("TR-DUPLICATE-ID", Codes(Input(new[] { E("A-1", "pending"), E("A-1", "pending") })));

    [Fact]
    public void UnknownIdInTag_Fails() =>
        Assert.Contains("TR-UNKNOWN-TAG", Codes(Input(new[] { E("A-1", "pending") }, new[] { T("x", CoverLayer.Unit, "A-2") })));

    [Fact]
    public void RetiredIdInTag_Fails() =>
        Assert.Contains("TR-UNKNOWN-TAG", Codes(Input(new[] { E("A-1", "retired") }, new[] { T("x", CoverLayer.Unit, "A-1") })));

    [Fact]
    public void UnknownIdInFixtureHeader_Fails()
    {
        var i = Input(new[] { E("A-1", "pending") });
        i.Fixtures.Add(new FixtureTag("fixtures/a.json", new[] { "NOPE-1" }));
        Assert.Contains("TR-UNKNOWN-TAG", Codes(i));
    }

    [Fact]
    public void ActiveEntryWithoutTest_Fails_ButPendingPasses()
    {
        Assert.Contains("TR-UNCOVERED", Codes(Input(new[] { E("A-1") })));
        Assert.Empty(Codes(Input(new[] { E("A-1", "pending") })));
    }

    [Fact]
    public void DivergeEntry_NeedsDivergesTest()
    {
        var entry = E("Q17", kind: "diverge");
        Assert.Contains("TR-NO-DIVERGES", Codes(Input(new[] { entry }, new[] { T("x", CoverLayer.Unit, "Q17") })));
        var diverges = new TestTag("y", CoverLayer.Unit, new[] { "Q17" }, new[] { "Q17" }, new string[0]);
        Assert.Empty(Codes(Input(new[] { entry }, new[] { diverges })));
    }

    [Fact]
    public void DeclaredLayerWithoutTest_Fails()
    {
        var entry = E("A-1", layers: new[] { "unit", "port" });
        Assert.Contains("TR-MISSING-LAYER", Codes(Input(new[] { entry }, new[] { T("x", CoverLayer.Unit, "A-1") })));
    }

    [Fact]
    public void DifferentialAndFuzzLayers_AreReportedNotGated()
    {
        var entry = E("A-1", layers: new[] { "unit", "differential", "fuzz" });
        var report = TraceabilityChecker.Check(Input(new[] { entry }, new[] { T("x", CoverLayer.Unit, "A-1") }));
        Assert.Empty(report.Violations);
        Assert.Equal(new[] { "differential", "fuzz" }, report.Matrix.Single().MissingReportedLayers);
    }

    [Fact]
    public void WireEntry_NeedsFixture_EvenIfLayerNotDeclared()
    {
        var entry = E("ENC-01", wire: true);
        var i = Input(new[] { entry }, new[] { T("x", CoverLayer.Unit, "ENC-01") });
        Assert.Contains("TR-MISSING-LAYER", Codes(i));
        i.Fixtures.Add(new FixtureTag("fixtures/a.json", new[] { "ENC-01" }));
        Assert.Empty(Codes(i));
    }

    [Fact]
    public void PortTest_SatisfiesUnitLayer()
    {
        var entry = E("A-1", layers: new[] { "unit" });
        Assert.Empty(Codes(Input(new[] { entry }, new[] { T("x", CoverLayer.Port, "A-1") })));
    }

    [Fact]
    public void LedgerGap_UpstreamTestMissingFromLedger_Fails()
    {
        var i = Input(new RegistryEntry[0], ledger: new[] { L("a.js:testA") });
        i.UpstreamKeys = new HashSet<string> { "a.js:testA", "a.js:testB" };
        Assert.Contains("TR-LEDGER-GAP", Codes(i));
    }

    [Fact]
    public void LedgerRow_NotUpstream_OrDuplicated_Fails()
    {
        var i = Input(new RegistryEntry[0], ledger: new[] { L("a.js:testA"), L("a.js:testA"), L("a.js:testZ") });
        i.UpstreamKeys = new HashSet<string> { "a.js:testA" };
        var codes = Codes(i);
        Assert.Contains("TR-LEDGER-GAP", codes);
        Assert.Contains("TR-DUPLICATE-ID", codes);
    }

    [Fact]
    public void RandomizedLedgerRow_MustBeOwnedBySpec10() =>
        Assert.Contains("TR-LEDGER-ROW", Codes(Input(new RegistryEntry[0], ledger: new[] { L("y-map.tests.js:testRepeatX", owner: 7, randomized: true) })));

    [Fact]
    public void LedgerOutcome_RequiresItsEvidence()
    {
        Assert.Contains("TR-LEDGER-ROW", Codes(Input(new RegistryEntry[0], ledger: new[] { L("a.js:t", outcome: "skipped") })));
        Assert.Contains("TR-LEDGER-ROW", Codes(Input(new RegistryEntry[0], ledger: new[] { L("a.js:t", outcome: "bogus") })));
        Assert.Contains("TR-LEDGER-ROW", Codes(Input(new RegistryEntry[0], ledger: new[] { L("a.js:t", outcome: "ported") })));
        var ok = L("a.js:t", outcome: "ported");
        var tag = new TestTag("x", CoverLayer.Port, new string[0], new string[0], new[] { "a.js:t" });
        Assert.Empty(Codes(Input(new RegistryEntry[0], new[] { tag }, new[] { ok })));
    }

    [Fact]
    public void PortOfUnknownLedgerKey_Fails()
    {
        var tag = new TestTag("x", CoverLayer.Port, new string[0], new string[0], new[] { "nope.js:t" });
        Assert.Contains("TR-UNKNOWN-TAG", Codes(Input(new RegistryEntry[0], new[] { tag }, new[] { L("a.js:t") })));
    }

    [Fact]
    public void PendingCount_MayOnlyFall_AgainstHighWaterMark()
    {
        var i = Input(new[] { E("A-1", "pending"), E("A-2", "pending") });
        i.PendingMark = new PendingMark(1, 0);
        Assert.Contains("TR-PENDING-GREW", Codes(i));
        i.PendingMark = new PendingMark(2, 0);
        Assert.Empty(Codes(i));
    }

    [Fact]
    public void ReleaseGate_RequiresZeroPending()
    {
        var i = Input(new[] { E("A-1", "pending") });
        i.ReleaseGate = true;
        Assert.Contains("TR-RELEASE-GATE", Codes(i));
    }

    [Fact]
    public void LockedIdMissing_Fails()
    {
        var i = Input(new[] { E("A-1", "pending") });
        i.LockedIds = new HashSet<string> { "A-1", "A-2" };
        Assert.Contains("TR-ID-REMOVED", Codes(i));
    }

    [Fact]
    public void Excluded_NeedsAllowedReason_AndInvalidEntriesFail()
    {
        var bad = E("A-1", "excluded");
        Assert.Contains("TR-INVALID-ENTRY", Codes(Input(new[] { bad })));
        var good = E("A-1", "excluded"); good.Exclusion = "js-crash";
        Assert.Empty(Codes(Input(new[] { good })));
        Assert.Contains("TR-INVALID-ENTRY", Codes(Input(new[] { E("nonsense id") })));
        Assert.Contains("TR-INVALID-ENTRY", Codes(Input(new[] { E("A-1", kind: "wat") })));
    }

    [Fact]
    public void SubmoduleSource_MustExist()
    {
        var e = E("A-1", "pending"); e.Source = "yjs/src/nope.js:3";
        var i = Input(new[] { e });
        i.LineCount = p => p == "yjs/src/real.js" ? 10 : -1;
        Assert.Contains("TR-INVALID-ENTRY", Codes(i));
        e.Source = "yjs/src/real.js:11";
        Assert.Contains("TR-INVALID-ENTRY", Codes(i));
        e.Source = "yjs/src/real.js:10";
        Assert.Empty(Codes(i));
    }
}
