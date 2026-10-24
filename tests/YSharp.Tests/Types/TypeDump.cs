using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using YSharp.TestKit.Values;

namespace YSharp.Tests.Types;

/// <summary>
/// The typed dumps of ADR 0016 written from y-sharp, in the shape the Node generator observes (<c>doc</c> and <c>events</c>
/// observations): <c>{kind:"text", delta}</c>, <c>{kind:"array", items}</c>, <c>{kind:"map", pairs}</c>,
/// <c>{kind:"xmlFragment|xmlElement|xmlText", name?, attrs, children|delta}</c>. Scalars are ADR 0011 tagged values, so -0, NaN,
/// lone surrogates and BigInt are distinguished.
/// </summary>
internal static class TypeDump
{
    public static JsonNode Tagged(TaggedValue v) => JsonNode.Parse(v.ToJson())!;

    public static TaggedValue ToTagged(YValue v)
    {
        switch (v.Kind)
        {
            case YValueKind.Undefined: return TaggedValue.Undefined;
            case YValueKind.Null: return TaggedValue.Null;
            case YValueKind.Bool: v.TryGetBool(out var b); return TaggedValue.FromBool(b);
            case YValueKind.Number: v.TryGetNumber(out var n); return TaggedValue.FromNumber(n);
            case YValueKind.BigInt: v.TryGetBigInt(out var big); return TaggedValue.BigInt(big);
            case YValueKind.String: v.TryGetString(out var s); return TaggedValue.FromString(s);
            case YValueKind.Bytes: v.TryGetBytes(out var bytes); return TaggedValue.FromBytes(bytes.ToArray());
            case YValueKind.Array:
            {
                v.TryGetArray(out var items);
                return TaggedValue.FromArray(items.ToArray().Select(ToTagged));
            }
            default:
            {
                v.TryGetObject(out var view);
                var pairs = new List<KeyValuePair<string, TaggedValue>>();
                for (var i = 0; i < view.Count; i++) pairs.Add(new(view[i].Key, ToTagged(view[i].Value)));
                return TaggedValue.FromObject(pairs);
            }
        }
    }

    public static JsonNode Value(YValue v) => Tagged(ToTagged(v));

    /// <summary>A list or map entry: a scalar as a tagged value, a nested type as its dump.</summary>
    public static JsonNode Entry(YEntry e) => e.Kind switch
    {
        YEntryKind.Type => Type(e.Type!),
        YEntryKind.Doc => throw new NotSupportedException("Subdocument entries are not part of the type dumps."),
        _ => Value(e.Value),
    };

    private static JsonNode DeltaOpNode(TextDeltaOp op)
    {
        var o = new JsonObject();
        switch (op.Kind)
        {
            case DeltaKind.Insert:
                o["insert"] = op.Text != null ? Tagged(TaggedValue.FromString(op.Text)) : Entry(op.Embed);
                break;
            case DeltaKind.Retain: o["retain"] = op.Length; break;
            default: o["delete"] = op.Length; break;
        }
        if (op.Attributes.Kind != YValueKind.Undefined) o["attributes"] = Value(op.Attributes);
        return o;
    }

    public static JsonArray TextDelta(IEnumerable<TextDeltaOp> ops)
    {
        var a = new JsonArray();
        foreach (var op in ops) a.Add(DeltaOpNode(op));
        return a;
    }

    public static JsonArray ListDelta(IEnumerable<ListDeltaOp> ops)
    {
        var a = new JsonArray();
        foreach (var op in ops)
        {
            var o = new JsonObject();
            switch (op.Kind)
            {
                case DeltaKind.Insert:
                    var items = new JsonArray();
                    foreach (var e in op.Inserted.ToArray()) items.Add(Entry(e));
                    o["insert"] = items;
                    break;
                case DeltaKind.Retain: o["retain"] = op.Length; break;
                default: o["delete"] = op.Length; break;
            }
            a.Add(o);
        }
        return a;
    }

    private static JsonArray Attrs(IReadOnlyDictionary<string, YValue> attrs)
    {
        var a = new JsonArray();
        foreach (var kv in attrs) a.Add(new JsonArray(JsonValue.Create(kv.Key), Value(kv.Value)));
        return a;
    }

    public static JsonNode Type(YType t)
    {
        var o = new JsonObject();
        switch (t)
        {
            case YXmlText xt:
                o["kind"] = "xmlText";
                o["attrs"] = Attrs(xt.GetAttributes());
                o["delta"] = TextDelta(xt.ToDelta());
                break;
            case YText text:
                o["kind"] = "text";
                o["delta"] = TextDelta(text.ToDelta());
                break;
            case YArray arr:
            {
                o["kind"] = "array";
                var items = new JsonArray();
                foreach (var e in arr.ToArray()) items.Add(Entry(e));
                o["items"] = items;
                break;
            }
            case YMap map:
            {
                o["kind"] = "map";
                var pairs = new JsonArray();
                foreach (var kv in Enumerate(map)) pairs.Add(new JsonArray(JsonValue.Create(kv.Key), Entry(kv.Value)));
                o["pairs"] = pairs;
                break;
            }
            case YXmlElement el:
                o["kind"] = "xmlElement";
                o["name"] = el.NodeName;
                o["attrs"] = Attrs(el.GetAttributes());
                o["children"] = Children(el);
                break;
            case YXmlFragment frag:
                o["kind"] = "xmlFragment";
                o["attrs"] = new JsonArray();
                o["children"] = Children(frag);
                break;
            default:
                o["kind"] = "unsupported";
                o["type"] = t.GetType().Name;
                break;
        }
        return o;
    }

    private static JsonArray Children(YXmlFragment f)
    {
        var a = new JsonArray();
        for (var i = 0; i < f.Length; i++) a.Add(Type(f[i]));
        return a;
    }

    public static IEnumerable<KeyValuePair<string, YEntry>> Enumerate(YMap map)
    {
        var e = map.GetEnumerator();
        while (e.MoveNext()) yield return e.Current;
    }

    /// <summary>The <c>doc</c> observation of one replica: every root of the expected dump, in the same sorted order.</summary>
    public static JsonNode Roots(Doc doc, JsonElement expectedRoots)
    {
        var all = new JsonArray();
        foreach (var pair in expectedRoots.EnumerateArray())
        {
            var name = pair[0].GetString()!;
            var kind = pair[1].GetProperty("kind").GetString();
            YType root = kind switch
            {
                "text" => doc.GetText(name),
                "xmlText" => doc.GetXmlText(name),
                "array" => doc.GetArray(name),
                "map" => doc.GetMap(name),
                "xmlFragment" or "xmlElement" => doc.GetXmlFragment(name),
                _ => throw new InvalidOperationException($"Unknown root kind '{kind}'."),
            };
            all.Add(new JsonArray(JsonValue.Create(name), Type(root)));
        }
        return all;
    }

    /// <summary>
    /// Makes the unordered parts comparable: attribute lists and event key changes carry no meaning in their order (JS Map order
    /// versus a .NET dictionary), so both sides are sorted by key.
    /// </summary>
    public static JsonNode Normalize(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[name];
                    if (child == null) continue;
                    Normalize(child);
                    if (name is "attrs" or "keys" && child is JsonArray list && list.All(x => x is JsonArray { Count: 2 }))
                    {
                        var sorted = list.Select(x => x!.DeepClone()).OrderBy(x => x[0]!.GetValue<string>(), StringComparer.Ordinal).ToList();
                        obj[name] = new JsonArray(sorted.ToArray());
                    }
                }
                break;
            case JsonArray arr:
                foreach (var x in arr) if (x != null) Normalize(x);
                break;
        }
        return node;
    }
}
