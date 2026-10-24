using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.Tests.Structs;
using YSharp.Tests.Types;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Rng;
using YSharp.TestKit.Sim;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Simulation;

internal static class GoldenSet
{
    /// <summary>Operation counts per generator and size (ADR 0017): map and array 40, 300, 1800; text and quill 30, 100, 300.</summary>
    public static readonly IReadOnlyDictionary<string, int[]> Sizes = new Dictionary<string, int[]>
    {
        ["map"] = new[] { 40, 300, 1800 },
        ["array"] = new[] { 40, 300, 1800 },
        ["text"] = new[] { 30, 100, 300 },
        ["quill"] = new[] { 30, 100, 300 },
    };

    private static readonly string[] SizeNames = { "small", "medium", "large" };

    public static IEnumerable<(string Name, string Kind, int Iterations)> All() =>
        Sizes.SelectMany(kv => kv.Value.Select((n, i) => ($"{kv.Key}-{SizeNames[i]}", kv.Key, n)));

    public static IEnumerable<object[]> Names() => All().Select(g => new object[] { g.Name });

    public static string Path(string name) => $"sim/{name}.json";
}

/// <summary>
/// The 12 golden op-log fixtures (<c>fixtures/sim/&lt;generator&gt;-&lt;size&gt;.json</c>, ADR 0017): the map, array, text and quill generators at
/// seed 1 in three sizes. Each stores the expanded op log, the delivery schedule and the oracle's observations; replaying the log
/// in y-sharp must reproduce update bytes, state, state vectors, the typed dump, the struct store and the delete set of every replica.
/// They are the gating guard against wrong-but-convergent conflict order.
/// </summary>
[Requires(Feature.Sim)]
public sealed class SimGoldenFixtureTests
{
    [Fact]
    [Covers("SIM-13")]
    [LibraryIndependent]
    public void TheTwelveFixtures_Exist_AndAreSeedOneRunsOfTheirGenerator()
    {
        var pinned = PinnedVersions.Read(RepoRoot.Find());
        var names = GoldenSet.All().ToList();
        Assert.Equal(12, names.Count);
        foreach (var g in names)
        {
            var fixture = FixtureLoader.Load(Path.Combine(RepoRoot.Find(), "fixtures", GoldenSet.Path(g.Name)), pinned);
            Assert.Equal(1, fixture.Header.Seed);
            Assert.Contains("SIM-13", fixture.Header.Covers);
            foreach (var observation in new[] { "updates", "state", "sv", "doc", "structs", "ds" })
                Assert.True(fixture.Observations.TryGetProperty(observation, out _), $"{g.Name}: missing observation {observation}");
            Assert.Equal(5, fixture.Script.GetProperty("replicas").GetArrayLength());
        }
    }

    [Theory, MemberData(nameof(GoldenSet.Names), MemberType = typeof(GoldenSet))]
    [Covers("SIM-10", "SIM-11", "SIM-12", "SIM-13", "SIM-14")]
    public void Fixture_EveryStoredScenario_ReplaysToTheOraclesBytesDumpsAndStructs(string name)
    {
        var fixture = FixtureReplay.Load(GoldenSet.Path(name));
        var replay = SimSteps.Run(fixture);
        FixtureReplay.AssertWire(fixture, replay);
        SimAsserts.DocDump(fixture, replay);
        SimAsserts.Structs(fixture, replay);
    }
}

/// <summary>Comparisons of a replay with a fixture's observations shared by the simulation fixture tests.</summary>
internal static class SimAsserts
{
    public static void DocDump(Fixture fixture, Replay replay)
    {
        foreach (var rep in fixture.Observations.GetProperty("doc").GetProperty("value").EnumerateObject())
        {
            var actual = TypeDump.Normalize(TypeDump.Roots(replay.Context.Doc(rep.Name), rep.Value)).ToJsonString();
            var expected = TypeDump.Normalize(JsonNode.Parse(rep.Value.GetRawText())!).ToJsonString();
            StructuralAssert.Equal(expected, actual, $"{fixture.Path}: doc dump of replica {rep.Name}");
        }
    }

    public static void Structs(Fixture fixture, Replay replay)
    {
        foreach (var rep in fixture.Observations.GetProperty("structs").GetProperty("value").EnumerateObject())
        {
            var doc = replay.Context.Doc(rep.Name);
            var diff = StructuralDiff.Find(rep.Value.GetRawText(), StructsProbe.DumpJson(doc));
            Assert.True(diff == null, $"{fixture.Path}: struct store of replica {rep.Name} differs from the oracle at {diff}");
            var ds = fixture.Observations.GetProperty("ds").GetProperty("value").GetProperty(rep.Name).GetRawText();
            var dsDiff = StructuralDiff.Find(ds, StructsProbe.DeleteSetJson(StructsProbe.Dump(doc)));
            Assert.True(dsDiff == null, $"{fixture.Path}: delete set of replica {rep.Name} differs from the oracle at {dsDiff}");
        }
    }

    public static void Events(Fixture fixture, Replay replay)
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

/// <summary>
/// Draw parity (ADR 0017): the C# generators, run at seed 1 through the ported PRNG, connector and <c>applyRandomTests</c>, must produce
/// exactly the op log stored in the golden fixture, edits and delivery schedule alike. That proves the PRNG, the draw order and the
/// generators match the Node port, which <c>tools/fixture-gen/check-sim-draws.mjs</c> proves matches upstream; it is the only place the
/// draw sequence of a whole run is asserted. A mismatch is reported as the first differing path in the script.
/// </summary>
[Requires(Feature.Sim)]
public sealed class SimDrawParityTests
{
    [Theory, MemberData(nameof(GoldenSet.Names), MemberType = typeof(GoldenSet))]
    [Covers("SIM-10", "SIM-11", "SIM-12", "SIM-13", "SIM-14")]
    public void CSharpGenerators_AtSeedOne_ProduceTheFixturesOpLog(string name)
    {
        var (_, kind, iterations) = GoldenSet.All().Single(g => g.Name == name);
        var fixture = FixtureReplay.Load(GoldenSet.Path(name));

        var log = new SimLog(5);
        SimHarness.ApplyRandomTests<object?>(Prng.Create(1), SimDrive.Mods(kind), iterations, internals: new StructsSimInternals(), recorder: log);

        var diff = StructuralDiff.Find(fixture.Script.GetRawText(), log.ToScript().ToJsonString());
        Assert.True(diff == null, $"{name}: the C# run's op log differs from the fixture's at {diff}");
    }
}

/// <summary>
/// The C# side of the <c>deliver {schedule}</c> step (ADR 0016): <c>sv</c> and <c>reply</c> entries capture a SyncStep1/SyncStep2 pair,
/// <c>apply</c> delivers a captured reply or the n-th update event of another replica, and a schedule may reorder, duplicate or drop
/// deliveries. Also <c>text.insertEmbedType</c>, and that the three new kinds match the schema field for field.
/// </summary>
[Requires(Feature.Sim)]
public sealed class DeliverStepTests
{
    private const string Text = """{"root":"t","kind":"text"}""";

    private static string Script(string steps, int replicas = 2)
    {
        var names = new[] { "a", "b", "c" };
        var decl = string.Join(",", names.Take(replicas).Select((n, i) => "{\"name\":\"" + n + "\",\"clientId\":" + (i + 1) + "}"));
        return "{\"schema\":1,\"replicas\":[" + decl + "],\"steps\":[" + steps + "]}";
    }

    private static Replay Run(string script)
    {
        using var json = JsonDocument.Parse(script);
        return SimSteps.Run(json.RootElement.Clone());
    }

    private static string TextOf(Replay r, string replica) => r.Context.Doc(replica).GetText("t").ToString();

    private static string Insert(string doc, int at, string value) =>
        "{\"step\":\"text.insert\",\"doc\":\"" + doc + "\",\"target\":" + Text + ",\"at\":" + at + ",\"value\":\"" + value + "\"}";

    [Fact]
    [Covers("SIM-10")]
    [LibraryIndependent]
    public void NewStepKinds_SchemaAndRegistry_MatchFieldForField()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Find(), "tools", "fixture-gen", "schema", "ops.schema.json")));
        var kinds = schema.RootElement.GetProperty("$defs").GetProperty("step").GetProperty("oneOf").EnumerateArray()
            .ToDictionary(o => o.GetProperty("properties").GetProperty("step").GetProperty("const").GetString()!, o => o, StringComparer.Ordinal);
        var registry = SimSteps.CreateRegistry();
        foreach (var kind in new[] { "deliver", "observeDeep", "text.insertEmbedType" })
        {
            Assert.True(kinds.TryGetValue(kind, out var o), $"{kind} is not in ops.schema.json");
            Assert.True(registry.TryGet(kind, out var def), $"{kind} is not registered");
            var fields = o.GetProperty("properties").EnumerateObject().Select(p => p.Name).Where(n => n != "step").OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(fields, def.Required.Concat(def.Optional).OrderBy(n => n, StringComparer.Ordinal));
            var required = o.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).Where(n => n != "step").OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(required, def.Required.OrderBy(n => n, StringComparer.Ordinal));
        }
    }

    [Fact]
    [Covers("SIM-10", "SIM-14")]
    public void SvReplyApply_IsASyncStep1AndStep2_AndBringsTheReplicaUpToDate()
    {
        var r = Run(Script(Insert("a", 0, "hello") + """
            ,{"step":"deliver","schedule":[
              {"do":"sv","doc":"b","name":"s0"},
              {"do":"reply","doc":"a","sv":"s0","name":"r0"},
              {"do":"apply","doc":"b","update":"r0"}]}
            """));
        Assert.Equal("hello", TextOf(r, "b"));
    }

    [Fact]
    [Covers("SIM-10", "SIM-14")]
    public void AReply_IsComputedWhenItIsCaptured_NotWhenItIsApplied()
    {
        // b asks a for what it is missing, a edits again, only then the reply is applied: b gets the state of the capture
        var r = Run(Script(
            Insert("a", 0, "one") +
            ",{\"step\":\"deliver\",\"schedule\":[{\"do\":\"sv\",\"doc\":\"b\",\"name\":\"s0\"},{\"do\":\"reply\",\"doc\":\"a\",\"sv\":\"s0\",\"name\":\"r0\"}]}," +
            Insert("a", 3, "two") +
            ",{\"step\":\"deliver\",\"schedule\":[{\"do\":\"apply\",\"doc\":\"b\",\"update\":\"r0\"}]}"));
        Assert.Equal("one", TextOf(r, "b"));
        Assert.Equal("onetwo", TextOf(r, "a"));
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    [Covers("SIM-10", "SIM-14")]
    public void UpdateReferences_CanBeReordered_Duplicated_AndDropped(string encoding)
    {
        // a makes updates #0 "x", #1 "y", #2 "z". b gets [2, 0, 0, 1] (reordered, #0 twice), c gets [0, 2] (#1 dropped)
        const string deliver = """
            ,{"step":"deliver","encoding":"ENC","schedule":[
              {"do":"apply","doc":"b","update":{"from":"a","index":2}},
              {"do":"apply","doc":"b","update":{"from":"a","index":0}},
              {"do":"apply","doc":"b","update":{"from":"a","index":0}},
              {"do":"apply","doc":"b","update":{"from":"a","index":1}},
              {"do":"apply","doc":"c","update":{"from":"a","index":0}},
              {"do":"apply","doc":"c","update":{"from":"a","index":2}}]}
            """;
        var r = Run(Script(Insert("a", 0, "x") + "," + Insert("a", 1, "y") + "," + Insert("a", 2, "z") + deliver.Replace("ENC", encoding), replicas: 3));
        Assert.Equal("xyz", TextOf(r, "b"));
        Assert.Equal("x", TextOf(r, "c")); // "z" waits for the dropped "y"
    }

    [Fact]
    [Covers("SIM-10")]
    public void ASchedule_ThatIsNotWellFormed_IsAScriptError()
    {
        void Fails(string schedule, string reason)
        {
            try
            {
                Run(Script("{\"step\":\"deliver\",\"schedule\":[" + schedule + "]}"));
            }
            catch (OpsScriptException ex)
            {
                Assert.Contains(reason, ex.Message);
                return;
            }
            Assert.Fail($"the schedule {schedule} was accepted");
        }

        Fails("""{"do":"nope","doc":"a"}""", "unknown entry");
        Fails("""{"do":"sv","doc":"a","name":"s0","extra":1}""", "unknown field");
        Fails("""{"do":"reply","doc":"a","sv":"never","name":"r0"}""", "never captured");
        Fails("""{"do":"apply","doc":"a","update":"never"}""", "never captured");
        Fails("""{"do":"apply","doc":"a","update":{"from":"zzz","index":0}}""", "unknown replica");
        Fails("""{"do":"apply","doc":"a","update":{"from":"b","index":5}}""", "no update event");
        Fails("""{"do":"sv","doc":"a","name":"s0"},{"do":"sv","doc":"b","name":"s0"}""", "twice");
        var enc = Assert.Throws<OpsScriptException>(() => Run(Script("""{"step":"deliver","encoding":"v3","schedule":[]}""")));
        Assert.Contains("unknown encoding", enc.Message);
    }

    [Fact]
    [Covers("SIM-13")]
    [Requires(Feature.TextFormat, Feature.Text, Feature.Map)]
    public void TextInsertEmbedType_SharedType_IsInsertedAsAnEmbed()
    {
        var r = Run(Script(Insert("a", 0, "ab") + """
            ,{"step":"text.insertEmbedType","doc":"a","target":{"root":"t","kind":"text"},"at":1,"kind":"map","init":{"$":"obj","pairs":[["k","v"]]}}
            """));
        var delta = r.Context.Doc("a").GetText("t").ToDelta();
        Assert.Equal(3, delta.Sum(op => op.Kind == DeltaKind.Insert ? (op.Text?.Length ?? 1) : 0));
        Assert.Contains(delta, op => op.Embed.Kind == YEntryKind.Type && op.Embed.Type is YMap);
    }
}

/// <summary>
/// Q25 (<c>Array.prototype.sort</c> is stable), the fixture layer: a deep observer receives its events sorted by path length, and
/// JS keeps equal-length paths in the order the types changed. <c>fixtures/observers/deep-event-order.json</c> has 24 children changed in
/// reverse order, so an unstable sort (<c>List.Sort</c> is introsort) shows. The <c>observeDeep</c> step records every callback's events
/// with their paths; replay must reproduce the oracle's order.
/// </summary>
[Requires(Feature.Sim)]
public sealed class DeepObserverFixtureTests
{
    private const string Path1 = "observers/deep-event-order.json";

    [Fact]
    [Covers("Q25", "TX-1")]
    public void DeepObserverFixture_Replay_FollowsTheOraclesEventOrder()
    {
        var fixture = FixtureReplay.Load(Path1);
        var replay = SimSteps.Run(fixture);
        FixtureReplay.AssertWire(fixture, replay);
        SimAsserts.DocDump(fixture, replay);
        SimAsserts.Events(fixture, replay);
    }

    [Fact]
    [Covers("Q25")]
    [LibraryIndependent]
    public void OracleOrder_DeepEvents_IsPathLengthThenChangeOrder()
    {
        // what the fixture pins, read straight from the oracle's observation (runs without an implementation)
        var fixture = FixtureReplay.Load(Path1);
        var callbacks = fixture.Observations.GetProperty("events").GetProperty("value").EnumerateArray().ToList();
        var first = callbacks.First(c => c.GetProperty("observer").GetString() == "deepA").GetProperty("events").EnumerateArray()
            .Select(e => string.Join("/", e.GetProperty("path").EnumerateArray().Select(p => p.ToString()))).ToList();
        Assert.Equal(25, first.Count);
        Assert.Equal("", first[0]); // the root's own key change has the shortest path
        Assert.Equal(Enumerable.Range(0, 24).Reverse().Select(i => $"c{i:D2}"), first.Skip(1)); // ties keep the order the children changed
        Assert.All(callbacks, c => Assert.True(c.GetProperty("deep").GetBoolean()));
    }
}
