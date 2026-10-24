using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using YSharp.TestKit.Time;

namespace YSharp.Tests.DocFeatures;

/// <summary>Shared helpers of the undo, snapshot and relative-position tests.</summary>
internal static class Ds
{
    public static string Json(YValue v)
    {
        var w = new ArrayBufferWriter<byte>();
        Assert.True(v.TryWriteJson(w).Ok);
        return Canon(Encoding.UTF8.GetString(w.WrittenSpan));
    }

    /// <summary>JSON with object members sorted by name: the upstream <c>t.compare</c> ignores key order, so the tests do too.</summary>
    public static string Canon(string json) => (Sort(JsonNode.Parse(json)) ?? JsonValue.Create<string?>(null))?.ToJsonString() ?? "null";

    private static JsonNode? Sort(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Sort).ToArray()),
        _ => n?.DeepClone(),
    };

    /// <summary>A type's <c>toJSON()</c> as compact JSON text.</summary>
    public static string J(YType t) => Json(t.ToJson());

    /// <summary>A text delta as <c>[text{attributes}]</c> runs, e.g. <c>[b][cxy{"bold":true}][z]</c>.</summary>
    public static string Delta(YText t, YSnapshot? snapshot = null, YSnapshot? previous = null) =>
        string.Concat(t.ToDelta(snapshot, previous).Select(op =>
            "[" + (op.Text ?? "<embed>") + (op.Attributes.Kind == YValueKind.Undefined ? "" : Json(op.Attributes)) + "]"));

    /// <summary>Map lookup that fails the test when the key is absent.</summary>
    public static YEntry Get(YMap map, string key) => map.TryGet(key, out var e) ? e : throw new Xunit.Sdk.XunitException($"key '{key}' is absent");

    public static bool Absent(YMap map, string key) => !map.TryGet(key, out _);

    public static YValue Attrs(params (string Key, YValue Value)[] pairs) =>
        YValue.Object(pairs.Select(p => new KeyValuePair<string, YValue>(p.Key, p.Value)).ToArray());

    /// <summary>
    /// Options with a virtual clock, so capture merging never depends on how fast the test runs. The clock is shared per
    /// call site through <paramref name="clock"/>; without it a fresh one at a fixed instant is used, which behaves like JS code that runs
    /// within one millisecond (every change merges unless the timeout is 0 or capturing is stopped).
    /// </summary>
    public static UndoManagerOptions Opts(VirtualTimeProvider? clock = null, int? captureTimeoutMs = null, ISet<object?>? trackedOrigins = null,
        bool ignoreRemoteMapChanges = false, Func<ItemView, bool>? deleteFilter = null, Func<Transaction, bool>? captureTransaction = null) =>
        new()
        {
            TimeProvider = clock ?? new VirtualTimeProvider(),
            CaptureTimeout = captureTimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromMilliseconds(500),
            TrackedOrigins = trackedOrigins,
            IgnoreRemoteMapChanges = ignoreRemoteMapChanges,
            DeleteFilter = deleteFilter,
            CaptureTransaction = captureTransaction,
        };

    public static UndoManager Um(YType scope, VirtualTimeProvider? clock = null, int? captureTimeoutMs = null, ISet<object?>? trackedOrigins = null,
        bool ignoreRemoteMapChanges = false, Func<ItemView, bool>? deleteFilter = null) =>
        new(scope, Opts(clock, captureTimeoutMs, trackedOrigins, ignoreRemoteMapChanges, deleteFilter));

    public static UndoManager Um(IReadOnlyList<YType> scope, VirtualTimeProvider? clock = null, int? captureTimeoutMs = null, ISet<object?>? trackedOrigins = null) =>
        new(scope, Opts(clock, captureTimeoutMs, trackedOrigins));

    public static ISet<object?> Origins(params object?[] origins) => new HashSet<object?>(origins);
}
