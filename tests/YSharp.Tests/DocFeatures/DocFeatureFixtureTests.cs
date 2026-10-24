using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Values;
using YSharp.Tests.Structs;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.DocFeatures;

/// <summary>
/// Replays every doc-feature fixture (<c>fixtures/{doc,subdocs,snapshot,relpos,undo,pud}/*.json</c>, generated from the pinned Yjs by
/// <c>tools/fixture-gen/scenarios/docfeatures.mjs</c>) against y-sharp and compares what the oracle observed: the emitted updates, state
/// vectors, encoded state (V1 and V2), struct store and delete set (through the Spec 6 struct dump seam), text roots, and the named
/// <c>results</c> and <c>events</c> that the doc-feature steps record. This is the C# interpreter side of the ADR 0016 step kinds
/// (<c>gc.run</c>, <c>subdoc.*</c>, <c>snapshot.*</c>, <c>relpos.*</c>, <c>undo.*</c>, <c>pud.*</c>).
/// </summary>
[LibraryIndependent]
public sealed class DocFeatureFixtureTests
{
    private static readonly string Root = RepoRoot.Find();
    private static readonly string[] Areas = { "doc", "subdocs", "snapshot", "relpos", "undo", "pud" };

    public static IEnumerable<object[]> FixtureNames() =>
        Areas.SelectMany(a => Directory.EnumerateFiles(Path.Combine(Root, "fixtures", a), "*.json")
                .Select(p => new object[] { a + "/" + Path.GetFileNameWithoutExtension(p) }))
            .OrderBy(x => (string)x[0], StringComparer.Ordinal);

    [Fact]
    [Covers("DOC-10")]
    public void Fixtures_ExistForEveryArea_AndCoverOnlyOwnedEntries()
    {
        var pinned = PinnedVersions.Read(Root);
        var owned = new[] { "DOC-", "SUB-", "UM-", "SNAP-", "RP-", "PUD-", "GC-" };
        foreach (var area in Areas)
        {
            var files = Directory.EnumerateFiles(Path.Combine(Root, "fixtures", area), "*.json").ToList();
            Assert.NotEmpty(files);
            foreach (var f in files)
            {
                var fx = FixtureLoader.Load(f, pinned);
                Assert.NotEmpty(fx.Header.Covers);
                Assert.All(fx.Header.Covers, id => Assert.True(owned.Any(p => id.StartsWith(p, StringComparison.Ordinal)), $"{f}: {id}"));
            }
        }
    }

    [Fact]
    [Covers("DOC-10")]
    public void EveryDocFeatureStepKind_IsRegisteredOnTheCSharpInterpreter_AndListedInTheSchema()
    {
        var registry = StandardSteps.CreateRegistry();
        foreach (var kind in DocFeatureSteps.Kinds) Assert.True(registry.TryGet(kind, out _), $"step kind {kind} is not registered");

        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tools", "fixture-gen", "schema", "ops.schema.json")));
        var inSchema = schema.RootElement.GetProperty("$defs").GetProperty("step").GetProperty("oneOf").EnumerateArray()
            .ToDictionary(o => o.GetProperty("properties").GetProperty("step").GetProperty("const").GetString()!, o => o, StringComparer.Ordinal);
        foreach (var kind in DocFeatureSteps.Kinds)
        {
            Assert.True(inSchema.TryGetValue(kind, out var def), $"step kind {kind} is not in ops.schema.json");
            registry.TryGet(kind, out var step);
            var schemaFields = def.GetProperty("properties").EnumerateObject().Select(p => p.Name).Where(n => n is not ("step" or "steps")).OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(step.Required.Concat(step.Optional).OrderBy(n => n, StringComparer.Ordinal), schemaFields);
            Assert.Equal(step.Required.OrderBy(n => n, StringComparer.Ordinal),
                def.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).Where(n => n is not ("step" or "steps")).OrderBy(n => n, StringComparer.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    [Covers("DOC-01", "DOC-03", "DOC-04", "DOC-06", "DOC-08", "DOC-09", "DOC-10", "DOC-12", "DOC-15", "DOC-16", "DOC-17")]
    [Covers("SUB-01", "SUB-02", "SUB-03", "SUB-04", "SUB-06", "SUB-07", "SUB-08", "SUB-10", "SUB-11", "SUB-12", "SUB-13")]
    [Covers("SNAP-01", "SNAP-02", "SNAP-05", "SNAP-06", "SNAP-07", "SNAP-08", "SNAP-09")]
    [Covers("RP-01", "RP-02", "RP-03", "RP-04", "RP-05", "RP-06", "RP-08", "RP-09")]
    [Covers("UM-01", "UM-02", "UM-03", "UM-04", "UM-05", "UM-06", "UM-08", "UM-10", "UM-11", "UM-12", "UM-14", "UM-18", "UM-19", "UM-20")]
    [Covers("PUD-01", "PUD-02", "PUD-03", "PUD-04", "PUD-05", "PUD-06", "PUD-08", "PUD-09")]
    [Requires(Feature.DocLifecycle, Feature.Snapshots, Feature.Positions, Feature.Undo, Feature.Subdocs, Feature.UserData, Feature.Text, Feature.TextFormat, Feature.Array, Feature.Map, Feature.Events)]
    public void Fixture_EveryStoredScenario_ReplaysLikeTheOracle(string name)
    {
        var fixture = FixtureLoader.Load(Path.Combine(Root, "fixtures", name + ".json"), PinnedVersions.Read(Root));
        {
            var registry = StandardSteps.CreateRegistry();
            var script = OpsScript.Parse(fixture.Script, registry);
            var ctx = new OpsInterpreter(registry).Run(script);
            Compare(name, fixture.Observations, ctx);
        }
    }

    // ------------------------------------------------------------------ comparison

    private static byte[] BytesOf(JsonElement tagged) => TaggedValue.Read(tagged).Bytes;

    private static void Compare(string name, JsonElement obs, OpsContext ctx)
    {
        foreach (var r in ctx.Docs.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var doc = ctx.Docs[r];
            var where = $"{name}/{r}";

            if (obs.TryGetProperty("structs", out var structs))
            {
                var diff = StructuralDiff.Find(structs.GetProperty("value").GetProperty(r), JsonDocument.Parse(StructsProbe.DumpJson(doc)).RootElement);
                Assert.True(diff == null, $"{where}: struct store differs from the oracle at {diff}");
            }
            if (obs.TryGetProperty("ds", out var ds))
            {
                var dsDiff = StructuralDiff.Find(ds.GetProperty("value").GetProperty(r).GetRawText(), StructsProbe.DeleteSetJson(StructsProbe.Dump(doc)));
                Assert.True(dsDiff == null, $"{where}: delete set differs from the oracle at {dsDiff}");
            }

            var wantUpdates = obs.GetProperty("updates").GetProperty("value").GetProperty(r).GetProperty("v1").EnumerateArray().Select(BytesOf).ToList();
            Assert.True(wantUpdates.Count == ctx.Updates[r].Count, $"{where}: oracle emitted {wantUpdates.Count} update events, y-sharp {ctx.Updates[r].Count}");
            for (var i = 0; i < wantUpdates.Count; i++) HexAssert.Equal(wantUpdates[i], ctx.Updates[r][i], $"{where} update event {i}");

            HexAssert.Equal(BytesOf(obs.GetProperty("sv").GetProperty("value").GetProperty(r).GetProperty("v1")), Net.StateVector(doc), $"{where} state vector");

            var state = obs.GetProperty("state").GetProperty("value").GetProperty(r);
            foreach (var (key, format) in new[] { ("v1", UpdateFormat.V1), ("v2", UpdateFormat.V2) })
            {
                HexAssert.Equal(BytesOf(state.GetProperty(key).GetProperty("full")), doc.EncodeStateAsUpdate(default, format), $"{where} encodeStateAsUpdate {key}");
                foreach (var other in state.GetProperty(key).GetProperty("diff").EnumerateObject())
                    HexAssert.Equal(BytesOf(other.Value), doc.EncodeStateAsUpdate(Net.StateVector(ctx.Docs[other.Name]), format), $"{where} diff against {other.Name} {key}");
            }

            foreach (var root in obs.GetProperty("doc").GetProperty("value").GetProperty(r).EnumerateArray())
            {
                var dumped = root[1];
                if (dumped.GetProperty("kind").GetString() != "text") continue;
                var expected = string.Concat(dumped.GetProperty("delta").EnumerateArray().Select(d => d.GetProperty("insert").ValueKind == JsonValueKind.String ? d.GetProperty("insert").GetString() : ""));
                Assert.Equal(expected, doc.GetText(root[0].GetString()!).ToString());
            }
        }

        var features = DocFeatureSteps.Of(ctx);
        var expectedResults = obs.TryGetProperty("results", out var res) ? res.GetProperty("value").EnumerateObject().ToList() : new List<JsonProperty>();
        Assert.True(expectedResults.Count == features.Results.Count,
            $"{name}: oracle recorded {expectedResults.Count} results, y-sharp {features.Results.Count} ({string.Join(", ", features.Results.Keys)})");
        foreach (var p in expectedResults)
        {
            Assert.True(features.Results.TryGetValue(p.Name, out var actual), $"{name}: result '{p.Name}' was not produced");
            using var actualDoc = JsonDocument.Parse(actual!);
            var diff = StructuralDiff.Find(p.Value.GetProperty("value"), actualDoc.RootElement);
            Assert.True(diff == null, $"{name}: result '{p.Name}' differs from the oracle at {diff}");
        }

        var expectedEvents = obs.TryGetProperty("events", out var ev) ? ev.GetProperty("value").EnumerateArray().ToList() : new List<JsonElement>();
        Assert.True(expectedEvents.Count == features.Events.Count, $"{name}: oracle fired {expectedEvents.Count} recorded events, y-sharp {features.Events.Count}");
        for (var i = 0; i < expectedEvents.Count; i++)
        {
            using var actualDoc = JsonDocument.Parse(features.Events[i]);
            var diff = StructuralDiff.Find(expectedEvents[i], actualDoc.RootElement);
            Assert.True(diff == null, $"{name}: event {i} differs from the oracle at {diff}");
        }
    }
}
