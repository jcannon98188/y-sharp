using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using YSharp.Fuzz.Corpus;
using YSharp.Fuzz.Guards;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Values;

namespace YSharp.Fuzz.Tests;

/// <summary>
/// Q15 (quirks.md): JS decodes truncated or hostile input leniently. A varint at EOF reads as 0, an exhausted V2 column keeps yielding
/// its last value, a string length past the joined string yields "", and structs integrate before a truncated delete set fails. y-sharp
/// reads strictly (ADR 0005): EOF is always an error and a failed decode changes nothing. The stored oracle outcomes show the JS side:
/// every fixture case classified <c>diverge</c> with divergence Q15 is one the oracle accepts (or leaves half applied) and y-sharp rejects.
/// </summary>
public sealed class LenientDecodingDivergenceTests
{
    private sealed record Q15Case(string File, string Name, UpdateFormat Format, byte[] Bytes, string Oracle);

    private static IReadOnlyList<Q15Case> Cases()
    {
        var root = RepoRoot.Find();
        var pinned = PinnedVersions.Read(root);
        var cases = new List<Q15Case>();
        foreach (var path in CorpusFiles.Files())
        {
            var fixture = FixtureLoader.Load(path, pinned);
            foreach (var c in fixture.Observations.GetProperty("malformed").GetProperty("value").EnumerateArray())
            {
                if (!c.TryGetProperty("divergence", out var d) || d.GetString() != "Q15") continue;
                cases.Add(new Q15Case(Path.GetFileName(path), c.GetProperty("name").GetString()!,
                    c.GetProperty("encoding").GetString() == "v2" ? UpdateFormat.V2 : UpdateFormat.V1,
                    TaggedValue.Read(c.GetProperty("bytes")).Bytes, c.GetProperty("outcome").GetString()!));
            }
        }
        return cases;
    }

    [Fact]
    public void Q15_TheStoredOracleRecordsJsBeingLenient()
    {
        var cases = Cases();
        Assert.True(cases.Count >= 4, $"expected at least 4 recorded Q15 divergences, found {cases.Count}");
        // lenient: the oracle accepts these (ok) or fails only after integrating part of the input (throws on the delete set / last byte)
        Assert.Contains(cases, c => c.Oracle == "ok");
        Assert.Contains(cases, c => c.Format == UpdateFormat.V2);
    }

    [Fact, Covers("Q15", Layer = CoverLayer.Unit), Diverges("Q15", adr: "0005")]
    public void Q15_EveryLenientlyDecodedInput_IsRejectedWithoutChange_InYSharp() => Stub.Run(() =>
    {
        foreach (var c in Cases())
        {
            var applied = Hostile.Apply($"{c.File}::{c.Name}", c.Bytes, c.Format);
            Assert.True(applied.Outcome is "Malformed" or "LimitExceeded",
                $"{c.File}::{c.Name}: JS ({c.Oracle}) is lenient here; y-sharp must reject, but returned {applied.Outcome}");
            Assert.Equal(applied.StateBefore, applied.StateAfter);
            Hostile.AssertPostCondition(applied.Doc, c.Name);
        }
    });

    [Fact, Covers("Q15", Layer = CoverLayer.Unit)]
    public void Q15_AVarintAtEof_IsAnErrorNotZero() => Stub.Run(() =>
    {
        // JS reads these as numClients=1, numStructs=0 and so on; y-sharp does not invent the missing bytes
        foreach (var hex in new[] { "01", "01 01", "01 01 01", "01 01 01 00 04 01 01 74 02 68" })
            Assert.Equal(YError.Malformed, new Doc().TryApplyUpdate(Hex.Bytes(hex)).Error);
    });
}
