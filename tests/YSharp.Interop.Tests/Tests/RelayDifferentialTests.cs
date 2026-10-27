using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FsCheck;
using FsCheck.Fluent;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Relay;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Values;

namespace YSharp.Interop.Tests;

/// <summary>
/// The relay tier against the oracle (TASK-5.2): every fold of a <see cref="YRelay"/> must be the bytes Yjs <c>mergeUpdates</c> gives for the
/// relay's previous state and the incoming update, and every answer for a client's state vector the bytes Yjs <c>diffUpdate</c> gives for the
/// merged state, in V1 and V2; and one <c>mergeUpdates</c> over every arrival must be the last fold. Smoke: fixed seeds. Nightly: random seeds within the differential time box.
/// </summary>
[Requires(Feature.V1, Feature.V2, Feature.UpdateFunctions, Feature.V2Functions, Feature.Array, Feature.Map, Feature.Text, Feature.TextFormat, Feature.Events)]
[NeedsNode]
[Tier(Tier.Smoke)]
public sealed class RelayDifferentialTests(OracleHost host)
{
    private static JsonNode BytesNode(byte[] b) => new JsonObject { ["$"] = "bytes", ["hex"] = Hex.Of(b) };

    [Fact, Covers("RLY-2", "RLY-3", "DIFF-01")]
    public void RelayFoldsAndDiffs_MatchMergeUpdatesAndDiffUpdate() =>
        DiffProperty.Check(host, "relay", Arb.From(Gen.Choose(0, int.MaxValue)), (_, seed) => Case(seed), cases: 20);

    private void Case(int seed)
    {
        var format = seed % 2 == 0 ? UpdateFormat.V1 : UpdateFormat.V2;
        var encoding = format == UpdateFormat.V1 ? "v1" : "v2";
        var rng = new Random(seed);
        var updates = RelayWorkload.Updates(seed, 40, format);
        var arrivals = updates.Concat(updates.Where(_ => rng.Next(5) == 0)).OrderBy(_ => rng.Next()).ToList();

        var relay = new YRelay();
        var steps = new JsonArray();
        var expected = new Dictionary<string, byte[]>();
        var state = relay.EncodeStateAsUpdate(default, format);
        for (var i = 0; i < arrivals.Count; i++)
        {
            steps.Add(new JsonObject
            {
                ["step"] = "mergeUpdates", ["name"] = $"fold{i}", ["encoding"] = encoding,
                ["updates"] = new JsonArray(BytesNode(state), BytesNode(arrivals[i])),
            });
            Assert.True(relay.TryApplyUpdate(arrivals[i], format).Ok, $"seed {seed}: fold {i} refused");
            state = relay.EncodeStateAsUpdate(default, format);
            expected[$"fold{i}"] = state;
        }

        // one merge of every arrival is the bytes of folding them one at a time, as a stored log is loaded (TASK-13)
        steps.Add(new JsonObject
        {
            ["step"] = "mergeUpdates", ["name"] = "mergeAll", ["encoding"] = encoding,
            ["updates"] = new JsonArray(arrivals.Select(a => (JsonNode)BytesNode(a)).ToArray()),
        });
        expected["mergeAll"] = state;
        var all = new System.Buffers.ArrayBufferWriter<byte>();
        Assert.True(YUpdate.TryMerge(arrivals.Select(a => (ReadOnlyMemory<byte>)a).ToList(), all, format).Ok, $"seed {seed}: merge refused");
        HexAssert.Equal(state, all.WrittenSpan.ToArray(), $"seed {seed} ({encoding}) one merge against the folds");

        for (var r = 0; r < 3; r++)
        {
            var client = new Doc();
            foreach (var u in updates.Where(_ => rng.Next(3) != 0)) client.ApplyUpdate(u, null, format);
            var sv = client.GetStateVector();
            var svBytes = new System.Buffers.ArrayBufferWriter<byte>();
            sv.Encode(svBytes);
            steps.Add(new JsonObject
            {
                ["step"] = "diffUpdate", ["name"] = $"diff{r}", ["encoding"] = encoding,
                ["update"] = BytesNode(state), ["sv"] = BytesNode(svBytes.WrittenSpan.ToArray()),
            });
            expected[$"diff{r}"] = relay.EncodeStateAsUpdate(svBytes.WrittenSpan, format);
        }

        var script = new JsonObject
        {
            ["schema"] = 1,
            ["replicas"] = new JsonArray(new JsonObject { ["name"] = "d0", ["clientId"] = 1 }),
            ["steps"] = steps,
        };
        var reply = host.RunAsync(script.ToJsonString()).GetAwaiter().GetResult();
        Assert.True(reply.Status == OracleStatus.Ok, $"seed {seed}: oracle {reply.Status}: {reply.Message}");
        var results = reply.Observations!["results"]!["value"]!.AsObject();
        foreach (var (name, bytes) in expected)
        {
            var oracle = TaggedValue.Read(JsonSerializer.SerializeToElement(results[name]!["bytes"])).Bytes;
            HexAssert.Equal(oracle, bytes, $"seed {seed} ({encoding}) {name}");
        }
    }
}
