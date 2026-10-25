using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Values;

namespace YSharp.Tests.Codecs;

/// <summary>
/// The V1 codec on the oracle's own bytes, with no shared type: every replica's full V1 state in every generated fixture is applied to a
/// fresh document, which must then encode the same full state, the same diff against every other replica's state vector and the same
/// state vector, byte for byte. Where the fixture records the struct store, the decoded store must match it too.
/// </summary>
[Requires(Feature.V1)]
public sealed class V1RoundTripTests
{
    // Applying these states splits a merged struct where a later struct's origin points inside it; Yjs re-joins the halves when the
    // transaction commits (tryMergeWithLefts), which is the commit merge of spec 15.
    private static readonly HashSet<string> NeedCommitMerge = new(StringComparer.Ordinal) { "sim/map-small.json", "sim/map-medium.json", "sim/map-large.json" };

    private static IEnumerable<string> AllWithState()
    {
        var root = Path.Combine(RepoRoot.Find(), "fixtures");
        foreach (var path in FixtureLoader.Discover(root))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.GetProperty("observations").TryGetProperty("state", out _))
                yield return Path.GetRelativePath(root, path).Replace('\\', '/');
        }
    }

    public static IEnumerable<object[]> FixturesWithState() => AllWithState().Where(f => !NeedCommitMerge.Contains(f)).Select(f => new object[] { f });

    public static IEnumerable<object[]> FixturesNeedingCommitMerge() => AllWithState().Where(NeedCommitMerge.Contains).Select(f => new object[] { f });

    [Theory, Covers("ENC-08", "ENC-10", "ENC-16")]
    [Requires(Feature.Gc)]
    [MemberData(nameof(FixturesNeedingCommitMerge))]
    public void OracleState_WithSplitsTheCommitRejoins_ReencodesToTheSameV1Bytes(string fixture)
    {
        OracleState_AppliedToAFreshDoc_ReencodesToTheSameV1Bytes(fixture);
        OracleState_AppliedToAFreshDoc_HoldsTheOraclesStructStore(fixture);
    }

    private static byte[] BytesOf(JsonElement e) => TaggedValue.Read(e).Bytes;

    [Theory, Covers("ENC-08", "ENC-09", "ENC-10", "ENC-11", "ENC-12", "ENC-13", "ENC-16", "ENC-17", "ENC-19")]
    [MemberData(nameof(FixturesWithState))]
    public void OracleState_AppliedToAFreshDoc_ReencodesToTheSameV1Bytes(string fixture)
    {
        var fx = FixtureReplay.Load(fixture);
        var state = fx.Observations.GetProperty("state").GetProperty("value");
        var svs = fx.Observations.GetProperty("sv").GetProperty("value");
        foreach (var replica in state.EnumerateObject())
        {
            var v1 = replica.Value.GetProperty("v1");
            var full = BytesOf(v1.GetProperty("full"));
            var doc = new Doc();
            var result = doc.TryApplyUpdate(full);
            Assert.True(result.Ok, $"{fixture} {replica.Name}: the oracle's state was not accepted ({result.Error}).");

            HexAssert.Equal(full, doc.EncodeStateAsUpdate(), $"{fixture} {replica.Name} full state");
            HexAssert.Equal(BytesOf(svs.GetProperty(replica.Name).GetProperty("v1")), Wire.StateVector(doc), $"{fixture} {replica.Name} state vector");
            foreach (var other in v1.GetProperty("diff").EnumerateObject())
            {
                var target = BytesOf(svs.GetProperty(other.Name).GetProperty("v1"));
                HexAssert.Equal(BytesOf(other.Value), doc.EncodeStateAsUpdate(target), $"{fixture} {replica.Name} diff against {other.Name}");
            }
        }
    }

    [Theory, Covers("ENC-10", "ENC-11")]
    [MemberData(nameof(FixturesWithState))]
    public void OracleState_AppliedToAFreshDoc_HoldsTheOraclesStructStore(string fixture)
    {
        var fx = FixtureReplay.Load(fixture);
        if (!fx.Observations.TryGetProperty("structs", out var structs)) return;
        var state = fx.Observations.GetProperty("state").GetProperty("value");
        foreach (var replica in structs.GetProperty("value").EnumerateObject())
        {
            var doc = new Doc();
            doc.ApplyUpdate(BytesOf(state.GetProperty(replica.Name).GetProperty("v1").GetProperty("full")));
            using var actual = JsonDocument.Parse(doc.DumpStructs());
            var diff = StructuralDiff.Find(WithoutKeep(replica.Value), WithoutKeep(actual.RootElement));
            Assert.True(diff is null, $"{fixture} {replica.Name}: {diff}");
        }
    }

    /// <summary>The dump without <c>keep</c>: an update carries no keep flags, so a decoded store cannot reproduce them.</summary>
    private static JsonElement WithoutKeep(JsonElement dump)
    {
        var text = JsonSerializer.Serialize(dump.EnumerateArray().Select(c => new object[]
        {
            c[0],
            c[1].EnumerateArray().Select(s => s.EnumerateObject().Where(p => p.Name != "keep").ToDictionary(p => p.Name, p => p.Value)).ToArray(),
        }).ToArray());
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
