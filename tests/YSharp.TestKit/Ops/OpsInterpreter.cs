using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using YSharp.TestKit.Time;
using YSharp.TestKit.Values;

namespace YSharp.TestKit.Ops;

public sealed record ReplicaSpec(string Name, uint ClientId, bool Gc);

/// <summary>An observer firing recorded under the <c>events</c> observation, in firing order.</summary>
public sealed record ObservedEvent(string Observer, string EventType);

/// <summary>The recorded outcome of a named <c>applyForeign</c> step (ADR 0012): whether the update was accepted, and in which format.</summary>
public sealed record ForeignOutcome(UpdateFormat Format, bool Accepted);

/// <summary>Mutable state of one script run: replicas, captured update bytes, recorded events and the virtual clock.</summary>
public sealed class OpsContext
{
    private readonly Dictionary<string, int> _transactDepth = new(StringComparer.Ordinal);

    public OpsContext(OpsRegistry registry)
    {
        Registry = registry;
    }

    public OpsRegistry Registry { get; }
    public VirtualTimeProvider Clock { get; } = new();
    public Dictionary<string, Doc> Docs { get; } = new(StringComparer.Ordinal);

    /// <summary>Update (V1) bytes in event order, per replica.</summary>
    public Dictionary<string, List<byte[]>> Updates { get; } = new(StringComparer.Ordinal);

    public List<ObservedEvent> Events { get; } = new();

    /// <summary>Outcomes of named <c>applyForeign</c> steps, in script order (the <c>results</c> observation, ADR 0012).</summary>
    public Dictionary<string, ForeignOutcome> ForeignOutcomes { get; } = new(StringComparer.Ordinal);

    public Func<OpsContext, ReplicaSpec, Doc> DocFactory { get; set; } = DefaultDocFactory;
    public Action<Doc, object?, Action> TransactRunner { get; set; } = (doc, origin, body) => doc.Transact(_ => body(), origin);

    public void DeclareReplica(string name, Doc doc)
    {
        if (!Docs.TryAdd(name, doc)) throw new OpsScriptException($"Replica '{name}' is declared twice.");
        Updates.TryAdd(name, new List<byte[]>());
    }

    public Doc Doc(string name) =>
        Docs.TryGetValue(name, out var d) ? d : throw new OpsScriptException($"Unknown replica '{name}'.");

    private static Doc DefaultDocFactory(OpsContext ctx, ReplicaSpec spec)
    {
        var doc = new Doc(new DocOptions { ClientId = spec.ClientId, Gc = spec.Gc, TimeProvider = ctx.Clock });
        var list = new List<byte[]>();
        ctx.Updates[spec.Name] = list;
        doc.Update += (update, _, _, _) => list.Add(update.ToArray());
        return doc;
    }

    /// <summary>Runs <paramref name="body"/> in a transaction on <paramref name="doc"/>; a nested call joins the outer one.</summary>
    public void InTransaction(string docName, object? origin, Action body)
    {
        var doc = Doc(docName);
        _transactDepth.TryGetValue(docName, out var depth);
        if (depth > 0)
        {
            body();
            return;
        }
        _transactDepth[docName] = 1;
        try { TransactRunner(doc, origin, body); }
        finally { _transactDepth[docName] = 0; }
    }

    /// <summary>
    /// Resolves a shared-type address (ADR 0016): <c>{root, kind}</c> or a path
    /// <c>[{root, kind}, "key", 2, ...]</c> of map keys and list indices. Failure is a script error.
    /// </summary>
    public YType ResolveTarget(Doc doc, JsonElement target)
    {
        if (target.ValueKind == JsonValueKind.Object) return ResolveRoot(doc, target);
        if (target.ValueKind != JsonValueKind.Array || target.GetArrayLength() == 0)
            throw new OpsScriptException("A target must be {root,kind} or a non-empty path.");
        var current = ResolveRoot(doc, target[0]);
        for (var i = 1; i < target.GetArrayLength(); i++)
        {
            var seg = target[i];
            current = seg.ValueKind switch
            {
                JsonValueKind.String => Child(current, seg.GetString()!),
                JsonValueKind.Number when seg.TryGetInt32(out var idx) => Child(current, idx),
                _ => throw new OpsScriptException($"Path segment {i} must be a key or an index."),
            };
        }
        return current;
    }

    private static YType ResolveRoot(Doc doc, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("root", out var name) || name.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
            throw new OpsScriptException("A root address needs string 'root' and 'kind'.");
        var n = name.GetString()!;
        return kind.GetString() switch
        {
            "text" => doc.GetText(n),
            "array" => doc.GetArray(n),
            "map" => doc.GetMap(n),
            "xmlFragment" => doc.GetXmlFragment(n),
            var other => throw new OpsScriptException($"Unknown root kind '{other}'."),
        };
    }

    private static YType Child(YType parent, string key)
    {
        if (parent is YMap map && map.TryGet(key, out var entry) && entry.Type is { } t) return t;
        throw new OpsScriptException($"Path key '{key}' does not resolve to a shared type.");
    }

    private static YType Child(YType parent, int index)
    {
        try
        {
            if (parent is YArray arr && arr[index].Type is { } t) return t;
            if (parent is YXmlFragment xml) return xml[index];
        }
        catch (ArgumentOutOfRangeException) { }
        throw new OpsScriptException($"Path index {index} does not resolve to a shared type.");
    }
}

/// <summary>Replays an <see cref="OpsScript"/> against y-sharp (the C# half of ADR 0011's two interpreters).</summary>
public sealed class OpsInterpreter
{
    private readonly OpsRegistry _registry;

    public OpsInterpreter(OpsRegistry registry) => _registry = registry;

    /// <summary>Replaces doc creation (tests of the interpreter itself).</summary>
    public Func<OpsContext, ReplicaSpec, Doc>? DocFactory { get; set; }

    /// <summary>Replaces <c>Doc.Transact</c> (tests of the interpreter itself).</summary>
    public Action<Doc, object?, Action>? TransactRunner { get; set; }

    public OpsContext Run(OpsScript script)
    {
        var ctx = new OpsContext(_registry);
        if (DocFactory != null) ctx.DocFactory = DocFactory;
        if (TransactRunner != null) ctx.TransactRunner = TransactRunner;
        RunSteps(ctx, script.Steps);
        return ctx;
    }

    public static void RunSteps(OpsContext ctx, IEnumerable<OpsStep> steps)
    {
        foreach (var step in steps) step.Definition.Run(ctx, step);
    }
}

/// <summary>The core step kinds of ADR 0016 that every area shares. Areas add theirs through <see cref="OpsRegistry.Register"/>.</summary>
public static class StandardSteps
{
    public static readonly StepDefinition Transact = new(
        "transact", new[] { "doc" }, new[] { "origin" },
        (ctx, s) => ctx.InTransaction(s.String("doc"), s.OptString("origin"), () => OpsInterpreter.RunSteps(ctx, s.Children!)),
        hasChildren: true);

    public static OpsRegistry CreateRegistry()
    {
        var r = new OpsRegistry();
        r.Register(new StepDefinition("replica", new[] { "name", "clientId" }, new[] { "gc" }, (ctx, s) =>
        {
            var spec = new ReplicaSpec(s.String("name"), checked((uint)s.Long("clientId")), s.Bool("gc", true));
            ctx.DeclareReplica(spec.Name, ctx.DocFactory(ctx, spec));
        }));
        r.Register(Transact);
        r.Register(new StepDefinition("tick", new[] { "ms" }, System.Array.Empty<string>(),
            (ctx, s) => ctx.Clock.Advance(TimeSpan.FromMilliseconds(s.Long("ms")))));
        r.Register(new StepDefinition("sync", new[] { "from", "to" }, System.Array.Empty<string>(), (ctx, s) =>
        {
            var from = ctx.Doc(s.String("from"));
            var to = ctx.Doc(s.String("to"));
            var sv = new ArrayBufferWriter<byte>();
            to.EncodeStateVector(sv);
            to.ApplyUpdate(from.EncodeStateAsUpdate(sv.WrittenSpan), "sync");
        }));
        r.Register(new StepDefinition("applyUpdate", new[] { "doc", "bytes" }, new[] { "encoding" }, (ctx, s) =>
        {
            var format = (s.OptString("encoding") ?? "v1") switch
            {
                "v1" => UpdateFormat.V1,
                "v2" => UpdateFormat.V2,
                var e => throw new OpsScriptException($"applyUpdate: unknown encoding '{e}'."),
            };
            var bytes = s.Tagged("bytes");
            if (bytes.Kind != TaggedKind.Bytes) throw new OpsScriptException("applyUpdate: 'bytes' must be tagged bytes.");
            ctx.Doc(s.String("doc")).ApplyUpdate(bytes.Bytes, null, format);
        }));
        // ADR 0012: bytes another implementation produced. Origin "foreign". Unnamed: a rejected update throws (a script error on the
        // oracle side). Named: the outcome is recorded (ctx.ForeignOutcomes) so the harness can classify mutated updates.
        r.Register(new StepDefinition("applyForeign", new[] { "doc", "bytes" }, new[] { "encoding", "name" }, (ctx, s) =>
        {
            var format = (s.OptString("encoding") ?? "v1") switch
            {
                "v1" => UpdateFormat.V1,
                "v2" => UpdateFormat.V2,
                var e => throw new OpsScriptException($"applyForeign: unknown encoding '{e}'."),
            };
            var bytes = s.Tagged("bytes");
            if (bytes.Kind != TaggedKind.Bytes) throw new OpsScriptException("applyForeign: 'bytes' must be tagged bytes.");
            var doc = ctx.Doc(s.String("doc"));
            var name = s.OptString("name");
            if (name == null)
            {
                doc.ApplyUpdate(bytes.Bytes, "foreign", format);
                return;
            }
            if (ctx.ForeignOutcomes.ContainsKey(name)) throw new OpsScriptException($"applyForeign: result '{name}' is recorded twice.");
            ctx.ForeignOutcomes[name] = new ForeignOutcome(format, doc.TryApplyUpdate(bytes.Bytes, "foreign", format).Ok);
        }));
        r.Register(new StepDefinition("observe", new[] { "doc", "target", "name" }, System.Array.Empty<string>(), (ctx, s) =>
        {
            var name = s.String("name");
            ctx.ResolveTarget(ctx.Doc(s.String("doc")), s.Element("target"))
                .Observe((e, _) => ctx.Events.Add(new ObservedEvent(name, e.GetType().Name)));
        }));

        r.Register(new StepDefinition("text.insert", new[] { "doc", "target", "at", "value" }, new[] { "attributes" }, (ctx, s) =>
            YTxt(ctx, s).Insert(s.Int("at"), TextValue(s), s.Has("attributes") ? s.Tagged("attributes").ToYValue() : default)));
        r.Register(new StepDefinition("text.delete", new[] { "doc", "target", "at", "len" }, System.Array.Empty<string>(), (ctx, s) =>
            YTxt(ctx, s).Delete(s.Int("at"), s.Int("len"))));
        r.Register(new StepDefinition("text.format", new[] { "doc", "target", "at", "len", "attributes" }, System.Array.Empty<string>(), (ctx, s) =>
            YTxt(ctx, s).Format(s.Int("at"), s.Int("len"), s.Tagged("attributes").ToYValue())));

        r.Register(new StepDefinition("array.insert", new[] { "doc", "target", "at", "values" }, System.Array.Empty<string>(), (ctx, s) =>
            YArr(ctx, s).Insert(s.Int("at"), Entries(s))));
        r.Register(new StepDefinition("array.push", new[] { "doc", "target", "values" }, System.Array.Empty<string>(), (ctx, s) =>
            YArr(ctx, s).Push(Entries(s))));
        r.Register(new StepDefinition("array.unshift", new[] { "doc", "target", "values" }, System.Array.Empty<string>(), (ctx, s) =>
            YArr(ctx, s).Unshift(Entries(s))));
        r.Register(new StepDefinition("array.delete", new[] { "doc", "target", "at" }, new[] { "len" }, (ctx, s) =>
            YArr(ctx, s).Delete(s.Int("at"), s.Has("len") ? s.Int("len") : 1)));

        r.Register(new StepDefinition("map.set", new[] { "doc", "target", "key", "value" }, System.Array.Empty<string>(), (ctx, s) =>
            YMp(ctx, s).Set(s.String("key"), (YEntry)s.Tagged("value").ToYValue())));
        r.Register(new StepDefinition("map.delete", new[] { "doc", "target", "key" }, System.Array.Empty<string>(), (ctx, s) =>
            YMp(ctx, s).Delete(s.String("key"))));
        r.Register(new StepDefinition("map.clear", new[] { "doc", "target" }, System.Array.Empty<string>(), (ctx, s) =>
            YMp(ctx, s).Clear()));
        DocFeatureSteps.Register(r);
        return r;
    }

    private static YType Target(OpsContext ctx, OpsStep s) => ctx.ResolveTarget(ctx.Doc(s.String("doc")), s.Element("target"));

    private static YText YTxt(OpsContext ctx, OpsStep s) =>
        Target(ctx, s) as YText ?? throw new OpsScriptException($"{s.Kind}: target is not a text.");

    private static YArray YArr(OpsContext ctx, OpsStep s) =>
        Target(ctx, s) as YArray ?? throw new OpsScriptException($"{s.Kind}: target is not an array.");

    private static YMap YMp(OpsContext ctx, OpsStep s) =>
        Target(ctx, s) as YMap ?? throw new OpsScriptException($"{s.Kind}: target is not a map.");

    /// <summary>Insert text is a plain string, or a tagged <c>str</c> when it holds a lone surrogate (ADR 0011).</summary>
    private static string TextValue(OpsStep s) =>
        s.Tagged("value") is { Kind: TaggedKind.String } v ? v.String : throw new OpsScriptException($"{s.Kind}: 'value' must be a string.");

    private static YEntry[] Entries(OpsStep s)
    {
        var values = s.Tagged("values");
        if (values.Kind != TaggedKind.Array) throw new OpsScriptException($"{s.Kind}: 'values' must be an array.");
        return values.Items.Select(v => (YEntry)v.ToYValue()).ToArray();
    }
}
