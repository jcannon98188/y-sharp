using System;
using System.Buffers;
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
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Structs;

/// <summary>
/// Replays every <c>fixtures/structs/*.json</c> (generated from the pinned Yjs by <c>tools/fixture-gen/scenarios/structs.mjs</c>)
/// against y-sharp and compares what the oracle observed: the struct store, the delete set, the emitted updates, the state
/// vector, the encoded state (V1 and V2, full and diffed against every other replica), pure-function results and text roots.
/// A struct-level difference is reported as a path into the dump (ADR 0012), not as a byte offset.
/// </summary>
[Requires(Feature.V1)]
public sealed class StructsFixtureTests
{
    private static readonly string Root = RepoRoot.Find();
    private static readonly string Dir = Path.Combine(Root, "fixtures", "structs");

    public static IEnumerable<object[]> FixtureNames() =>
        Directory.EnumerateFiles(Dir, "*.json").Select(p => new object[] { Path.GetFileNameWithoutExtension(p) }).OrderBy(x => (string)x[0], StringComparer.Ordinal);

    [Fact]
    [LibraryIndependent]
    public void Fixtures_AnyHeader_ExistAndCoverRegistryEntriesOfSpec6()
    {
        var pinned = PinnedVersions.Read(Root);
        var files = Directory.EnumerateFiles(Dir, "*.json").ToList();
        Assert.True(files.Count >= 20, "expected the structs fixture set");
        foreach (var f in files)
        {
            var fx = FixtureLoader.Load(f, pinned);
            Assert.NotEmpty(fx.Header.Covers);
            Assert.All(fx.Header.Covers, id => Assert.True(id.StartsWith("STR-", StringComparison.Ordinal) || id.StartsWith("GC-", StringComparison.Ordinal) || id is "Q20" or "Q21", $"{f}: {id}"));
            Assert.True(fx.Observations.TryGetProperty("structs", out _) && fx.Observations.TryGetProperty("ds", out _), $"{f}: structs and ds observations are required");
        }
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    [Covers("STR-29", "STR-38", "STR-39", "STR-42", "STR-43", "STR-44", "STR-61", "STR-62", "STR-63", "STR-64", "STR-65", "STR-66", "STR-67", "STR-68", "GC-06", "Q20", "Q21")]
    [Requires(Feature.Text, Feature.Array, Feature.Map, Feature.V2, Feature.UpdateFunctions, Feature.V2Functions)]
    public void Fixture_EveryStoredScenario_ReplaysLikeTheOracle(string name)
    {
        var fixture = FixtureLoader.Load(Path.Combine(Dir, name + ".json"), PinnedVersions.Read(Root));
        {
            var results = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var registry = CreateRegistry(results);
            var script = OpsScript.Parse(fixture.Script, registry);
            var ctx = new OpsInterpreter(registry).Run(script);
            Compare(name, fixture.Observations, ctx, results);
        }
    }

    // ------------------------------------------------------------------ registry

    /// <summary>Standard steps plus the ones Spec 6 scripts need that no earlier spec has registered (registered only when absent).</summary>
    internal static OpsRegistry CreateRegistry(Dictionary<string, byte[]> results)
    {
        var r = StandardSteps.CreateRegistry();
        if (!r.TryGet("mergeUpdates", out _))
            r.Register(new StepDefinition("mergeUpdates", new[] { "name", "updates" }, new[] { "encoding" }, (ctx, s) =>
            {
                var format = Format(s);
                var list = s.Tagged("updates").Items.Select(v => (ReadOnlyMemory<byte>)v.Bytes).ToList();
                var w = new ArrayBufferWriter<byte>();
                var status = YUpdate.TryMerge(list, w, format);
                Assert.True(status.Ok, $"mergeUpdates {s.String("name")}: {status.Error}");
                results[s.String("name")] = w.WrittenSpan.ToArray();
            }));
        if (!r.TryGet("array.insertType", out _))
            r.Register(new StepDefinition("array.insertType", new[] { "doc", "target", "at", "kind" }, new[] { "init" }, (ctx, s) =>
            {
                var arr = ctx.ResolveTarget(ctx.Doc(s.String("doc")), s.Element("target")) as YArray ?? throw new OpsScriptException("array.insertType: target is not an array.");
                arr.Insert(s.Int("at"), new YEntry[] { MakeType(s) });
            }));
        if (!r.TryGet("map.insertType", out _))
            r.Register(new StepDefinition("map.insertType", new[] { "doc", "target", "key", "kind" }, new[] { "init" }, (ctx, s) =>
            {
                var map = ctx.ResolveTarget(ctx.Doc(s.String("doc")), s.Element("target")) as YMap ?? throw new OpsScriptException("map.insertType: target is not a map.");
                map.Set(s.String("key"), MakeType(s));
            }));
        return r;
    }

    private static UpdateFormat Format(OpsStep s) => (s.OptString("encoding") ?? "v1") == "v2" ? UpdateFormat.V2 : UpdateFormat.V1;

    private static YType MakeType(OpsStep s)
    {
        var init = s.Has("init") ? s.Tagged("init") : null;
        switch (s.String("kind"))
        {
            case "text":
                return new YText(init?.String);
            case "array":
                var a = new YArray();
                if (init != null) a.Push(init.Items.Select(v => (YEntry)v.ToYValue()).ToArray());
                return a;
            case "map":
                return new YMap(init == null
                    ? Array.Empty<KeyValuePair<string, YEntry>>()
                    : init.Pairs.Select(p => new KeyValuePair<string, YEntry>(p.Key, (YEntry)p.Value.ToYValue())).ToArray());
            default:
                throw new OpsScriptException($"insertType: unknown kind '{s.String("kind")}'.");
        }
    }

    // ------------------------------------------------------------------ comparison

    private static byte[] BytesOf(JsonElement tagged) => TaggedValue.Read(tagged).Bytes;

    private static void Compare(string name, JsonElement obs, OpsContext ctx, Dictionary<string, byte[]> results)
    {
        var replicas = ctx.Docs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        foreach (var r in replicas)
        {
            var doc = ctx.Docs[r];
            var where = $"{name}/{r}";

            var dump = StructsProbe.Dump(doc);
            var diff = StructuralDiff.Find(obs.GetProperty("structs").GetProperty("value").GetProperty(r), JsonDocument.Parse(StructsProbe.DumpJson(doc)).RootElement);
            Assert.True(diff == null, $"{where}: struct store differs from the oracle at {diff}");

            var dsDiff = StructuralDiff.Find(obs.GetProperty("ds").GetProperty("value").GetProperty(r).GetRawText(), StructsProbe.DeleteSetJson(dump));
            Assert.True(dsDiff == null, $"{where}: delete set differs from the oracle at {dsDiff}");

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

        if (obs.TryGetProperty("results", out var res))
            foreach (var p in res.GetProperty("value").EnumerateObject())
            {
                Assert.True(results.TryGetValue(p.Name, out var got), $"{name}: result '{p.Name}' was not produced");
                HexAssert.Equal(BytesOf(p.Value.GetProperty("bytes")), got!, $"{name} result {p.Name} ({p.Value.GetProperty("api").GetString()})");
            }
    }
}
