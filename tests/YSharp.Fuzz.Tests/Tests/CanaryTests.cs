using System;
using Xunit;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Fuzz.Tests;

/// <summary>
/// HOS-DEEP at document depth (ADR 0013, ADR 0005): documents with 1M nested types, run in the out-of-process canary so a stack overflow
/// is a named failure of one test. Apply, delete, GC, traversal and encode must never recurse on document depth, and a document has no depth cap.
/// The self-tests need no implementation: they prove the canary reports a real stack overflow by name and does not mistake a clean run for one.
/// </summary>
[Requires(Feature.V1)]
public sealed class CanaryTests
{
    // ------------------------------------------------------------------ the harness itself (runs on the stub)

    [Fact]
    [LibraryIndependent]
    public void Canary_CleanRun_IsReportedAsPassed() =>
        Assert.Equal(CanaryVerdict.Passed, CanaryHost.Run("selftest-ok").Verdict);

    [Fact]
    [LibraryIndependent]
    public void Canary_RealStackOverflow_IsReportedByName()
    {
        var run = CanaryHost.Run("selftest-overflow", timeout: TimeSpan.FromSeconds(60));
        Assert.True(run.Verdict == CanaryVerdict.StackOverflow, $"expected StackOverflow, got {run}");
    }

    [Fact]
    [LibraryIndependent]
    public void Canary_ReportsAFailedCaseAsFailed_AndAnUnknownCaseAsUnknown()
    {
        Assert.Equal(CanaryVerdict.Failed, CanaryHost.Run("selftest-fail").Verdict);
        Assert.Equal(CanaryVerdict.UnknownCase, CanaryHost.Run("no-such-case").Verdict);
    }

    [Theory]
    [InlineData(0, "OK done", "", CanaryVerdict.Passed)]
    [InlineData(0, "", "", CanaryVerdict.Crashed)]
    [InlineData(1, "", "FAIL x", CanaryVerdict.Failed)]
    [InlineData(134, "", "Stack overflow.\n   at X", CanaryVerdict.StackOverflow)]
    [InlineData(unchecked((int)0xC00000FD), "", "", CanaryVerdict.StackOverflow)]
    [InlineData(139, "", "", CanaryVerdict.Crashed)]
    [LibraryIndependent]
    public void Canary_ExitCodesAndOutput_AreClassified(int exit, string stdout, string stderr, CanaryVerdict expected) =>
        Assert.Equal(expected, CanaryHost.Classify(exit, stdout, stderr).Verdict);

    // ------------------------------------------------------------------ HOS-DEEP document depth (1M)

    [Fact, Covers("HOS-DEEP", "ERR-11", Layer = CoverLayer.Unit)]
    public void DocumentDepth_1MNestedArrays_ApplyEncodeDeleteAndCollectIteratively() =>
        CanaryHost.AssertPasses("array-chain", 1_000_000);

    [Fact, Covers("HOS-DEEP", "ERR-11", Layer = CoverLayer.Unit)]
    public void DocumentDepth_1MNestedMaps_ApplyEncodeDeleteAndCollectIteratively() =>
        CanaryHost.AssertPasses("map-chain", 1_000_000);

    [Fact, Covers("HOS-DEEP", "ERR-11", Layer = CoverLayer.Unit)]
    public void DocumentDepth_1MNestedTypes_UpdateFunctionsMergeDiffConvertAndStateVectorDoNotRecurse() =>
        CanaryHost.AssertPasses("update-functions", 1_000_000);

    [Fact, Covers("HOS-DEEP", "ERR-11")]
    public void DocumentDepth_FuzzedDepths_NeverOverflow()
    {
        // the fuzz-layer view of the canary: a handful of depths across the range, each in its own process
        foreach (var depth in new[] { 1, 17, 1_000, 65_537, 250_000 })
            CanaryHost.AssertPasses("array-chain", depth);
    }
}
