using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using YSharp.TestKit.Values;

namespace YSharp.TestKit.Ops;

/// <summary>
/// The C# half of the doc-feature step kinds of ADR 0016 (Spec 8): <c>gc.run</c>, <c>subdoc.add/load/destroy</c> (plus
/// <c>subdoc.observe/inspect</c>), <c>snapshot.take/restore</c> (plus <c>snapshot.read</c>), <c>relpos.create/resolve</c>,
/// <c>undo.create/stopCapturing/undo/redo</c> and <c>pud.setUserDescription/observe</c>. The Node half is
/// <c>tools/fixture-gen/lib/steps-doc.mjs</c>; <c>ops.schema.json</c> is the single field list both follow.
/// <para>
/// Recorded output mirrors the Node side so a replay can compare it field for field: <see cref="DocFeatureState.Results"/> holds the
/// JSON <c>value</c> of each named result and <see cref="DocFeatureState.Events"/> the JSON of each event record, in firing order.
/// </para>
/// </summary>
public static class DocFeatureSteps
{
    private static readonly ConditionalWeakTable<OpsContext, DocFeatureState> States = new();

    public static DocFeatureState Of(OpsContext ctx) => States.GetValue(ctx, c => new DocFeatureState(c));

    public static readonly string[] Kinds =
    {
        "gc.run", "subdoc.add", "subdoc.load", "subdoc.destroy", "subdoc.observe", "subdoc.inspect",
        "snapshot.take", "snapshot.restore", "snapshot.read", "relpos.create", "relpos.resolve",
        "undo.create", "undo.stopCapturing", "undo.undo", "undo.redo", "pud.setUserDescription", "pud.observe",
    };

    public static void Register(OpsRegistry r)
    {
        static string[] None() => Array.Empty<string>();
        void Add(string kind, string[] required, string[] optional, Action<OpsContext, OpsStep> run) =>
            r.Register(new StepDefinition(kind, required, optional, run));

        Add("gc.run", new[] { "doc" }, new[] { "gc" }, (ctx, s) =>
        {
            var doc = ctx.Doc(s.String("doc"));
            if (s.Has("gc")) doc.Gc = s.Bool("gc", true);
            DocSeams.CollectGarbage(doc);
        });

        // ---------------------------------------------------------------- subdocs
        Add("subdoc.add", new[] { "doc", "target", "guid" }, new[] { "at", "key", "opts" }, (ctx, s) =>
        {
            var options = SubdocOptions(s);
            var sub = new Doc(options);
            var target = ctx.ResolveTarget(ctx.Doc(s.String("doc")), s.Element("target"));
            if (s.Has("key"))
            {
                if (target is not YMap map) throw new OpsScriptException("subdoc.add: key needs a map target");
                map.Set(s.String("key"), (YEntry)sub);
            }
            else
            {
                if (target is not YArray arr) throw new OpsScriptException("subdoc.add: at needs an array target");
                arr.Insert(s.Has("at") ? s.Int("at") : arr.Length, new[] { (YEntry)sub });
            }
        });
        Add("subdoc.load", new[] { "doc", "guid" }, new[] { "index" }, (ctx, s) => FindSubdoc(ctx, s).Load());
        Add("subdoc.destroy", new[] { "doc", "guid" }, new[] { "index" }, (ctx, s) => FindSubdoc(ctx, s).Destroy());
        Add("subdoc.observe", new[] { "doc", "name" }, None(), (ctx, s) =>
        {
            var name = s.String("name");
            var state = Of(ctx);
            ctx.Doc(s.String("doc")).SubdocsChanged += (change, _, _) => state.Events.Add(Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("observer", name);
                w.WriteString("type", "subdocs");
                Guids(w, "added", change.Added);
                Guids(w, "removed", change.Removed);
                Guids(w, "loaded", change.Loaded);
                w.WriteEndObject();
            }));
        });
        Add("subdoc.inspect", new[] { "doc", "name" }, None(), (ctx, s) =>
        {
            var doc = ctx.Doc(s.String("doc"));
            Of(ctx).Record(s.String("name"), Json(w =>
            {
                w.WriteStartObject();
                w.WritePropertyName("guids");
                w.WriteStartArray();
                foreach (var g in doc.Subdocs.Select(d => d.Guid).Distinct(StringComparer.Ordinal)) w.WriteStringValue(g);
                w.WriteEndArray();
                w.WritePropertyName("subdocs");
                w.WriteStartArray();
                foreach (var d in doc.Subdocs)
                {
                    w.WriteStartObject();
                    w.WriteString("guid", d.Guid);
                    w.WriteBoolean("shouldLoad", DocSeams.ShouldLoad(d));
                    w.WriteBoolean("autoLoad", DocSeams.AutoLoad(d));
                    w.WriteBoolean("isLoaded", d.IsLoaded);
                    w.WriteBoolean("gc", d.Gc);
                    w.WritePropertyName("collectionid");
                    if (DocSeams.CollectionId(d) is { } cid) w.WriteStringValue(cid); else w.WriteNullValue();
                    w.WritePropertyName("meta");
                    var meta = DocSeams.Meta(d);
                    (meta.Kind is YValueKind.Undefined or YValueKind.Null ? TaggedValue.Null : Tag(meta)).Write(w);
                    w.WriteBoolean("clientIdIsParents", d.ClientId == doc.ClientId);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }));
        });

        // ---------------------------------------------------------------- snapshots
        Add("snapshot.take", new[] { "doc", "name" }, None(), (ctx, s) =>
        {
            var snap = YSnapshot.Create(ctx.Doc(s.String("doc")));
            Of(ctx).Snapshots[s.String("name")] = snap;
            Of(ctx).Record(s.String("name"), Json(w =>
            {
                w.WriteStartObject();
                w.WritePropertyName("v1");
                TaggedValue.FromBytes(Encode(snap, UpdateFormat.V1)).Write(w);
                w.WritePropertyName("v2");
                TaggedValue.FromBytes(Encode(snap, UpdateFormat.V2)).Write(w);
                w.WriteEndObject();
            }));
        });
        Add("snapshot.restore", new[] { "doc", "snapshot", "as", "clientId" }, None(), (ctx, s) =>
        {
            var snap = Of(ctx).Snapshot(s.String("snapshot"));
            var spec = new ReplicaSpec(s.String("as"), checked((uint)s.Long("clientId")), true);
            var target = ctx.DocFactory(ctx, spec);
            var restored = YSnapshot.CreateDoc(ctx.Doc(s.String("doc")), snap, target);
            ctx.DeclareReplica(spec.Name, restored);
        });
        Add("snapshot.read", new[] { "doc", "target", "snapshot", "name" }, new[] { "prev" }, (ctx, s) =>
        {
            var state = Of(ctx);
            var snap = state.Snapshot(s.String("snapshot"));
            var prev = s.Has("prev") ? state.Snapshot(s.String("prev")) : null;
            var text = ctx.ResolveTarget(ctx.Doc(s.String("doc")), s.Element("target")) as YText
                       ?? throw new OpsScriptException("snapshot.read: target is not a text");
            var delta = text.ToDelta(snap, prev);
            state.Record(s.String("name"), Json(w => WriteDelta(w, delta)));
        });

        // ---------------------------------------------------------------- relative positions
        Add("relpos.create", new[] { "doc", "target", "name", "at" }, new[] { "assoc" }, (ctx, s) =>
        {
            var type = ctx.ResolveTarget(ctx.Doc(s.String("doc")), s.Element("target"));
            var rpos = type.CreateRelativePosition(s.Int("at"), s.Has("assoc") ? s.Int("assoc") : 0);
            var buffer = new ArrayBufferWriter<byte>();
            rpos.Encode(buffer);
            var bytes = buffer.WrittenSpan.ToArray();
            Of(ctx).RelativePositions[s.String("name")] = bytes;
            var json = new ArrayBufferWriter<byte>();
            Check(s, rpos.ToJson().TryWriteJson(json));
            Of(ctx).Record(s.String("name"), Json(w =>
            {
                w.WriteStartObject();
                w.WritePropertyName("bytes");
                TaggedValue.FromBytes(bytes).Write(w);
                w.WriteString("json", Encoding.UTF8.GetString(json.WrittenSpan));
                w.WriteEndObject();
            }));
        });
        Add("relpos.resolve", new[] { "doc", "relpos", "name" }, new[] { "followUndone" }, (ctx, s) =>
        {
            var doc = ctx.Doc(s.String("doc"));
            var bytes = Of(ctx).RelativePositions.TryGetValue(s.String("relpos"), out var b)
                ? b : throw new OpsScriptException($"relpos.resolve: unknown relative position '{s.String("relpos")}'");
            Check(s, RelativePosition.TryDecode(bytes, out var rpos));
            var abs = rpos.Resolve(doc, s.Bool("followUndone", true));
            Of(ctx).Record(s.String("name"), Json(w =>
            {
                if (abs is not { } a) { w.WriteNullValue(); return; }
                w.WriteStartObject();
                w.WritePropertyName("type");
                var root = doc.Share.FirstOrDefault(kv => ReferenceEquals(kv.Value, a.Type));
                if (root.Key != null) w.WriteStringValue(root.Key);
                else
                {
                    // a nested type is identified by its own item id: the end-of-type position anchors on exactly that id
                    var id = a.Type.CreateRelativePosition(int.MaxValue).Type ?? throw new OpsScriptException("relpos.resolve: nested type without an id");
                    w.WriteStartArray();
                    w.WriteNumberValue(id.Client);
                    w.WriteNumberValue(id.Clock);
                    w.WriteEndArray();
                }
                w.WriteNumber("index", a.Index);
                w.WriteNumber("assoc", a.Assoc);
                w.WriteEndObject();
            }));
        });

        // ---------------------------------------------------------------- undo
        Add("undo.create", new[] { "doc", "name", "scope" }, new[] { "captureTimeout", "trackedOrigins", "ignoreRemoteMapChanges" }, (ctx, s) =>
        {
            var doc = ctx.Doc(s.String("doc"));
            var name = s.String("name");
            var state = Of(ctx);
            if (state.UndoManagers.ContainsKey(name)) throw new OpsScriptException($"undo manager '{name}' created twice");
            var options = new UndoManagerOptions
            {
                CaptureTimeout = s.Has("captureTimeout") ? TimeSpan.FromMilliseconds(s.Long("captureTimeout")) : TimeSpan.FromMilliseconds(500),
                TrackedOrigins = s.Has("trackedOrigins") ? TrackedOrigins(s.Element("trackedOrigins")) : null,
                IgnoreRemoteMapChanges = s.Bool("ignoreRemoteMapChanges", false),
                TimeProvider = ctx.Clock,
            };
            var scope = s.Element("scope");
            UndoManager manager = scope.ValueKind == JsonValueKind.String
                ? (scope.GetString() == "doc" ? new UndoManager(doc, options) : throw new OpsScriptException("undo.create: scope must be \"doc\" or a list of targets"))
                : new UndoManager(scope.EnumerateArray().Select(t => ctx.ResolveTarget(doc, t)).ToList(), options);
            manager.StackItemAdded += e => state.Events.Add(StackEvent(name, "stack-item-added", e));
            manager.StackItemUpdated += e => state.Events.Add(StackEvent(name, "stack-item-updated", e));
            manager.StackItemPopped += e => state.Events.Add(StackEvent(name, "stack-item-popped", e));
            manager.StackCleared += (undoCleared, redoCleared) => state.Events.Add(Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("observer", name);
                w.WriteString("type", "stack-cleared");
                w.WriteBoolean("undoStackCleared", undoCleared);
                w.WriteBoolean("redoStackCleared", redoCleared);
                w.WriteEndObject();
            }));
            state.UndoManagers[name] = manager;
        });
        Add("undo.stopCapturing", new[] { "name" }, None(), (ctx, s) => Of(ctx).Manager(s.String("name")).StopCapturing());
        foreach (var direction in new[] { "undo", "redo" })
        {
            var undo = direction == "undo";
            Add($"undo.{direction}", new[] { "name" }, new[] { "as" }, (ctx, s) =>
            {
                var manager = Of(ctx).Manager(s.String("name"));
                var popped = (undo ? manager.Undo() : manager.Redo()) != null;
                if (!s.Has("as")) return;
                Of(ctx).Record(s.String("as"), Json(w =>
                {
                    w.WriteStartObject();
                    w.WriteBoolean("popped", popped);
                    w.WriteNumber("undoStack", manager.UndoStack.Count);
                    w.WriteNumber("redoStack", manager.RedoStack.Count);
                    w.WriteEndObject();
                }));
            });
        }

        // ---------------------------------------------------------------- permanent user data
        Add("pud.setUserDescription", new[] { "doc", "clientId", "description" }, None(), (ctx, s) =>
        {
            var doc = ctx.Doc(s.String("doc"));
            Pud(ctx, s.String("doc")).SetUserMapping(doc, checked((uint)s.Long("clientId")), s.String("description"));
        });
        Add("pud.observe", new[] { "doc", "name" }, new[] { "clientIds", "deletedIds" }, (ctx, s) =>
        {
            var doc = ctx.Doc(s.String("doc"));
            var pud = Pud(ctx, s.String("doc"));
            Of(ctx).Record(s.String("name"), Json(w =>
            {
                w.WriteStartObject();
                w.WritePropertyName("users");
                WriteDump(w, doc.GetMap("users"));
                w.WritePropertyName("byClientId");
                w.WriteStartArray();
                if (s.Has("clientIds"))
                    foreach (var id in s.Element("clientIds").EnumerateArray())
                    {
                        w.WriteStartArray();
                        w.WriteNumberValue(id.GetUInt32());
                        if (pud.GetUserByClientId(id.GetUInt32()) is { } user) w.WriteStringValue(user); else w.WriteNullValue();
                        w.WriteEndArray();
                    }
                w.WriteEndArray();
                w.WritePropertyName("byDeletedId");
                w.WriteStartArray();
                if (s.Has("deletedIds"))
                    foreach (var pair in s.Element("deletedIds").EnumerateArray())
                    {
                        var client = pair[0].GetUInt32();
                        var clock = pair[1].GetInt64();
                        w.WriteStartArray();
                        w.WriteStartArray();
                        w.WriteNumberValue(client);
                        w.WriteNumberValue(clock);
                        w.WriteEndArray();
                        if (pud.GetUserByDeletedId(new ItemId(client, clock)) is { } user) w.WriteStringValue(user); else w.WriteNullValue();
                        w.WriteEndArray();
                    }
                w.WriteEndArray();
                w.WriteEndObject();
            }));
        });
    }

    // ------------------------------------------------------------------ helpers

    private static PermanentUserData Pud(OpsContext ctx, string replica)
    {
        var state = Of(ctx);
        if (state.PermanentUserData.TryGetValue(replica, out var existing)) return existing;
        // the oracle's setTimeout(fn, 0) is a virtual timer: it fires on the next tick, in scheduling order
        var created = new PermanentUserData(ctx.Doc(replica), new PermanentUserDataOptions
        {
            Defer = action => ctx.Clock.CreateTimer(_ => action(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan),
        });
        state.PermanentUserData[replica] = created;
        return created;
    }

    private static Doc FindSubdoc(OpsContext ctx, OpsStep s)
    {
        var guid = s.String("guid");
        var index = s.Has("index") ? s.Int("index") : 0;
        var found = ctx.Doc(s.String("doc")).Subdocs.Where(d => d.Guid == guid).ToList();
        return index < found.Count ? found[index] : throw new OpsScriptException($"{s.Kind}: no subdoc {guid}#{index}");
    }

    private static DocOptions SubdocOptions(OpsStep s)
    {
        var options = new DocOptions { Guid = s.String("guid") };
        if (!s.Has("opts")) return options;
        foreach (var p in s.Element("opts").EnumerateObject())
        {
            options = p.Name switch
            {
                "gc" => options with { Gc = p.Value.GetBoolean() },
                "autoLoad" => options with { AutoLoad = p.Value.GetBoolean() },
                "shouldLoad" => options with { ShouldLoad = p.Value.GetBoolean() },
                "collectionid" => options with { CollectionId = p.Value.GetString() },
                "meta" => options with { Meta = MetaValue(p.Value) },
                _ => throw new OpsScriptException($"subdoc.add: unknown option '{p.Name}'"),
            };
        }
        return options;
    }

    private static YValue MetaValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString()!,
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new OpsScriptException("subdoc.add: meta must be a string, number or boolean"),
    };

    private static ISet<object?> TrackedOrigins(JsonElement list)
    {
        var set = new HashSet<object?>();
        foreach (var e in list.EnumerateArray()) set.Add(e.ValueKind == JsonValueKind.Null ? null : e.GetString());
        return set;
    }

    private static string StackEvent(string observer, string type, StackItemEvent e) => Json(w =>
    {
        w.WriteStartObject();
        w.WriteString("observer", observer);
        w.WriteString("type", type);
        w.WriteString("kind", e.IsRedo ? "redo" : "undo");
        w.WriteEndObject();
    });

    private static void Guids(Utf8JsonWriter w, string name, IReadOnlyList<Doc> docs)
    {
        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (var d in docs) w.WriteStringValue(d.Guid);
        w.WriteEndArray();
    }

    private static byte[] Encode(YSnapshot snapshot, UpdateFormat format)
    {
        var w = new ArrayBufferWriter<byte>();
        snapshot.Encode(w, format);
        return w.WrittenSpan.ToArray();
    }

    private static void Check(OpsStep s, YStatus status)
    {
        if (!status.Ok) throw new OpsScriptException($"{s.Kind}: library reported {status.Error}");
    }

    private static void WriteDelta(Utf8JsonWriter w, TextDeltaOp[] delta)
    {
        w.WriteStartArray();
        foreach (var op in delta)
        {
            w.WriteStartObject();
            switch (op.Kind)
            {
                case DeltaKind.Insert:
                    w.WritePropertyName("insert");
                    if (op.Text != null) w.WriteStringValue(op.Text);
                    else WriteEntry(w, op.Embed);
                    break;
                case DeltaKind.Retain: w.WriteNumber("retain", op.Length); break;
                default: w.WriteNumber("delete", op.Length); break;
            }
            if (op.Attributes.Kind != YValueKind.Undefined)
            {
                w.WritePropertyName("attributes");
                Tag(op.Attributes).Write(w);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteEntry(Utf8JsonWriter w, YEntry entry)
    {
        switch (entry.Kind)
        {
            case YEntryKind.Type: WriteDump(w, entry.Type!); break;
            case YEntryKind.Doc:
                w.WriteStartObject();
                w.WriteString("kind", "subdoc");
                w.WriteString("guid", entry.Doc!.Guid);
                w.WriteEndObject();
                break;
            default: Tag(entry.Value).Write(w); break;
        }
    }

    /// <summary>The typed dump of ADR 0016 for the container types a PermanentUserData store uses.</summary>
    private static void WriteDump(Utf8JsonWriter w, YType type)
    {
        w.WriteStartObject();
        switch (type)
        {
            case YMap map:
                w.WriteString("kind", "map");
                w.WritePropertyName("pairs");
                w.WriteStartArray();
                foreach (var kv in map)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(kv.Key);
                    WriteEntry(w, kv.Value);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                break;
            case YArray array:
                w.WriteString("kind", "array");
                w.WritePropertyName("items");
                w.WriteStartArray();
                foreach (var item in array.ToArray()) WriteEntry(w, item);
                w.WriteEndArray();
                break;
            default:
                w.WriteString("kind", "unsupported");
                w.WriteString("type", type.GetType().Name);
                break;
        }
        w.WriteEndObject();
    }

    /// <summary>A library value as an ADR 0011 tagged value (the reverse of <see cref="TaggedValue.ToYValue"/>).</summary>
    public static TaggedValue Tag(YValue v)
    {
        switch (v.Kind)
        {
            case YValueKind.Undefined: return TaggedValue.Undefined;
            case YValueKind.Null: return TaggedValue.Null;
            case YValueKind.Bool: return TaggedValue.FromBool(v.AsBool());
            case YValueKind.Number: return TaggedValue.FromNumber(v.AsNumber());
            case YValueKind.BigInt: return v.TryGetBigInt(out var big) ? TaggedValue.BigInt(big) : TaggedValue.Undefined;
            case YValueKind.String: return TaggedValue.FromString(v.AsString());
            case YValueKind.Bytes:
                v.TryGetBytes(out var bytes);
                return TaggedValue.FromBytes(bytes.ToArray());
            case YValueKind.Array:
            {
                v.TryGetArray(out var items);
                var list = new List<TaggedValue>();
                foreach (var item in items) list.Add(Tag(item));
                return TaggedValue.FromArray(list);
            }
            default:
            {
                v.TryGetObject(out var view);
                var pairs = new List<KeyValuePair<string, TaggedValue>>();
                for (var i = 0; i < view.Count; i++) pairs.Add(new(view[i].Key, Tag(view[i].Value)));
                return TaggedValue.FromObject(pairs);
            }
        }
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            write(w);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>Per-run state of the doc-feature steps: named snapshots, positions, undo managers and what they recorded.</summary>
public sealed class DocFeatureState
{
    public DocFeatureState(OpsContext ctx) => Context = ctx;

    public OpsContext Context { get; }
    public Dictionary<string, YSnapshot> Snapshots { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, byte[]> RelativePositions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, UndoManager> UndoManagers { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PermanentUserData> PermanentUserData { get; } = new(StringComparer.Ordinal);

    /// <summary>JSON <c>value</c> of each named result (the fixture's <c>results</c> observation).</summary>
    public Dictionary<string, string> Results { get; } = new(StringComparer.Ordinal);

    /// <summary>JSON of each event record in firing order (the fixture's <c>events</c> observation).</summary>
    public List<string> Events { get; } = new();

    public void Record(string name, string json)
    {
        if (!Results.TryAdd(name, json)) throw new OpsScriptException($"result '{name}' is recorded twice");
    }

    public YSnapshot Snapshot(string name) =>
        Snapshots.TryGetValue(name, out var s) ? s : throw new OpsScriptException($"unknown snapshot '{name}'");

    public UndoManager Manager(string name) =>
        UndoManagers.TryGetValue(name, out var m) ? m : throw new OpsScriptException($"unknown undo manager '{name}'");
}

/// <summary>
/// Members of the implementation that the stub API of ADR 0008 does not expose but the doc-feature steps and tests need. Each is looked up by
/// name (public or internal) so the TestKit keeps compiling against the stub; a missing member is a clear failure, not a skip.
/// <list type="bullet">
/// <item><c>Doc.CollectGarbage()</c>: manual garbage collection of the whole store (JS <c>Y.tryGc(createDeleteSetFromStructStore(store), store, gcFilter)</c>).</item>
/// <item><c>Doc.ShouldLoad</c>, <c>Doc.AutoLoad</c>: the subdoc load flags (DOC-01, SUB-03).</item>
/// <item><c>Doc.CollectionId</c> (string?) and <c>Doc.Meta</c> (YValue): DOC-17.</item>
/// <item><c>Doc.SignalSynced(bool)</c>: the provider hook that raises JS <c>emit('sync', [isSynced])</c> (DOC-13, DOC-14).</item>
/// </list>
/// </summary>
public static class DocSeams
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static InvalidOperationException Missing(string member, string contract) =>
        new($"Doc.{member} is missing. Spec 8 needs {contract} (see DocSeams).");

    private static object? Get(Doc doc, string name, string contract) =>
        (typeof(Doc).GetProperty(name, Any) ?? throw Missing(name, contract)).GetValue(doc);

    public static bool ShouldLoad(Doc doc) => (bool)Get(doc, "ShouldLoad", "`bool ShouldLoad`")!;
    public static bool AutoLoad(Doc doc) => (bool)Get(doc, "AutoLoad", "`bool AutoLoad`")!;
    public static string? CollectionId(Doc doc) => (string?)Get(doc, "CollectionId", "`string? CollectionId`");
    public static YValue Meta(Doc doc) => (YValue)Get(doc, "Meta", "`YValue Meta`")!;

    public static void CollectGarbage(Doc doc) => Invoke(doc, "CollectGarbage", "`void CollectGarbage()`");

    public static void SignalSynced(Doc doc, bool isSynced) => Invoke(doc, "SignalSynced", "`void SignalSynced(bool isSynced)`", isSynced);

    private static void Invoke(Doc doc, string name, string contract, params object[] args)
    {
        var m = typeof(Doc).GetMethod(name, Any, null, args.Select(a => a.GetType()).ToArray(), null) ?? throw Missing(name, contract);
        try { m.Invoke(doc, args); }
        catch (TargetInvocationException e) when (e.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
    }
}
