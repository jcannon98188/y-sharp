using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Allocation;

/// <summary>
/// BUD-12 (ADR 0009, 0005): an input of N bytes allocates at most k*N plus a fixed amount, asserted here on every input of the fixed
/// hostile corpus (<c>fixtures/hostile</c>, <c>malformed</c> observations). The fuzz project asserts it on every generated input.
/// A violation is a crash-safety bug. k and the fixed amount are recorded with the other bounded limits (<see cref="AllocationBudgets"/>).
/// </summary>
[Requires(Feature.Fuzz)]
[Collection("Allocation")]
[Tier(Tier.Gating)]
public sealed class HostileAllocationTests
{
    public static IEnumerable<object[]> Corpus() => CorpusInputs().Select(i => new object[] { i.File, i.Name, i.Format, i.Bytes });

    private static IEnumerable<(string File, string Name, UpdateFormat Format, byte[] Bytes)> CorpusInputs()
    {
        var dir = Path.Combine(RepoRoot.Find(), "fixtures", "hostile");
        foreach (var path in Directory.EnumerateFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (!json.RootElement.TryGetProperty("observations", out var obs)
                || !obs.TryGetProperty("malformed", out var malformed)
                || !malformed.TryGetProperty("value", out var cases)) continue;
            foreach (var c in cases.EnumerateArray())
            {
                if (!c.TryGetProperty("bytes", out var bytes) || !bytes.TryGetProperty("hex", out var hex)) continue;
                var format = c.TryGetProperty("encoding", out var enc) && enc.GetString() == "v2" ? UpdateFormat.V2 : UpdateFormat.V1;
                yield return (Path.GetFileName(path), c.GetProperty("name").GetString() ?? "?", format, Convert.FromHexString(hex.GetString() ?? ""));
            }
        }
    }

    [Fact]
    [LibraryIndependent]
    public void Corpus_HostileFixtures_IsNotEmpty() => Assert.NotEmpty(CorpusInputs());

    [Theory, Covers("BUD-12")]
    [MemberData(nameof(Corpus))]
    public void HostileInput_AnyMalformedCase_AllocatesAtMostKTimesNPlusAFixedAmount(string file, string name, UpdateFormat format, byte[] input)
    {
#if DEBUG
        Assert.Skip("Allocation tests run only in Release (ADR 0009).");
#endif
        {
            var b = AllocationBudgets.Get("BUD-12");
            Func<string, long> limit = p => b.LimitOf(p) ?? -1;

            new Doc().TryApplyUpdate(input, null, format);   // warm-up: JIT, static tables
            var doc = new Doc();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            doc.TryApplyUpdate(input, null, format);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            if (limit("k") < 0 || limit("fixed") < 0)
                Assert.Skip($"BUD-12 is pending: {file}/{name} ({input.Length} bytes) allocated {allocated}; record k and the fixed amount (measured plus 25%) in AllocationBudgets.Limits.");

            var allowed = limit("k") * input.Length + limit("fixed");
            Assert.True(allocated <= allowed, $"BUD-12: {file}/{name}: {input.Length} bytes allocated {allocated}, allowed {allowed} (k={limit("k")}, fixed={limit("fixed")}). This is a crash-safety bug (ADR 0005).");
        }
    }
}
