using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using YSharp.Tests.Codecs;
using YSharp.Tests.Protocols;
using YSharp.Tests.Simulation;
using YSharp.Tests.Types;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Values;

namespace YSharp.Interop;

public enum SharpStatus
{
    Ok,

    /// <summary>The interpreter refused the script (<see cref="OpsScriptException"/>): the script is invalid, not y-sharp.</summary>
    ScriptError,

    /// <summary>y-sharp threw something else (including <see cref="NotImplementedException"/>): y-sharp's fault.</summary>
    Fault,

    /// <summary>y-sharp did not finish within the per-case timeout (ADR 0012 "Hangs"): y-sharp's fault, never the oracle's.</summary>
    Hang,

}

public sealed record SharpReply(SharpStatus Status, JsonObject? Observations, string? Message, Exception? Exception = null);

/// <summary>
/// The y-sharp half of a differential case: replays the same ops script on the C# interpreter (TestKit) and renders the same named
/// observations the oracle returns (ADR 0011: update events, state and diffs, state vectors, typed <c>doc</c> dumps, <c>results</c>). The
/// work runs on a worker thread with a per-case timeout, so a y-sharp hang is reported as y-sharp's fault (ADR 0012).
/// </summary>
public static class SharpSide
{
    public static TimeSpan CaseTimeout => TimeSpan.FromMilliseconds(OracleOptions.EnvMs("YSHARP_CASE_TIMEOUT_MS", 30_000));

    /// <summary>The registry both sides share. Areas that join the differential add their step kinds here (ADR 0012).</summary>
    public static OpsRegistry CreateRegistry()
    {
        var registry = SimSteps.CreateRegistry();
        ProtocolSteps.Register(registry);
        foreach (var extend in Extensions) extend(registry);
        return registry;
    }

    /// <summary>Registrations for step kinds an area adds without touching the harness core.</summary>
    public static List<Action<OpsRegistry>> Extensions { get; } = new();

    /// <summary>Replays a script; <paramref name="extras"/> opts into the <c>structs</c> and <c>ds</c> observations, as the oracle's <c>run</c> does.</summary>
    public static SharpReply Run(string scriptJson, TimeSpan? timeout = null, IEnumerable<string>? extras = null) =>
        RunGuarded(() => Replay(scriptJson, extras?.ToList() ?? new List<string>()), timeout ?? CaseTimeout);

    /// <summary>Runs <paramref name="work"/> on a worker thread and maps its outcome; a thread that overruns is abandoned (it is a background thread).</summary>
    public static SharpReply RunGuarded(Func<JsonObject> work, TimeSpan timeout)
    {
        SharpReply? result = null;
        var thread = new Thread(() =>
        {
            try { result = new SharpReply(SharpStatus.Ok, work(), null); }
            catch (OpsScriptException ex) { result = new SharpReply(SharpStatus.ScriptError, null, ex.Message, ex); }
            catch (Exception ex) { result = new SharpReply(SharpStatus.Fault, null, $"{ex.GetType().Name}: {ex.Message}", ex); }
        }, 16 * 1024 * 1024) { IsBackground = true, Name = "y-sharp differential case" };
        thread.Start();
        if (!thread.Join(timeout)) return new SharpReply(SharpStatus.Hang, null, $"y-sharp did not finish within {timeout.TotalMilliseconds:0} ms.");
        return result!;
    }

    private static JsonObject Replay(string scriptJson, IReadOnlyCollection<string> extras)
    {
        var registry = CreateRegistry();
        var script = OpsScript.Parse(scriptJson, registry);
        var v2 = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
        var interpreter = new OpsInterpreter(registry)
        {
            DocFactory = (ctx, spec) =>
            {
                var doc = new Doc(new DocOptions { ClientId = spec.ClientId, Gc = spec.Gc, TimeProvider = ctx.Clock });
                var l1 = new List<byte[]>();
                var l2 = new List<byte[]>();
                ctx.Updates[spec.Name] = l1;
                v2[spec.Name] = l2;
                SimSteps.Record(ctx, spec.Name, l1, l2);
                doc.Update += (u, _, _, _) => l1.Add(u.ToArray());
                doc.UpdateV2 += (u, _, _, _) => l2.Add(u.ToArray());
                return doc;
            },
        };
        var ctx = interpreter.Run(script);
        return Observe(ctx, v2, RootsOf(JsonNode.Parse(scriptJson)!), extras);
    }

    // ------------------------------------------------------------------ observations (shape of tools/fixture-gen/lib/observe.mjs)

    private static JsonNode Bytes(byte[] b) => JsonNode.Parse(TaggedValue.FromBytes(b).ToJson())!;

    private static JsonObject Entry(string api, JsonNode value) => new() { ["api"] = api, ["value"] = value };

    public static JsonObject Observe(OpsContext ctx, IReadOnlyDictionary<string, List<byte[]>> v2Updates, IReadOnlyList<(string Name, string Kind)> roots,
        IReadOnlyCollection<string>? extras = null)
    {
        var names = ctx.Docs.Keys.ToList();
        var obs = new JsonObject();

        var updates = new JsonObject();
        foreach (var n in names)
            updates[n] = new JsonObject
            {
                ["v1"] = new JsonArray(ctx.Updates[n].Select(Bytes).ToArray()),
                ["v2"] = new JsonArray(v2Updates[n].Select(Bytes).ToArray()),
            };
        obs["updates"] = Entry("y-sharp Doc.Update, Doc.UpdateV2", updates);

        var state = new JsonObject();
        var sv = new JsonObject();
        var dumps = new JsonObject();
        using var rootsDoc = JsonDocument.Parse(new JsonArray(roots.OrderBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => (JsonNode)new JsonArray(r.Name, new JsonObject { ["kind"] = r.Kind })).ToArray()).ToJsonString());
        foreach (var n in names)
        {
            var doc = ctx.Doc(n);
            var perFormat = new JsonObject();
            foreach (var (label, format) in new[] { ("v1", UpdateFormat.V1), ("v2", UpdateFormat.V2) })
            {
                var diff = new JsonObject();
                foreach (var other in names.Where(o => o != n))
                    diff[other] = Bytes(doc.EncodeStateAsUpdate(Wire.StateVector(ctx.Doc(other)), format));
                perFormat[label] = new JsonObject { ["full"] = Bytes(doc.EncodeStateAsUpdate(default, format)), ["diff"] = diff };
            }
            state[n] = perFormat;
            var svBytes = Wire.StateVector(doc);
            sv[n] = new JsonObject { ["v1"] = Bytes(svBytes), ["v2"] = Bytes(svBytes) };
            dumps[n] = TypeDump.Normalize(TypeDump.Roots(doc, rootsDoc.RootElement));
        }
        obs["state"] = Entry("y-sharp Doc.EncodeStateAsUpdate", state);
        obs["sv"] = Entry("y-sharp Doc.EncodeStateVector", sv);
        obs["doc"] = Entry("y-sharp typed walk of every root", dumps);

        var features = DocFeatureSteps.Of(ctx);
        if (features.Events.Count > 0)
            obs["events"] = Entry("y-sharp YType.Observe, ObserveDeep, Doc.SubdocsChanged, UndoManager events", new JsonArray(features.Events.Select(e => JsonNode.Parse(e)).ToArray()));

        var results = new JsonObject();
        foreach (var kv in ctx.ForeignOutcomes)
            results[kv.Key] = new JsonObject
            {
                ["api"] = kv.Value.Format == UpdateFormat.V1 ? "Y.applyUpdate (foreign bytes)" : "Y.applyUpdateV2 (foreign bytes)",
                ["value"] = new JsonObject { ["accepted"] = kv.Value.Accepted },
            };
        foreach (var kv in CodecSteps.ResultBytes(ctx))
            results[kv.Key] = new JsonObject { ["api"] = "y-sharp YUpdate", ["bytes"] = Bytes(kv.Value) };
        foreach (var kv in features.Results)
            results[kv.Key] = new JsonObject { ["api"] = "y-sharp doc features", ["value"] = JsonNode.Parse(kv.Value) };
        var protocol = ProtocolSteps.Of(ctx);
        foreach (var r in protocol.Results.Values)
            results[r.Name] = ProtocolResult(r);
        if (results.Count > 0) obs["results"] = Entry("y-sharp results", results);

        if (protocol.Awarenesses.Count > 0)
            obs["awareness"] = Entry("y-sharp Awareness.ClientId, GetStates, Meta", new JsonObject(protocol.Awarenesses.Select(kv =>
                KeyValuePair.Create(kv.Key, (JsonNode?)AwarenessDump(kv.Value)))));

        if (extras?.Contains("structs") == true)
            obs["structs"] = Entry("y-sharp struct store", new JsonObject(names.Select(n => KeyValuePair.Create(n, (JsonNode?)StoreDump.Structs(ctx.Doc(n))))));
        if (extras?.Contains("ds") == true)
            obs["ds"] = Entry("y-sharp delete set from the struct store", new JsonObject(names.Select(n => KeyValuePair.Create(n, (JsonNode?)StoreDump.DeleteSet(StoreDump.Structs(ctx.Doc(n)))))));
        return obs;
    }

    /// <summary>A protocol result with the fields protocol-steps.mjs records for its step kind.</summary>
    private static JsonObject ProtocolResult(ProtocolResult r)
    {
        var o = new JsonObject { ["api"] = r.Kind };
        if (r.Bytes != null) o["bytes"] = Bytes(r.Bytes);
        switch (r.Kind)
        {
            case "sync.read":
                o["type"] = r.Type;
                o["threw"] = r.Threw;
                o["handlerCalled"] = r.HandlerCalled;
                o["consumed"] = r.Consumed;
                break;
            case "awareness.applyUpdate" or "awareness.encode" or "awareness.modify":
                o["threw"] = r.Threw;
                break;
        }
        return o;
    }

    /// <summary>The <c>awareness</c> observation of one instance: client id, live states and meta, sorted by client id (observe.mjs).</summary>
    private static JsonObject AwarenessDump(Awareness aw) => new()
    {
        ["clientId"] = (long)aw.ClientId,
        ["states"] = new JsonArray(aw.GetStates().OrderBy(k => k.Key)
            .Select(k => (JsonNode)new JsonArray(JsonValue.Create((long)k.Key), TypeDump.Value(k.Value))).ToArray()),
        ["meta"] = new JsonArray(aw.Meta.OrderBy(k => k.Key).Select(k => (JsonNode)new JsonArray(JsonValue.Create((long)k.Key),
            new JsonObject { ["clock"] = k.Value.Clock, ["lastUpdated"] = (long)(k.Value.LastUpdated - ProtocolSteps.Epoch).TotalMilliseconds })).ToArray()),
    };

    /// <summary>Every <c>{root, kind}</c> a script addresses, so each replica's dump covers the same roots (as <c>rootsOf</c> in script.mjs).</summary>
    public static IReadOnlyList<(string Name, string Kind)> RootsOf(JsonNode script)
    {
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(JsonNode? r)
        {
            if (r is not JsonObject o) return;
            var name = o["root"]?.GetValue<string>();
            var kind = o["kind"]?.GetValue<string>();
            if (name == null || kind == null) return;
            if (roots.TryGetValue(name, out var existing) && existing != kind)
                throw new OpsScriptException($"root '{name}' used as both {existing} and {kind}");
            roots[name] = kind;
        }
        void Walk(JsonNode? steps)
        {
            if (steps is not JsonArray list) return;
            foreach (var s in list)
            {
                if (s is not JsonObject o) continue;
                if (o["target"] is JsonArray path) Add(path.Count > 0 ? path[0] : null);
                else Add(o["target"]);
                Walk(o["steps"]);
            }
        }
        Walk(script["steps"]);
        return roots.Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
