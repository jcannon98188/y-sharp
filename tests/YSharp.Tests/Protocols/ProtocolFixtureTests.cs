using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.Tests.Types;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Values;

namespace YSharp.Tests.Protocols;

/// <summary>
/// Replays the generated awareness, sync and auth fixtures (ADR 0011, 0014) in y-sharp and compares what the oracle recorded:
/// the bytes every writer produced, what every reader did (type, error, bytes consumed, reply), the ordered awareness event log
/// with virtual timestamps, the final state and meta of every awareness, and the documents' update events and state.
/// </summary>
public sealed class ProtocolFixtureTests
{
    private static readonly string[] Areas = { "awareness", "sync", "auth" };

    /// <summary>
    /// Fixtures where the oracle's behaviour is a recorded divergence: a bad awareness batch applies its leading entries in JS and
    /// nothing in y-sharp (AW-51, ADR 0005). Awarenesses named <c>partial*</c> are exempt from the state comparison; the
    /// <see cref="AwarenessErrorTests"/> diverge test pins both behaviours.
    /// </summary>
    private const string PartialPrefix = "partial";

    public static IEnumerable<object[]> Fixtures()
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

    internal static OpsContext Replay(Fixture fixture, out Dictionary<string, List<byte[]>> updates)
    {
        var registry = ProtocolSteps.CreateRegistry();
        var script = OpsScript.Parse(fixture.Script, registry);
        var interpreter = new OpsInterpreter(registry);
        var captured = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
        interpreter.DocFactory = (ctx, spec) =>
        {
            var doc = new Doc(new DocOptions { ClientId = spec.ClientId, Gc = spec.Gc, TimeProvider = ctx.Clock });
            var list = new List<byte[]>();
            ctx.Updates[spec.Name] = list;
            captured[spec.Name] = list;
            doc.Update += (u, _, _, _) => list.Add(u.ToArray());
            return doc;
        };
        var result = interpreter.Run(script);
        updates = captured;
        return result;
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void Fixture_ReplaysToTheOraclesBytesResultsEventsAndState(string path) => Stub.Run(() =>
    {
        var fixture = FixtureReplay.Load(path);
        var ctx = Replay(fixture, out var updates);
        var obs = fixture.Observations;
        AssertDocs(fixture, ctx, updates);
        if (obs.TryGetProperty("results", out var results)) AssertResults(fixture, ctx, results.GetProperty("value"));
        AssertEvents(fixture, ctx);
        if (obs.TryGetProperty("awareness", out var aw)) AssertAwareness(fixture, ctx, aw.GetProperty("value"));
    });

    private static void AssertDocs(Fixture fixture, OpsContext ctx, Dictionary<string, List<byte[]>> updates)
    {
        var obs = fixture.Observations;
        foreach (var rep in obs.GetProperty("updates").GetProperty("value").EnumerateObject())
        {
            var want = rep.Value.GetProperty("v1").EnumerateArray().Select(e => TaggedValue.Read(e).Bytes).ToList();
            Assert.Equal(want.Count, updates[rep.Name].Count);
            for (var i = 0; i < want.Count; i++) HexAssert.Equal(want[i], updates[rep.Name][i], $"{fixture.Path} update {i} of {rep.Name}");
        }
        foreach (var rep in obs.GetProperty("state").GetProperty("value").EnumerateObject())
            HexAssert.Equal(TaggedValue.Read(rep.Value.GetProperty("v1").GetProperty("full")).Bytes, ctx.Doc(rep.Name).EncodeStateAsUpdate(), $"{fixture.Path} state of {rep.Name}");
        foreach (var rep in obs.GetProperty("sv").GetProperty("value").EnumerateObject())
            HexAssert.Equal(TaggedValue.Read(rep.Value.GetProperty("v1")).Bytes, Wire.StateVector(ctx.Doc(rep.Name)), $"{fixture.Path} sv of {rep.Name}");
    }

    private static void AssertResults(Fixture fixture, OpsContext ctx, JsonElement expected)
    {
        var actual = ProtocolSteps.Of(ctx).Results;
        Assert.Equal(expected.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal), actual.Keys.OrderBy(n => n, StringComparer.Ordinal));
        foreach (var r in expected.EnumerateObject())
        {
            var got = actual[r.Name];
            var label = $"{fixture.Path} result {r.Name}";
            var threw = r.Value.GetProperty("threw").GetBoolean();
            Assert.True(threw == got.Threw, $"{label}: oracle {(threw ? "threw" : "did not throw")}, y-sharp {(got.Threw ? "failed" : "succeeded")}");
            if (r.Value.TryGetProperty("bytes", out var bytes) && !threw)
                HexAssert.Equal(TaggedValue.Read(bytes).Bytes, got.Bytes ?? Array.Empty<byte>(), label);
            if (r.Value.TryGetProperty("handlerCalled", out var handler))
                Assert.True(handler.GetBoolean() == got.HandlerCalled, $"{label}: error handler called differs");
            if (r.Value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Number)
                Assert.Equal(type.GetInt32(), got.Type);
            if (r.Value.TryGetProperty("consumed", out var consumed) && !threw && !(r.Value.GetProperty("handlerCalled").GetBoolean()))
                Assert.Equal(consumed.GetInt32(), got.Consumed);
        }
    }

    private static void AssertEvents(Fixture fixture, OpsContext ctx)
    {
        var actual = ProtocolSteps.Of(ctx).Events;
        if (!fixture.Observations.TryGetProperty("events", out var events))
        {
            Assert.Empty(actual);
            return;
        }
        var want = JsonNode.Parse(events.GetProperty("value").GetRawText())!.ToJsonString();
        var got = new JsonArray(actual.Select(a => a.DeepClone()).ToArray()).ToJsonString();
        StructuralAssert.Equal(want, got, $"{fixture.Path}: awareness events");
    }

    private static void AssertAwareness(Fixture fixture, OpsContext ctx, JsonElement expected)
    {
        foreach (var a in expected.EnumerateObject())
        {
            if (a.Name.StartsWith(PartialPrefix, StringComparison.Ordinal)) continue;
            var aw = ProtocolSteps.Of(ctx).Awarenesses[a.Name];
            var states = new JsonArray(aw.GetStates().OrderBy(k => k.Key)
                .Select(k => (JsonNode)new JsonArray(JsonValue.Create((long)k.Key), TypeDump.Value(k.Value))).ToArray());
            var meta = new JsonArray(aw.Meta.OrderBy(k => k.Key).Select(k => (JsonNode)new JsonArray(JsonValue.Create((long)k.Key),
                new JsonObject { ["clock"] = k.Value.Clock, ["lastUpdated"] = (long)(k.Value.LastUpdated - ProtocolSteps.Epoch).TotalMilliseconds })).ToArray());
            var got = new JsonObject { ["clientId"] = (long)aw.ClientId, ["states"] = states, ["meta"] = meta }.ToJsonString();
            StructuralAssert.Equal(JsonNode.Parse(a.Value.GetRawText())!.ToJsonString(), got, $"{fixture.Path}: awareness {a.Name}");
        }
    }
}
