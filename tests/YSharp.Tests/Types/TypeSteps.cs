using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Values;

namespace YSharp.Tests.Types;

/// <summary>
/// The C# half of the step kinds ADR 0016 assigns to the shared types: <c>text.applyDelta</c>, <c>xml.insert</c>,
/// <c>xml.delete</c>, <c>xml.setAttribute</c>, <c>xml.removeAttribute</c> and an <c>observe</c> that records the event payload
/// the Node generator observes. <c>text.insertEmbed</c>, <c>array.insertType</c> and <c>map.insertType</c> come from Spec 5's
/// <see cref="CodecSteps"/> (registered there only when the shared registry lacks them); this registry starts from that one, so
/// none of them is duplicated. <c>text.*</c> on an XML text needs nothing new: <c>ResolveTarget</c> already walks fragments by index.
/// </summary>
internal static class TypeSteps
{
    private static readonly ConditionalWeakTable<OpsContext, List<JsonNode>> Recorded = new();

    /// <summary>The events the <c>observe</c> steps recorded, in firing order, in the shape of the <c>events</c> observation.</summary>
    public static List<JsonNode> Events(OpsContext ctx) => Recorded.GetValue(ctx, _ => new List<JsonNode>());

    /// <summary>
    /// Records an event in firing order, here and in the doc-feature event log, so a script that mixes type observers with <c>subdoc.observe</c>
    /// or <c>undo.*</c> events keeps the one order of the Node side's <c>ctx.events</c>.
    /// </summary>
    public static void Record(OpsContext ctx, JsonNode e)
    {
        Events(ctx).Add(e);
        DocFeatureSteps.Of(ctx).Events.Add(e.ToJsonString());
    }

    public static OpsRegistry CreateRegistry()
    {
        var codec = CodecSteps.CreateRegistry();
        var r = new OpsRegistry();
        foreach (var kind in codec.Kinds.Where(k => k != "observe"))
        {
            codec.TryGet(kind, out var def);
            r.Register(def);
        }

        void Add(string kind, string[] required, string[] optional, Action<OpsContext, OpsStep> run)
        {
            if (!r.TryGet(kind, out _)) r.Register(new StepDefinition(kind, required, optional, run));
        }

        Add("observe", new[] { "doc", "target", "name" }, Array.Empty<string>(), (ctx, s) =>
        {
            var name = s.String("name");
            Target(ctx, s).Observe((e, _) => Record(ctx, EventNode(name, e)));
        });

        Add("text.applyDelta", new[] { "doc", "target", "delta" }, Array.Empty<string>(), (ctx, s) =>
        {
            var text = Target(ctx, s) as YText ?? throw new OpsScriptException("text.applyDelta: target is not a text.");
            text.ApplyDelta(DeltaOps(s.Tagged("delta")));
        });

        Add("xml.insert", new[] { "doc", "target", "at", "kind" }, new[] { "name", "init" }, (ctx, s) =>
        {
            var node = s.String("kind") switch
            {
                "element" => (YType)new YXmlElement(s.String("name")),
                "text" => new YXmlText(s.Has("init") ? s.Tagged("init").String : null),
                var other => throw new OpsScriptException($"xml.insert: unknown kind '{other}'."),
            };
            Fragment(ctx, s).Insert(s.Int("at"), new[] { node });
        });
        Add("xml.delete", new[] { "doc", "target", "at" }, new[] { "len" }, (ctx, s) =>
            Fragment(ctx, s).Delete(s.Int("at"), s.Has("len") ? s.Int("len") : 1));
        Add("xml.setAttribute", new[] { "doc", "target", "name", "value" }, Array.Empty<string>(), (ctx, s) =>
        {
            var value = s.Tagged("value").ToYValue();
            switch (Target(ctx, s))
            {
                case YXmlElement el: el.SetAttribute(s.String("name"), value); break;
                case YXmlText xt: xt.SetAttribute(s.String("name"), value); break;
                default: throw new OpsScriptException("xml.setAttribute: target is not an element or an xml text.");
            }
        });
        Add("xml.removeAttribute", new[] { "doc", "target", "name" }, Array.Empty<string>(), (ctx, s) =>
        {
            switch (Target(ctx, s))
            {
                case YXmlElement el: el.RemoveAttribute(s.String("name")); break;
                case YXmlText xt: xt.RemoveAttribute(s.String("name")); break;
                default: throw new OpsScriptException("xml.removeAttribute: target is not an element or an xml text.");
            }
        });
        return r;
    }

    private static YType Target(OpsContext ctx, OpsStep s) => ctx.ResolveTarget(ctx.Doc(s.String("doc")), s.Element("target"));

    private static YXmlFragment Fragment(OpsContext ctx, OpsStep s) =>
        Target(ctx, s) as YXmlFragment ?? throw new OpsScriptException($"{s.Kind}: target is not an xml fragment or element.");

    /// <summary>A Quill-style delta (<c>insert</c> string or embed, <c>retain</c>, <c>delete</c>, optional <c>attributes</c>) as ops.</summary>
    internal static TextDeltaOp[] DeltaOps(TaggedValue delta)
    {
        if (delta.Kind != TaggedKind.Array) throw new OpsScriptException("text.applyDelta: 'delta' must be an array.");
        var ops = new List<TextDeltaOp>();
        foreach (var item in delta.Items)
        {
            if (item.Kind != TaggedKind.Object) throw new OpsScriptException("text.applyDelta: every delta op must be an object.");
            TaggedValue? Field(string name) => item.Pairs.Where(p => p.Key == name).Select(p => p.Value).FirstOrDefault();
            var attributes = Field("attributes") is { } a ? a.ToYValue() : default;
            if (Field("insert") is { } insert)
            {
                if (insert.Kind == TaggedKind.String) ops.Add(new TextDeltaOp(DeltaKind.Insert, insert.String.Length, insert.String, default, attributes));
                else ops.Add(new TextDeltaOp(DeltaKind.Insert, 1, null, (YEntry)insert.ToYValue(), attributes));
            }
            else if (Field("retain") is { } retain) ops.Add(new TextDeltaOp(DeltaKind.Retain, (int)retain.Number, null, default, attributes));
            else if (Field("delete") is { } del) ops.Add(new TextDeltaOp(DeltaKind.Delete, (int)del.Number));
            else throw new OpsScriptException("text.applyDelta: a delta op needs insert, retain or delete.");
        }
        return ops.ToArray();
    }

    /// <summary>One fired observer as the <c>events</c> observation records it (tools/fixture-gen/lib/steps.mjs, step <c>observe</c>).</summary>
    private static JsonNode EventNode(string observer, YEvent e)
    {
        var o = new JsonObject { ["observer"] = observer, ["type"] = e.GetType().Name };
        var target = e.Target;
        if (target is YText or YArray) o["delta"] = DeltaOf(e);
        if (target is YMap or YXmlFragment or YXmlText)
        {
            var keys = new JsonArray();
            foreach (var kv in e.Keys)
            {
                var change = new JsonObject { ["action"] = kv.Value.Action.ToString().ToLowerInvariant() };
                if (kv.Value.Action != KeyChangeAction.Add && kv.Value.OldValue is { } old) change["oldValue"] = TypeDump.Entry(old);
                keys.Add(new JsonArray(JsonValue.Create(kv.Key), change));
            }
            o["keys"] = keys;
        }
        if (e is YXmlEvent xe)
        {
            o["childListChanged"] = xe.ChildListChanged;
            o["attributesChanged"] = new JsonArray(xe.AttributesChanged.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            o["delta"] = TypeDump.ListDelta(xe.Delta.ToArray());
        }
        return o;
    }

    private static JsonNode DeltaOf(YEvent e) => e switch
    {
        YTextEvent te => TypeDump.TextDelta(te.Delta.ToArray()),
        YArrayEvent ae => TypeDump.ListDelta(ae.Delta.ToArray()),
        _ => throw new InvalidOperationException($"No delta for {e.GetType().Name}."),
    };
}
