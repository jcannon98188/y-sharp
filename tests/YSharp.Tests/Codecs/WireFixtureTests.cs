using System.Linq;
using Xunit;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Codecs;

/// <summary>
/// Replays the Node-generated codec fixtures (fixtures/encoding, fixtures/codecs, fixtures/hostile; ADR 0011) in y-sharp and compares
/// the oracle's update events, full and diff state in V1 and V2, state vectors and update-function results byte for byte. The
/// <c>doc</c>, <c>structs</c> and <c>ds</c> dumps are compared by the type areas (specs 6 and 7), which own the readers for them.
/// The tags repeat each fixture header's <c>covers</c>, so a fixture that gains an ID without a test shows up in review.
/// </summary>
public sealed class WireFixtureTests
{
    private static void RunWire(string path, bool truncation = false) => Stub.Run(() =>
    {
        var fixture = FixtureReplay.Load(path);
        var replay = FixtureReplay.Run(fixture);
        FixtureReplay.AssertWire(fixture, replay);
        if (truncation) FixtureReplay.AssertTruncation(fixture, replay);
    });

    [Fact, Covers("ENC-06", "ENC-07", "ENC-72", "Q01", "Q02", "Q03", "Q04", "Q05", "Q06", "Q08")]
    public void WireFixture_AnyValues_ReplayMatchesTheOracleBytes() => RunWire("encoding/any-values.json");

    [Fact, Covers("ENC-12", "ENC-75", "Q08", "Q11", "Q28", "Q30")]
    public void WireFixture_EmbedsAndFormats_ReplayMatchesTheOracleBytes() => RunWire("encoding/embeds-and-formats.json");

    [Fact, Covers("ENC-05", "ENC-73", "ENC-28", "Q16", "Q17", "Q18", "Q19")]
    public void Strings_ReplayMatchesTheOracleBytes_AndEveryTruncationFails() => RunWire("encoding/strings-utf16.json", truncation: true);

    [Fact, Covers("ENC-02", "ENC-03", "ENC-04", "ENC-08", "ENC-09", "ENC-70", "ENC-71", "ENC-74", "ENC-77", "ENC-78", "ENC-79", "Q12", "Q13", "Q14")]
    public void WireFixture_IdsClocksLengths_ReplayMatchesTheOracleBytes() => RunWire("encoding/ids-clocks-lengths.json");

    [Fact, Covers("ENC-16", "ENC-17", "ENC-18", "ENC-19", "ENC-20", "ENC-80")]
    public void WireFixture_DeleteSets_ReplayMatchesTheOracleBytes() => RunWire("encoding/delete-sets.json");

    [Fact, Covers("ENC-01", "ENC-10", "ENC-11", "ENC-13", "ENC-14", "ENC-15", "ENC-27", "ENC-28", "ENC-76", "ENC-81", "ENC-85", "Q14")]
    public void StructLayouts_ReplayMatchesTheOracleBytes_AndEveryTruncationFails() => RunWire("encoding/struct-layouts.json", truncation: true);

    [Fact, Covers("ENC-23", "ENC-24", "ENC-25", "ENC-81", "ENC-82", "ENC-83", "ENC-84")]
    public void WireFixture_MergeDiffConvertEdges_ReplayMatchesTheOracleBytes() => RunWire("encoding/merge-diff-convert-edges.json");

    [Fact, Covers("ENC-21", "ENC-23", "ENC-24", "ENC-25")]
    public void MergeDiffConvert_Basic_ReplayMatchesTheOracleBytes() => RunWire("codecs/merge-diff-convert.json");

    [Fact, Covers("ENC-21", "ENC-28")]
    public void TextInsertDeleteSync_ReplayMatchesTheOracleBytes_AndEveryTruncationFails() => RunWire("text/insert-delete-sync.json", truncation: true);

    [Fact, Covers("ENC-29", "ENC-30", "ENC-31", "ENC-32", "ENC-33", "ENC-34", "ENC-35", "ENC-37", "ENC-38", "ENC-39", "ENC-40", "ENC-41", "ENC-42", "ENC-43", "ENC-45", "ENC-52", "ENC-53", "ENC-54")]
    public void WireFixture_MalformedV1_FollowsTheStrictDecodingContract() => Stub.Run(() =>
        FixtureReplay.AssertMalformed(FixtureReplay.Load("hostile/malformed-v1-updates.json")));

    [Fact, Covers("ENC-54", "ENC-55", "ENC-56")]
    public void WireFixture_MalformedV2_FollowsTheStrictDecodingContract() => Stub.Run(() =>
        FixtureReplay.AssertMalformed(FixtureReplay.Load("hostile/malformed-v2-updates.json")));

    [Fact, Covers("ENC-22", "ENC-36", "ENC-44", "ENC-46", "ENC-47", "ENC-48", "ENC-49", "ENC-50", "ENC-51", "ENC-56",
        "ENC-60", "ENC-61", "ENC-62", "ENC-63", "ENC-64", "ENC-65", "ENC-66", "ENC-67", "ENC-68", "ENC-69", "Q07")]
    public void WireFixture_MalformedExtras_FollowTheStrictDecodingContract() => Stub.Run(() =>
        FixtureReplay.AssertMalformed(FixtureReplay.Load("hostile/malformed-codec-extras.json")));

    [Fact]
    public void WireFixture_EveryCodecFixture_IsCoveredByATest()
    {
        // every codec fixture file has a replay above; a new one must add a test here
        var expected = new[]
        {
            "encoding/any-values.json", "encoding/embeds-and-formats.json", "encoding/strings-utf16.json", "encoding/ids-clocks-lengths.json",
            "encoding/delete-sets.json", "encoding/struct-layouts.json", "encoding/merge-diff-convert-edges.json", "codecs/merge-diff-convert.json",
            "hostile/malformed-v1-updates.json", "hostile/malformed-v2-updates.json", "hostile/malformed-codec-extras.json",
        };
        var root = YSharp.TestKit.Fixtures.RepoRoot.Find();
        foreach (var rel in expected)
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(root, "fixtures", rel)), rel);
        var onDisk = System.IO.Directory.EnumerateFiles(System.IO.Path.Combine(root, "fixtures", "encoding"), "*.json")
            .Select(p => "encoding/" + System.IO.Path.GetFileName(p)).ToHashSet();
        Assert.True(onDisk.SetEquals(expected.Where(e => e.StartsWith("encoding/"))), "fixtures/encoding changed; add or remove its replay test");
    }
}
