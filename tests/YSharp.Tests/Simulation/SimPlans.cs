using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using YSharp.TestKit.Rng;
using YSharp.TestKit.Sim;

namespace YSharp.Tests.Simulation;

/// <summary>What a plan may ask the document about (the upstream generators read these, nothing else).</summary>
internal interface ISimView
{
    /// <summary>JS <c>yarray.length</c> / <c>ytext.length</c> (a text counts embeds).</summary>
    int Length { get; }

    /// <summary>JS <c>ytext.toString().length</c> (embeds do not count).</summary>
    int StringLength { get; }

    /// <summary>Length of the <c>Y.Array</c> at array index <paramref name="index"/>, or -1 when the element there is not a <c>Y.Array</c>.</summary>
    int NestedArrayLength(int index);
}

/// <summary>One operation of a generator: draws from the generator, reads the view, returns the ops steps (stored form) to run.</summary>
internal delegate IReadOnlyList<JsonObject> SimPlan(IPrng gen, string doc, ISimView view, PayloadCounters counters);

/// <summary>
/// The four operation generators of the upstream randomized tests (<c>mapTransactions</c>, <c>arrayTransactions</c>,
/// <c>textChanges</c>, <c>qChanges</c>), written as plans: they consume exactly the draws upstream does (ADR 0017 "keep draws")
/// and return the ops steps instead of touching a document. The run executes the steps through the ops interpreter and records
/// them, so the op log is what ran. <c>plan-goldens.json</c> (from <c>tools/fixture-gen/lib/sim.mjs</c>) pins each plan's draws
/// and output bit for bit. Divergences from upstream: <c>SIM-03</c> (code-block key), <c>SIM-05</c> (counters per run).
/// </summary>
internal static class SimPlans
{
    private static readonly string[] Keys = { "one", "two" };
    private const string Image = "https://user-images.githubusercontent.com/5553757/48975307-61efb100-f06d-11e8-9177-ee895e5916e5.png";

    // ------------------------------------------------------------------ stored-form builders

    private static JsonObject Root(string root) => new() { ["root"] = root, ["kind"] = root };

    private static JsonArray Path(string root, JsonNode segment) => new(Root(root), segment);

    private static JsonObject Step(string kind, string doc, params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject { ["step"] = kind, ["doc"] = doc };
        foreach (var (k, v) in fields) o[k] = v;
        return o;
    }

    private static JsonNode N(int n) => JsonValue.Create(n);
    private static JsonNode S(string s) => JsonValue.Create(s)!;

    /// <summary>ADR 0011 tagged object: an ordered pair list.</summary>
    internal static JsonObject Obj(params (string Key, JsonNode? Value)[] pairs)
    {
        var list = new JsonArray();
        foreach (var (k, v) in pairs) list.Add(new JsonArray(JsonValue.Create(k)!, v));
        return new JsonObject { ["$"] = "obj", ["pairs"] = list };
    }

    private static JsonArray Ints(params int[] values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());

    // ------------------------------------------------------------------ marks

    /// <summary>The three marks of the upstream Quill generator, as stored attributes.</summary>
    internal static JsonObject Mark(int index) => index switch
    {
        0 => Obj(("bold", JsonValue.Create(true))),
        1 => Obj(("italic", JsonValue.Create(true))),
        _ => Obj(("italic", JsonValue.Create(true)), ("color", JsonValue.Create("#888"))),
    };

    // JS marksChoices = [undefined, ...marks]; the draw is oneOf over those four, so choices are -1 (undefined) and 0..2
    private static readonly int[] MarkChoices = { -1, 0, 1, 2 };
    private static readonly int[] MarkIndices = { 0, 1, 2 };

    // ------------------------------------------------------------------ map

    public static readonly IReadOnlyList<SimPlan> Map = new SimPlan[]
    {
        // set
        (gen, doc, _, _) =>
        {
            var key = Prng.OneOf(gen, Keys);
            var value = Prng.Utf16String(gen);
            return new[] { Step("map.set", doc, ("target", Root("map")), ("key", S(key)), ("value", S(value))) };
        },
        // setType
        (gen, doc, _, _) =>
        {
            var key = Prng.OneOf(gen, Keys);
            var kind = Prng.OneOf(gen, new[] { "array", "map" });
            var deep = Path("map", S(key));
            return new[]
            {
                Step("map.insertType", doc, ("target", Root("map")), ("key", S(key)), ("kind", S(kind))),
                kind == "array"
                    ? Step("array.insert", doc, ("target", deep), ("at", N(0)), ("values", Ints(1, 2, 3, 4)))
                    : Step("map.set", doc, ("target", deep), ("key", S("deepkey")), ("value", S("deepvalue"))),
            };
        },
        // _delete
        (gen, doc, _, _) =>
        {
            var key = Prng.OneOf(gen, Keys);
            return new[] { Step("map.delete", doc, ("target", Root("map")), ("key", S(key))) };
        },
    };

    // ------------------------------------------------------------------ array

    public static readonly IReadOnlyList<SimPlan> Array = new SimPlan[]
    {
        // insert
        (gen, doc, view, c) =>
        {
            var uniqueNumber = c.NextUniqueNumber();
            var len = (int)Prng.Int32(gen, 1, 4);
            var content = Enumerable.Repeat(uniqueNumber, len).ToArray();
            var pos = (int)Prng.Int32(gen, 0, view.Length);
            return new[] { Step("array.insert", doc, ("target", Root("array")), ("at", N(pos)), ("values", Ints(content))) };
        },
        // insertTypeArray
        (gen, doc, view, _) =>
        {
            var pos = (int)Prng.Int32(gen, 0, view.Length);
            return new[]
            {
                Step("array.insertType", doc, ("target", Root("array")), ("at", N(pos)), ("kind", S("array"))),
                Step("array.insert", doc, ("target", Path("array", N(pos))), ("at", N(0)), ("values", Ints(1, 2, 3, 4))),
            };
        },
        // insertTypeMap
        (gen, doc, view, _) =>
        {
            var pos = (int)Prng.Int32(gen, 0, view.Length);
            var steps = new List<JsonObject> { Step("array.insertType", doc, ("target", Root("array")), ("at", N(pos)), ("kind", S("map"))) };
            foreach (var value in new[] { 42, 43, 44 })
                steps.Add(Step("map.set", doc, ("target", Path("array", N(pos))), ("key", S("someprop")), ("value", N(value))));
            return steps;
        },
        // insertTypeNull
        (gen, doc, view, _) =>
        {
            var pos = (int)Prng.Int32(gen, 0, view.Length);
            return new[] { Step("array.insert", doc, ("target", Root("array")), ("at", N(pos)), ("values", new JsonArray { (JsonNode?)null })) };
        },
        // _delete
        (gen, doc, view, _) =>
        {
            var length = view.Length;
            if (length <= 0) return System.Array.Empty<JsonObject>();
            var somePos = (int)Prng.Int32(gen, 0, length - 1);
            var delLength = (int)Prng.Int32(gen, 1, Math.Min(2, length - somePos));
            if (Prng.Bool(gen))
            {
                var nested = view.NestedArrayLength(somePos);
                if (nested <= 0) return System.Array.Empty<JsonObject>();
                var outer = somePos;
                somePos = (int)Prng.Int32(gen, 0, nested - 1);
                delLength = (int)Prng.Int32(gen, 0, Math.Min(2, nested - somePos));
                return new[] { Step("array.delete", doc, ("target", Path("array", N(outer))), ("at", N(somePos)), ("len", N(delLength))) };
            }
            return new[] { Step("array.delete", doc, ("target", Root("array")), ("at", N(somePos)), ("len", N(delLength))) };
        },
    };

    // ------------------------------------------------------------------ text (textChanges)

    public static readonly IReadOnlyList<SimPlan> Text = new SimPlan[]
    {
        // insert text
        (gen, doc, view, c) =>
        {
            var insertPos = (int)Prng.Int32(gen, 0, view.Length);
            var value = c.NextChar() + Prng.Word(gen);
            return new[] { Step("text.insert", doc, ("target", Root("text")), ("at", N(insertPos)), ("value", S(value))) };
        },
        // delete text
        (gen, doc, view, _) =>
        {
            var contentLen = view.StringLength;
            var insertPos = (int)Prng.Int32(gen, 0, contentLen);
            var overwrite = Math.Min((int)Prng.Int32(gen, 0, contentLen - insertPos), 2);
            return new[] { Step("text.delete", doc, ("target", Root("text")), ("at", N(insertPos)), ("len", N(overwrite))) };
        },
    };

    // ------------------------------------------------------------------ quill (qChanges)

    /// <summary>
    /// SIM-03: the delta of the code-block operation. Upstream passes the attributes under the key <c>format</c>, which
    /// <c>applyDelta</c> ignores, so its code blocks never carry the attribute; the port uses <c>attributes</c>.
    /// </summary>
    internal static JsonArray CodeBlockDelta(int insertPos, string text)
    {
        var ops = new JsonArray();
        if (insertPos > 0) ops.Add(Obj(("retain", N(insertPos))));
        ops.Add(Obj(("insert", S(text))));
        ops.Add(Obj(("insert", S("\n")), ("attributes", Obj(("code-block", JsonValue.Create(true))))));
        return ops;
    }

    public static readonly IReadOnlyList<SimPlan> Quill = new SimPlan[]
    {
        // insert text
        (gen, doc, view, c) =>
        {
            var insertPos = (int)Prng.Int32(gen, 0, view.Length);
            var attrs = Prng.OneOf(gen, MarkChoices);
            var value = c.NextChar() + Prng.Word(gen);
            var fields = new List<(string, JsonNode?)> { ("target", Root("text")), ("at", N(insertPos)), ("value", S(value)) };
            if (attrs >= 0) fields.Add(("attributes", Mark(attrs)));
            return new[] { Step("text.insert", doc, fields.ToArray()) };
        },
        // insert embed
        (gen, doc, view, _) =>
        {
            var insertPos = (int)Prng.Int32(gen, 0, view.Length);
            if (Prng.Bool(gen))
                return new[] { Step("text.insertEmbed", doc, ("target", Root("text")), ("at", N(insertPos)), ("value", Obj(("image", S(Image))))) };
            var key = Prng.Word(gen);
            var value = Prng.Word(gen);
            return new[]
            {
                Step("text.insertEmbedType", doc, ("target", Root("text")), ("at", N(insertPos)), ("kind", S("map")), ("init", Obj((key, S(value))))),
            };
        },
        // delete text
        (gen, doc, view, _) =>
        {
            var contentLen = view.StringLength;
            var insertPos = (int)Prng.Int32(gen, 0, contentLen);
            var overwrite = Math.Min((int)Prng.Int32(gen, 0, contentLen - insertPos), 2);
            return new[] { Step("text.delete", doc, ("target", Root("text")), ("at", N(insertPos)), ("len", N(overwrite))) };
        },
        // format text
        (gen, doc, view, _) =>
        {
            var contentLen = view.StringLength;
            var insertPos = (int)Prng.Int32(gen, 0, contentLen);
            var overwrite = Math.Min((int)Prng.Int32(gen, 0, contentLen - insertPos), 2);
            var format = Prng.OneOf(gen, MarkIndices);
            return new[]
            {
                Step("text.format", doc, ("target", Root("text")), ("at", N(insertPos)), ("len", N(overwrite)), ("attributes", Mark(format))),
            };
        },
        // insert codeblock
        (gen, doc, view, c) =>
        {
            var insertPos = (int)Prng.Int32(gen, 0, view.StringLength);
            var text = c.NextChar() + Prng.Word(gen);
            return new[] { Step("text.applyDelta", doc, ("target", Root("text")), ("delta", CodeBlockDelta(insertPos, text))) };
        },
        // complex delta op
        (gen, doc, view, _) =>
        {
            var contentLen = view.StringLength;
            var currentPos = Math.Max(0, (int)Prng.Int32(gen, 0, contentLen - 1));
            var ops = new JsonArray();
            if (currentPos > 0) ops.Add(Obj(("retain", N(currentPos))));
            var branches = new Action[3];
            branches[0] = () => // format
            {
                var retain = Math.Min((int)Prng.Int32(gen, 0, contentLen - currentPos), 5);
                var format = Prng.OneOf(gen, MarkIndices);
                ops.Add(Obj(("retain", N(retain)), ("attributes", Mark(format))));
                currentPos += retain;
            };
            branches[1] = () => // insert
            {
                var attrs = Prng.OneOf(gen, MarkChoices);
                var text = Prng.Word(gen, 1, 3);
                ops.Add(attrs >= 0 ? Obj(("insert", S(text)), ("attributes", Mark(attrs))) : Obj(("insert", S(text))));
            };
            branches[2] = () => // delete
            {
                var delLen = Math.Min((int)Prng.Int32(gen, 0, contentLen - currentPos), 10);
                ops.Add(Obj(("delete", N(delLen))));
                currentPos += delLen;
            };
            for (var i = 0; i < 7 && currentPos < contentLen; i++) Prng.OneOf(gen, branches)();
            return new[] { Step("text.applyDelta", doc, ("target", Root("text")), ("delta", ops)) };
        },
    };

    /// <summary>The generator of a case kind (<c>map</c>, <c>array</c>, <c>text</c>, <c>quill</c>).</summary>
    public static IReadOnlyList<SimPlan> For(string kind) => kind switch
    {
        "map" => Map,
        "array" => Array,
        "text" => Text,
        "quill" => Quill,
        _ => throw new ArgumentException($"Unknown generator '{kind}'."),
    };
}
