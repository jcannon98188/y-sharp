using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Types;

/// <summary>
/// Replays the generated fixtures of the shared-type areas (text, array, map, xml, events) in y-sharp and compares what the oracle
/// recorded (ADR 0011): update-event bytes, full and diff state in V1 and V2, state vectors, the typed <c>doc</c> dump of every
/// replica and the events the <c>observe</c> steps captured. The struct-store and delete-set dumps belong to Spec 6.
/// </summary>
[Requires(Feature.TypesCore, Feature.Array, Feature.Map, Feature.Text, Feature.TextFormat, Feature.Xml, Feature.Events)]
public sealed class TypeFixtureTests
{
    private static readonly string[] Areas = { "text", "array", "map", "xml", "events" };

    public static IEnumerable<object[]> TypeFixtures()
    {
        var root = RepoRoot.Find();
        foreach (var area in Areas)
        {
            var dir = Path.Combine(root, "fixtures", area);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                yield return new object[] { $"{area}/{Path.GetFileName(file)}" };
        }
    }

    private static Replay Replay(Fixture fixture)
    {
        var registry = TypeSteps.CreateRegistry();
        var script = OpsScript.Parse(fixture.Script, registry);
        var interpreter = new OpsInterpreter(registry);
        var v2 = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
        interpreter.DocFactory = (ctx, spec) =>
        {
            var doc = new Doc(new DocOptions { ClientId = spec.ClientId, Gc = spec.Gc, TimeProvider = ctx.Clock });
            var l1 = new List<byte[]>();
            var l2 = new List<byte[]>();
            ctx.Updates[spec.Name] = l1;
            v2[spec.Name] = l2;
            doc.Update += (u, _, _, _) => l1.Add(u.ToArray());
            doc.UpdateV2 += (u, _, _, _) => l2.Add(u.ToArray());
            return doc;
        };
        var ctx = interpreter.Run(script);
        return new Replay(ctx, ctx.Updates.ToDictionary(k => k.Key, k => k.Value), v2);
    }

    [Theory, MemberData(nameof(TypeFixtures))]
    public void Fixture_EveryStoredScenario_ReplaysToTheOraclesBytesDumpsAndEvents(string path)
    {
        var fixture = FixtureReplay.Load(path);
        var replay = Replay(fixture);
        FixtureReplay.AssertWire(fixture, replay);
        AssertDocDump(fixture, replay);
        AssertEvents(fixture, replay);
        if (fixture.Observations.TryGetProperty("truncation", out _)) FixtureReplay.AssertTruncation(fixture, replay);
    }

    private static void AssertDocDump(Fixture fixture, Replay replay)
    {
        foreach (var rep in fixture.Observations.GetProperty("doc").GetProperty("value").EnumerateObject())
        {
            var actual = TypeDump.Normalize(TypeDump.Roots(replay.Context.Doc(rep.Name), rep.Value)).ToJsonString();
            var expected = TypeDump.Normalize(JsonNode.Parse(rep.Value.GetRawText())!).ToJsonString();
            StructuralAssert.Equal(expected, actual, $"{fixture.Path}: doc dump of replica {rep.Name}");
        }
    }

    private static void AssertEvents(Fixture fixture, Replay replay)
    {
        var actual = TypeSteps.Events(replay.Context);
        if (!fixture.Observations.TryGetProperty("events", out var events))
        {
            Assert.Empty(actual);
            return;
        }
        var expected = TypeDump.Normalize(JsonNode.Parse(events.GetProperty("value").GetRawText())!).ToJsonString();
        var got = TypeDump.Normalize(new JsonArray(actual.Select(a => a.DeepClone()).ToArray())).ToJsonString();
        StructuralAssert.Equal(expected, got, $"{fixture.Path}: observed events");
    }
}
