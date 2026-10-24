using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Values;

namespace YSharp.Fuzz.Corpus;

/// <summary>One stored hostile input (ADR 0013 Corpus): bytes, the API they go through, the outcomes y-sharp may produce, and the oracle's recorded outcome.</summary>
internal sealed record CorpusCase(
    string File,
    IReadOnlyList<string> Classes,
    string Name,
    string Api,
    UpdateFormat Format,
    byte[] Bytes,
    IReadOnlyList<byte[]> Before,
    IReadOnlyList<byte[]> After,
    IReadOnlyList<string> Expect,
    bool Tiny,
    string OracleOutcome,
    string Classification)
{
    public string Label => $"{File}::{Name}";

    public override string ToString() => Label;
}

internal static class CorpusFiles
{
    public static string FixturesRoot => Path.Combine(RepoRoot.Find(), "fixtures");

    public static string HostileDir => Path.Combine(FixturesRoot, "hostile");

    /// <summary>The fixed corpus: every file in <c>fixtures/hostile</c>. Adding a file is the whole regression test.</summary>
    public static IReadOnlyList<string> Files() =>
        Directory.EnumerateFiles(HostileDir, "*.json").OrderBy(p => p, StringComparer.Ordinal).ToList();

    public static IReadOnlyList<CorpusCase> Load(string path)
    {
        var fixture = FixtureLoader.Load(path, PinnedVersions.Read(RepoRoot.Find()));
        var file = Path.GetFileName(path);
        var classes = fixture.Header.Covers.Where(c => c.StartsWith("HOS-", StringComparison.Ordinal)).ToList();
        var cases = new List<CorpusCase>();
        foreach (var c in fixture.Observations.GetProperty("malformed").GetProperty("value").EnumerateArray())
        {
            var classification = c.GetProperty("classification").GetString() ?? "match";
            var oracle = c.GetProperty("outcome").GetString() ?? "";
            cases.Add(new CorpusCase(
                file,
                classes,
                c.GetProperty("name").GetString()!,
                c.TryGetProperty("api", out var api) ? api.GetString()! : "update",
                c.GetProperty("encoding").GetString() == "v2" ? UpdateFormat.V2 : UpdateFormat.V1,
                TaggedValue.Read(c.GetProperty("bytes")).Bytes,
                ReadBytesList(c, "before"),
                ReadBytesList(c, "after"),
                ReadExpect(c, classification, oracle),
                c.TryGetProperty("limits", out var limits) && limits.GetString() == "tiny",
                oracle,
                classification));
        }
        return cases;
    }

    public static IReadOnlyList<CorpusCase> LoadAll() => Files().SelectMany(Load).ToList();

    private static IReadOnlyList<byte[]> ReadBytesList(JsonElement c, string name) =>
        c.TryGetProperty(name, out var list) ? list.EnumerateArray().Select(e => TaggedValue.Read(e).Bytes).ToList() : Array.Empty<byte[]>();

    /// <summary>
    /// Stored <c>expect</c> where the case has one; otherwise the strict-decoding contract of the older malformed files
    /// (ADR 0005): a <c>match</c> case the oracle accepts is accepted, anything else is rejected with a typed error.
    /// </summary>
    private static IReadOnlyList<string> ReadExpect(JsonElement c, string classification, string oracle)
    {
        if (c.TryGetProperty("expect", out var expect)) return expect.EnumerateArray().Select(e => e.GetString()!).ToList();
        return classification == "match" && oracle == "ok" ? new[] { "ok" } : new[] { "Malformed", "LimitExceeded" };
    }
}

/// <summary>Valid updates and frames harvested from the generated fixtures: the seeds of the truncation sweeps and the byte mutator.</summary>
internal static class SeedUpdates
{
    internal sealed record Seed(string Source, UpdateFormat Format, byte[] Bytes)
    {
        public override string ToString() => $"{Source} ({Format}, {Bytes.Length} bytes)";
    }

    private static readonly Lazy<IReadOnlyList<Seed>> All = new(Harvest);

    public static IReadOnlyList<Seed> Collect() => All.Value;

    private static IReadOnlyList<Seed> Harvest()
    {
        var root = RepoRoot.Find();
        var pinned = PinnedVersions.Read(root);
        var seeds = new List<Seed>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in FixtureLoader.Discover(Path.Combine(root, "fixtures")))
        {
            // the hostile folder holds inputs, not valid seeds
            if (path.Contains(Path.DirectorySeparatorChar + "hostile" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            var fixture = FixtureLoader.Load(path, pinned);
            var rel = Path.GetRelativePath(Path.Combine(root, "fixtures"), path).Replace('\\', '/');
            if (!fixture.Observations.TryGetProperty("updates", out var updates)) continue;
            foreach (var replica in updates.GetProperty("value").EnumerateObject())
                foreach (var (name, format) in new[] { ("v1", UpdateFormat.V1), ("v2", UpdateFormat.V2) })
                    foreach (var u in replica.Value.GetProperty(name).EnumerateArray())
                    {
                        var bytes = TaggedValue.Read(u).Bytes;
                        if (bytes.Length == 0 || !seen.Add(format + Convert.ToHexString(bytes))) continue;
                        seeds.Add(new Seed($"{rel}:{replica.Name}", format, bytes));
                    }
        }
        return seeds;
    }

    private static readonly Lazy<IReadOnlyList<byte[]>> StateVectorSeeds = new(HarvestStateVectors);

    /// <summary>Valid V1 state vectors recorded by the oracle.</summary>
    public static IReadOnlyList<byte[]> StateVectors() => StateVectorSeeds.Value;

    private static IReadOnlyList<byte[]> HarvestStateVectors()
    {
        var root = RepoRoot.Find();
        var pinned = PinnedVersions.Read(root);
        var found = new List<byte[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in FixtureLoader.Discover(Path.Combine(root, "fixtures")))
        {
            var fixture = FixtureLoader.Load(path, pinned);
            if (!fixture.Observations.TryGetProperty("sv", out var sv)) continue;
            foreach (var replica in sv.GetProperty("value").EnumerateObject())
            {
                var bytes = TaggedValue.Read(replica.Value.GetProperty("v1")).Bytes;
                if (seen.Add(Convert.ToHexString(bytes))) found.Add(bytes);
            }
        }
        return found;
    }

    /// <summary>The seeds small enough for an every-offset sweep inside the gating time budget.</summary>
    public static IEnumerable<Seed> Small(int maxBytes = 600) => Collect().Where(s => s.Bytes.Length <= maxBytes);
}
