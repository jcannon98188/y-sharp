using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using YSharp.Tests.Codecs;
using YSharp.Tests.Types;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Values;

namespace YSharp.Tests.Simulation;

/// <summary>
/// The C# half of the step kinds Spec 10 adds to ADR 0016 (schema <c>tools/fixture-gen/schema/ops.schema.json</c>, Node half
/// <c>tools/fixture-gen/lib/steps-sim.mjs</c>):
/// <list type="bullet">
/// <item><c>deliver {schedule, encoding?}</c>, the delivery schedule: entries <c>sv</c> (capture a state vector, a SyncStep1),
/// <c>reply</c> (capture <c>EncodeStateAsUpdate(doc, sv)</c>, a SyncStep2) and <c>apply</c> (deliver a captured reply by name, or
/// the n-th update event of another replica). Repeating an <c>apply</c> duplicates a delivery, leaving one out drops it, listing
/// them in another order reorders delivery.</item>
/// <item><c>observeDeep {doc, target, name}</c>, the deep-observer recorder behind Q25.</item>
/// <item><c>text.insertEmbedType {doc, target, at, kind, init?, attributes?}</c>, a shared type as a text embed.</item>
/// </list>
/// Everything else comes from <see cref="TypeSteps"/> (which includes the codec and doc-feature steps).
/// </summary>
internal static class SimSteps
{
    private sealed class Mail
    {
        public Dictionary<string, byte[]> Svs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, byte[]> Replies { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<byte[]>> V1 { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<byte[]>> V2 { get; } = new(StringComparer.Ordinal);
    }

    private static readonly ConditionalWeakTable<OpsContext, Mail> State = new();

    private static Mail MailOf(OpsContext ctx) => State.GetValue(ctx, _ => new Mail());

    public static OpsRegistry CreateRegistry()
    {
        var r = TypeSteps.CreateRegistry();
        r.Register(new StepDefinition("deliver", new[] { "schedule" }, new[] { "encoding" }, Deliver));
        r.Register(new StepDefinition("observeDeep", new[] { "doc", "target", "name" }, Array.Empty<string>(), (ctx, s) =>
        {
            var name = s.String("name");
            ctx.ResolveTarget(ctx.Doc(s.String("doc")), s.Element("target")).ObserveDeep((events, _) =>
            {
                var list = new JsonArray();
                foreach (var e in events)
                {
                    var path = new JsonArray();
                    foreach (var seg in e.GetPath()) path.Add(seg.Key != null ? JsonValue.Create(seg.Key) : JsonValue.Create(seg.Index));
                    list.Add(new JsonObject { ["type"] = e.GetType().Name, ["path"] = path });
                }
                TypeSteps.Events(ctx).Add(new JsonObject { ["observer"] = name, ["deep"] = true, ["events"] = list });
            });
        }));
        r.Register(new StepDefinition("text.insertEmbedType", new[] { "doc", "target", "at", "kind" }, new[] { "init", "attributes" }, (ctx, s) =>
        {
            var text = ctx.ResolveTarget(ctx.Doc(s.String("doc")), s.Element("target")) as YText
                       ?? throw new OpsScriptException("text.insertEmbedType: target is not a text.");
            text.InsertEmbed(s.Int("at"), MakeType(s), s.Has("attributes") ? s.Tagged("attributes").ToYValue() : default);
        }));
        return r;
    }

    private static YType MakeType(OpsStep s)
    {
        var init = s.Has("init") ? s.Tagged("init") : null;
        switch (s.String("kind"))
        {
            case "text":
                return new YText(init is { Kind: TaggedKind.String } ? init.String : null);
            case "array":
                return YArray.From(init is { Kind: TaggedKind.Array } ? init.Items.Select(v => (YEntry)v.ToYValue()) : Array.Empty<YEntry>());
            case "map":
                return new YMap(init is { Kind: TaggedKind.Object }
                    ? init.Pairs.Select(p => new KeyValuePair<string, YEntry>(p.Key, p.Value.ToYValue()))
                    : Array.Empty<KeyValuePair<string, YEntry>>());
            default:
                throw new OpsScriptException($"text.insertEmbedType: unknown kind '{s.String("kind")}'.");
        }
    }

    private static readonly Dictionary<string, string[]> EntryFields = new(StringComparer.Ordinal)
    {
        ["sv"] = new[] { "do", "doc", "name" },
        ["reply"] = new[] { "do", "doc", "sv", "name" },
        ["apply"] = new[] { "do", "doc", "update" },
    };

    private static void Deliver(OpsContext ctx, OpsStep s)
    {
        var format = (s.OptString("encoding") ?? "v1") switch
        {
            "v1" => UpdateFormat.V1,
            "v2" => UpdateFormat.V2,
            var e => throw new OpsScriptException($"deliver: unknown encoding '{e}'."),
        };
        var mail = MailOf(ctx);
        var schedule = s.Element("schedule");
        if (schedule.ValueKind != JsonValueKind.Array) throw new OpsScriptException("deliver: 'schedule' must be an array.");
        foreach (var entry in schedule.EnumerateArray())
        {
            string Str(string field) =>
                entry.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!
                : throw new OpsScriptException($"deliver: entry needs a string '{field}'.");

            var kind = Str("do");
            if (!EntryFields.TryGetValue(kind, out var allowed)) throw new OpsScriptException($"deliver: unknown entry '{kind}'.");
            foreach (var p in entry.EnumerateObject())
                if (!allowed.Contains(p.Name)) throw new OpsScriptException($"deliver: {kind} entry has unknown field '{p.Name}'.");

            var doc = ctx.Doc(Str("doc"));
            switch (kind)
            {
                case "sv":
                {
                    var name = Str("name");
                    var w = new ArrayBufferWriter<byte>();
                    doc.EncodeStateVector(w);
                    if (!mail.Svs.TryAdd(name, w.WrittenSpan.ToArray())) throw new OpsScriptException($"deliver: state vector '{name}' is captured twice.");
                    break;
                }
                case "reply":
                {
                    var name = Str("name");
                    if (!mail.Svs.TryGetValue(Str("sv"), out var sv)) throw new OpsScriptException($"deliver: state vector '{Str("sv")}' was never captured.");
                    if (!mail.Replies.TryAdd(name, doc.EncodeStateAsUpdate(sv, format))) throw new OpsScriptException($"deliver: reply '{name}' is captured twice.");
                    break;
                }
                default:
                {
                    var update = entry.GetProperty("update");
                    byte[] bytes;
                    if (update.ValueKind == JsonValueKind.String)
                    {
                        var name = update.GetString()!;
                        if (!mail.Replies.TryGetValue(name, out bytes!)) throw new OpsScriptException($"deliver: reply '{name}' was never captured.");
                    }
                    else if (update.ValueKind == JsonValueKind.Object)
                    {
                        var from = update.GetProperty("from").GetString()!;
                        var index = update.GetProperty("index").GetInt32();
                        var lists = format == UpdateFormat.V1 ? mail.V1 : mail.V2;
                        if (!lists.TryGetValue(from, out var list)) throw new OpsScriptException($"deliver: unknown replica '{from}'.");
                        if (index < 0 || index >= list.Count) throw new OpsScriptException($"deliver: replica '{from}' has no update event #{index}.");
                        bytes = list[index];
                    }
                    else throw new OpsScriptException("deliver: 'update' must be a reply name or {from, index}.");
                    doc.ApplyUpdate(bytes, "deliver", format);
                    break;
                }
            }
        }
    }

    /// <summary>Replays a script on y-sharp with <see cref="CreateRegistry"/>, recording both update formats of every replica.</summary>
    public static Replay Run(JsonElement script)
    {
        var registry = CreateRegistry();
        var parsed = OpsScript.Parse(script, registry);
        var interpreter = new OpsInterpreter(registry);
        interpreter.DocFactory = (ctx, spec) =>
        {
            var doc = new Doc(new DocOptions { ClientId = spec.ClientId, Gc = spec.Gc, TimeProvider = ctx.Clock });
            var mail = MailOf(ctx);
            var l1 = new List<byte[]>();
            var l2 = new List<byte[]>();
            ctx.Updates[spec.Name] = l1;
            mail.V1[spec.Name] = l1;
            mail.V2[spec.Name] = l2;
            doc.Update += (u, _, _, _) => l1.Add(u.ToArray());
            doc.UpdateV2 += (u, _, _, _) => l2.Add(u.ToArray());
            return doc;
        };
        var ctx = interpreter.Run(parsed);
        var m = MailOf(ctx);
        return new Replay(ctx, m.V1, m.V2);
    }

    public static Replay Run(Fixture fixture) => Run(fixture.Script);
}
