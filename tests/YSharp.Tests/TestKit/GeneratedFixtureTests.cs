using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Values;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.TestKit;

/// <summary>
/// The Node-generated fixtures (TASK-2.4) round-trip through the TestKit loader, the ops-script parser and the tagged-value
/// reader. Drift against a regeneration is the CI job's concern; this checks the files agree with the pinned submodules,
/// <c>.nvmrc</c>, the manifest and the normative <c>ops.schema.json</c> vocabulary.
/// </summary>
[LibraryIndependent]
public sealed class GeneratedFixtureTests
{
    private static readonly string Root = RepoRoot.Find();
    private static readonly string FixturesDir = Path.Combine(Root, "fixtures");
    private static readonly string[] ValueFields = { "value", "values", "attributes", "init", "delta", "bytes", "updates", "update", "sv" };

    private static PinnedVersions Pinned => PinnedVersions.Read(Root);

    private static IEnumerable<string> Files => FixtureLoader.Discover(FixturesDir);

    [Fact]
    public void Fixtures_Generated_Exist() => Assert.NotEmpty(Files);

    [Fact]
    public void Fixtures_EveryOne_LoadWithPinnedVersionsAndNvmrcNode()
    {
        var nvmrcMajor = File.ReadAllText(Path.Combine(Root, ".nvmrc")).Trim().TrimStart('v').Split('.')[0];
        foreach (var path in Files)
        {
            var f = FixtureLoader.Load(path, Pinned);
            Assert.NotEmpty(f.Header.Covers);
            Assert.Equal(nvmrcMajor, f.Header.Node);
            Assert.StartsWith("ysharp-fixture-gen ", f.Header.Generator, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Fixtures_EveryScript_ParsesWithWellFormedTaggedValues()
    {
        var registry = RegistryWithSchemaKinds();
        foreach (var path in Files)
        {
            var f = FixtureLoader.Load(path, Pinned);
            var script = OpsScript.Parse(f.Script, registry);
            foreach (var step in Flatten(script.Steps))
                foreach (var field in ValueFields.Where(step.Has))
                    TaggedValue.Read(step.Element(field));
        }
    }

    [Fact]
    public void Fixtures_EveryObservation_HasOnlyReadableTaggedValues()
    {
        foreach (var path in Files)
        {
            var obs = FixtureLoader.Load(path, Pinned).Observations;
            Assert.True(obs.TryGetProperty("malformed", out _) || (obs.TryGetProperty("updates", out _) && obs.TryGetProperty("state", out _)
                && obs.TryGetProperty("sv", out _) && obs.TryGetProperty("doc", out _)), path);
            Walk(obs);
        }
    }

    [Fact]
    public void Fixtures_Manifest_ListsEveryFixtureWithItsHash()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesDir, "index.json")));
        var listed = doc.RootElement.GetProperty("fixtures").EnumerateArray()
            .ToDictionary(e => e.GetProperty("path").GetString()!, e => e);
        var onDisk = Files.Select(p => Path.GetRelativePath(FixturesDir, p).Replace('\\', '/')).ToList();
        Assert.Equal(onDisk.OrderBy(x => x, StringComparer.Ordinal), listed.Keys.OrderBy(x => x, StringComparer.Ordinal));
        foreach (var rel in onDisk)
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(FixturesDir, rel))));
            Assert.Equal(hash, listed[rel].GetProperty("sha256").GetString()!, ignoreCase: true);
        }
    }

    [Fact]
    public void Fixtures_Malformed_RecordOutcomeAndClassification()
    {
        var seen = 0;
        foreach (var path in Files)
        {
            var obs = FixtureLoader.Load(path, Pinned).Observations;
            if (!obs.TryGetProperty("malformed", out var m)) continue;
            foreach (var c in m.GetProperty("value").EnumerateArray())
            {
                seen++;
                // not-run: hostile-corpus cases for entry points the oracle lacks (sync, awareness, state-vector decode; TASK-2.11)
                Assert.Contains(c.GetProperty("outcome").GetString(), new[] { "ok", "throws", "js-crash", "not-run" });
                Assert.Contains(c.GetProperty("classification").GetString(), new[] { "match", "diverge", "js-crash" });
                Assert.Equal(TaggedKind.Bytes, TaggedValue.Read(c.GetProperty("bytes")).Kind);
            }
        }
        Assert.True(seen > 0);
    }

    [Fact]
    public void Fixtures_TruncationOutcomes_CoverEveryProperPrefix()
    {
        var seen = 0;
        foreach (var path in Files)
        {
            var obs = FixtureLoader.Load(path, Pinned).Observations;
            if (!obs.TryGetProperty("truncation", out var t)) continue;
            foreach (var entry in t.GetProperty("value").EnumerateArray())
            {
                seen++;
                var next = 0;
                foreach (var run in entry.GetProperty("cuts").EnumerateArray())
                {
                    Assert.Equal(next, run.GetProperty("from").GetInt32());
                    next = run.GetProperty("to").GetInt32() + 1;
                    Assert.Contains(run.GetProperty("outcome").GetString(), new[] { "ok", "throws", "lenient-accepts" });
                }
                Assert.Equal(entry.GetProperty("length").GetInt32(), next);
            }
        }
        Assert.True(seen > 0);
    }

    private static IEnumerable<OpsStep> Flatten(IEnumerable<OpsStep> steps) =>
        steps.SelectMany(s => new[] { s }.Concat(s.Children is null ? Array.Empty<OpsStep>() : Flatten(s.Children)));

    private static void Walk(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object when e.TryGetProperty("$", out _):
                TaggedValue.Read(e);
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) Walk(p.Value);
                break;
            case JsonValueKind.Array:
                foreach (var i in e.EnumerateArray()) Walk(i);
                break;
        }
    }

    /// <summary>Standard C# steps plus placeholders for every other kind in <c>ops.schema.json</c> (areas implement them later).</summary>
    private static OpsRegistry RegistryWithSchemaKinds()
    {
        var registry = StandardSteps.CreateRegistry();
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tools", "fixture-gen", "schema", "ops.schema.json")));
        foreach (var def in schema.RootElement.GetProperty("$defs").GetProperty("step").GetProperty("oneOf").EnumerateArray())
        {
            var kind = def.GetProperty("properties").GetProperty("step").GetProperty("const").GetString()!;
            if (registry.TryGet(kind, out _)) continue;
            var required = def.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).Where(x => x is not ("step" or "steps")).ToArray();
            var optional = def.GetProperty("properties").EnumerateObject().Select(p => p.Name).Where(n => n is not ("step" or "steps") && !required.Contains(n)).ToArray();
            registry.Register(new StepDefinition(kind, required, optional, (_, _) => throw new NotSupportedException(kind), def.GetProperty("properties").TryGetProperty("steps", out _)));
        }
        return registry;
    }
}
