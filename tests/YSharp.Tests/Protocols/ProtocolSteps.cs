using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using YSharp.Tests.Types;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Time;
using YSharp.TestKit.Values;

namespace YSharp.Tests.Protocols;

/// <summary>What a protocol step recorded under a result name (the <c>results</c> observation of the Node generator).</summary>
internal sealed class ProtocolResult
{
    public string Name { get; init; } = "";

    /// <summary>The step kind that recorded it, which decides the fields the Node side writes (protocol-steps.mjs <c>record</c>).</summary>
    public string Kind { get; init; } = "";
    public byte[]? Bytes { get; init; }
    public bool Threw { get; init; }
    public int? Type { get; init; }
    public bool? HandlerCalled { get; init; }
    public int? Consumed { get; init; }
}

internal sealed class ProtocolState
{
    public Dictionary<string, Awareness> Awarenesses { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ProtocolResult> Results { get; } = new(StringComparer.Ordinal);
    public List<JsonNode> Events { get; } = new();
}

/// <summary>
/// The C# half of the protocol step kinds of ADR 0016 (Spec 9): <c>awareness.new/setLocal/setLocalField/applyUpdate/removeStates/
/// encode/modify/destroy</c>, <c>sync.step1/step2/update/read</c> and <c>auth.permissionDenied</c>. Their Node halves are in
/// tools/fixture-gen/lib/protocol-steps.mjs and their schema entries in ops.schema.json.
/// </summary>
internal static class ProtocolSteps
{
    private static readonly ConditionalWeakTable<OpsContext, ProtocolState> States = new();

    public static ProtocolState Of(OpsContext ctx) => States.GetValue(ctx, _ => new ProtocolState());

    /// <summary>The instant virtual time zero maps to (the JS interpreter's clock starts at 0).</summary>
    public static DateTimeOffset Epoch { get; } = new VirtualTimeProvider().GetUtcNow();

    public static readonly string[] Kinds =
    {
        "awareness.new", "awareness.setLocal", "awareness.setLocalField", "awareness.applyUpdate", "awareness.removeStates",
        "awareness.encode", "awareness.modify", "awareness.destroy", "sync.step1", "sync.step2", "sync.update", "sync.read",
        "auth.permissionDenied",
    };

    public static OpsRegistry CreateRegistry()
    {
        var r = StandardSteps.CreateRegistry();
        Register(r);
        return r;
    }

    /// <summary>Adds the protocol step kinds to <paramref name="r"/> (the differential adds them to its simulation registry).</summary>
    public static void Register(OpsRegistry r)
    {
        void Add(string kind, string[] required, string[] optional, Action<OpsContext, OpsStep> run) =>
            r.Register(new StepDefinition(kind, required, optional, run));
        var none = Array.Empty<string>();

        Add("awareness.new", new[] { "awareness", "doc" }, none, (ctx, s) =>
        {
            var st = Of(ctx);
            var name = s.String("awareness");
            var aw = new Awareness(ctx.Doc(s.String("doc")), ctx.Clock);
            if (!st.Awarenesses.TryAdd(name, aw)) throw new OpsScriptException($"awareness '{name}' declared twice.");
            JsonNode Ids(IReadOnlyList<ulong> ids) => new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create((long)i)!).ToArray());
            AwarenessHandler Log(string ev) => (c, origin) => Event(ctx, new JsonObject
            {
                ["observer"] = name, ["event"] = ev, ["at"] = ElapsedMs(ctx), ["added"] = Ids(c.Added), ["updated"] = Ids(c.Updated),
                ["removed"] = Ids(c.Removed), ["origin"] = origin is string o ? JsonValue.Create(o) : null,
            });
            aw.Change += Log("change");
            aw.Update += Log("update");
            aw.Destroyed += () => Event(ctx, new JsonObject { ["observer"] = name, ["event"] = "destroy", ["at"] = ElapsedMs(ctx) });
        });
        Add("awareness.setLocal", new[] { "awareness", "state" }, none, (ctx, s) =>
            Aw(ctx, s).SetLocalState(s.Tagged("state").ToYValue()));
        Add("awareness.setLocalField", new[] { "awareness", "field", "value" }, none, (ctx, s) =>
            Aw(ctx, s).SetLocalStateField(s.String("field"), s.Tagged("value").ToYValue()));
        Add("awareness.applyUpdate", new[] { "awareness" }, new[] { "bytes", "from", "origin", "name" }, (ctx, s) =>
        {
            var aw = Aw(ctx, s);
            if (s.Has("name"))
            {
                // the Node step resolves its input inside the attempt, so a named apply of bytes an earlier step failed to produce records a throw
                var input = TryInput(ctx, s);
                var ok = input != null && aw.TryApplyUpdate(input, s.OptString("origin")).Ok;
                Of(ctx).Results[s.String("name")] = new ProtocolResult { Name = s.String("name"), Kind = s.Kind, Threw = !ok };
                return;
            }
            var result = aw.TryApplyUpdate(Input(ctx, s), s.OptString("origin"));
            if (!result.Ok) throw new InvalidOperationException($"awareness.applyUpdate failed with {result.Error}.");
        });
        Add("awareness.removeStates", new[] { "awareness", "clients" }, new[] { "origin" }, (ctx, s) =>
            Aw(ctx, s).RemoveStates(Ids(s), s.OptString("origin")));
        Add("awareness.encode", new[] { "name", "awareness", "clients" }, new[] { "states" }, (ctx, s) =>
        {
            Dictionary<ulong, YValue>? states = null;
            if (s.Has("states"))
                states = s.Tagged("states").Items.ToDictionary(p => (ulong)p.Items[0].Number, p => p.Items[1].ToYValue());
            var w = new ArrayBufferWriter<byte>();
            var status = Aw(ctx, s).TryEncodeUpdate(w, Ids(s), states);
            Record(ctx, s, status.Ok ? w.WrittenSpan.ToArray() : null, !status.Ok);
        });
        Add("awareness.modify", new[] { "name", "mode" }, new[] { "bytes", "from", "field", "value" }, (ctx, s) =>
        {
            var w = new ArrayBufferWriter<byte>();
            var field = s.OptString("field");
            var value = s.Has("value") ? s.Tagged("value").ToYValue() : default;
            Func<YValue, YValue> modify = s.String("mode") switch
            {
                "identity" => v => v,
                "blank" => _ => YValue.Null,
                "setField" => v => WithField(v, field!, value),
                "setFieldKeepNull" => v => v.Kind == YValueKind.Null ? v : WithField(v, field!, value),
                "undefined" => _ => default,
                var other => throw new OpsScriptException($"awareness.modify: unknown mode '{other}'."),
            };
            var input = TryInput(ctx, s);   // resolved inside the attempt on the Node side too
            var ok = input != null && Awareness.TryModifyUpdate(input, modify, w).Ok;
            Record(ctx, s, ok ? w.WrittenSpan.ToArray() : null, !ok);
        });
        Add("awareness.destroy", new[] { "awareness" }, none, (ctx, s) => Aw(ctx, s).Dispose());

        Add("sync.step1", new[] { "name", "doc" }, new[] { "append" }, (ctx, s) =>
        {
            var w = Prefixed(ctx, s);
            YSync.WriteStep1(w, ctx.Doc(s.String("doc")));
            Record(ctx, s, w.WrittenSpan.ToArray(), false);
        });
        Add("sync.step2", new[] { "name", "doc" }, new[] { "sv", "append" }, (ctx, s) =>
        {
            var w = Prefixed(ctx, s);
            YSync.WriteStep2(w, ctx.Doc(s.String("doc")), s.Has("sv") ? s.Tagged("sv").Bytes : default);
            Record(ctx, s, w.WrittenSpan.ToArray(), false);
        });
        Add("sync.update", new[] { "name", "update" }, new[] { "append" }, (ctx, s) =>
        {
            var w = Prefixed(ctx, s);
            YSync.WriteUpdate(w, s.Tagged("update").Bytes);
            Record(ctx, s, w.WrittenSpan.ToArray(), false);
        });
        Add("sync.read", new[] { "name", "doc" }, new[] { "bytes", "from", "origin" }, (ctx, s) =>
        {
            var input = Input(ctx, s);
            ReadOnlySpan<byte> span = input;
            var reply = new ArrayBufferWriter<byte>();
            var handlerCalled = false;
            var status = YSync.TryReadMessage(ref span, reply, ctx.Doc(s.String("doc")), s.OptString("origin"), out var result, _ => handlerCalled = true);
            Of(ctx).Results[s.String("name")] = new ProtocolResult
            {
                Name = s.String("name"), Kind = s.Kind, Bytes = reply.WrittenSpan.ToArray(), Threw = !status.Ok && !handlerCalled,
                Type = status.Ok || handlerCalled ? (int)result.Type : null, HandlerCalled = handlerCalled, Consumed = input.Length - span.Length,
            };
        });
        Add("auth.permissionDenied", new[] { "name", "reason" }, none, (ctx, s) =>
        {
            var w = new ArrayBufferWriter<byte>();
            YAuth.WritePermissionDenied(w, s.String("reason"));
            Record(ctx, s, w.WrittenSpan.ToArray(), false);
        });
    }

    /// <summary>
    /// Records an awareness event here and in the doc-feature event log, so a script mixing awareness with type observers keeps the one
    /// firing order of the Node side's <c>ctx.events</c>.
    /// </summary>
    private static void Event(OpsContext ctx, JsonObject e)
    {
        Of(ctx).Events.Add(e);
        DocFeatureSteps.Of(ctx).Events.Add(e.ToJsonString());
    }

    public static long ElapsedMs(OpsContext ctx) => (long)(ctx.Clock.GetUtcNow() - Epoch).TotalMilliseconds;

    private static Awareness Aw(OpsContext ctx, OpsStep s) =>
        Of(ctx).Awarenesses.TryGetValue(s.String("awareness"), out var a) ? a : throw new OpsScriptException($"unknown awareness '{s.String("awareness")}'.");

    private static ulong[] Ids(OpsStep s) => s.Element("clients").EnumerateArray().Select(e => e.GetUInt64()).ToArray();

    private static void Record(OpsContext ctx, OpsStep s, byte[]? bytes, bool threw) =>
        Of(ctx).Results[s.String("name")] = new ProtocolResult { Name = s.String("name"), Kind = s.Kind, Bytes = bytes, Threw = threw };

    private static byte[] Input(OpsContext ctx, OpsStep s)
    {
        if (s.Has("bytes") == s.Has("from")) throw new OpsScriptException($"{s.Kind}: exactly one of bytes and from is required.");
        if (s.Has("bytes")) return s.Tagged("bytes").Bytes;
        return Of(ctx).Results.TryGetValue(s.String("from"), out var r) && r.Bytes != null
            ? r.Bytes
            : throw new OpsScriptException($"no recorded bytes named '{s.String("from")}'.");
    }

    /// <summary><see cref="Input"/>, or null when <c>from</c> names a result with no bytes (a step that failed); a malformed step still throws.</summary>
    private static byte[]? TryInput(OpsContext ctx, OpsStep s)
    {
        if (s.Has("bytes") == s.Has("from")) throw new OpsScriptException($"{s.Kind}: exactly one of bytes and from is required.");
        if (s.Has("bytes")) return s.Tagged("bytes").Bytes;
        return Of(ctx).Results.TryGetValue(s.String("from"), out var r) ? r.Bytes : null;
    }

    private static ArrayBufferWriter<byte> Prefixed(OpsContext ctx, OpsStep s)
    {
        var w = new ArrayBufferWriter<byte>();
        if (s.Has("append"))
            w.Write(Of(ctx).Results.TryGetValue(s.String("append"), out var r) && r.Bytes != null ? r.Bytes : throw new OpsScriptException("append names no recorded bytes."));
        return w;
    }

    /// <summary>
    /// JS <c>{...state, [field]: value}</c>: an existing key keeps its position, a new one is appended. Spread copies own enumerable keys, so
    /// a string gives one key per UTF-16 unit and an array one per element (<c>"0"</c>, <c>"1"</c>, ...); null, booleans and numbers give none.
    /// </summary>
    internal static YValue WithField(YValue state, string field, YValue value)
    {
        var members = new List<KeyValuePair<string, YValue>>();
        if (state.TryGetObject(out var view))
            for (var i = 0; i < view.Count; i++) members.Add(view[i]);
        else if (state.TryGetString(out var text))
            for (var i = 0; i < text.Length; i++) members.Add(new(i.ToString(CultureInfo.InvariantCulture), (YValue)text[i].ToString()));
        else if (state.TryGetArray(out var items))
            for (var i = 0; i < items.Length; i++) members.Add(new(i.ToString(CultureInfo.InvariantCulture), items[i]));
        var at = members.FindIndex(m => m.Key == field);
        if (at >= 0) members[at] = new(field, value);
        else members.Add(new(field, value));
        return YValue.Object(members.ToArray());
    }
}
