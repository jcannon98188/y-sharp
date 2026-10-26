using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Rng;

namespace YSharp.TestKit.Sim;

public enum NetworkAction { None, DisconnectRandom, ReconnectRandom, FlushAll, FlushRandom }

/// <summary>
/// Checks that need y-sharp internals (the struct store, pending buffers, delete sets). The TestKit sees the public API only
/// (ADR 0008), so <c>YSharp.Tests</c> supplies an implementation through <c>InternalsVisibleTo</c>.
/// </summary>
public interface ISimInternals
{
    /// <summary>JS: <c>pendingDs === null &amp;&amp; pendingStructs === null</c> after full sync.</summary>
    void AssertNoPending(Doc doc);

    /// <summary>
    /// JS <c>compareStructStores</c> plus SIM-02: the delete sets must be equal (upstream discards that result).
    /// </summary>
    void AssertStoresEqual(Doc a, Doc b);
}

public sealed class SimResult<T>
{
    public SimResult(TestConnector connector, IReadOnlyList<TestYInstance> users, IReadOnlyList<T> testObjects)
    {
        TestConnector = connector;
        Users = users;
        TestObjects = testObjects;
    }

    public TestConnector TestConnector { get; }
    public IReadOnlyList<TestYInstance> Users { get; }
    public IReadOnlyList<T> TestObjects { get; }
}

/// <summary>
/// Port of the JS <c>testHelper</c> functions <c>init</c>, <c>compare</c> and <c>applyRandomTests</c> (ADR 0015, 0017).
/// Draw order matches upstream; the five upstream defects are fixed as SIM-01..05 (see each member).
/// Registered in docs/inventory/simulation.yaml: SIM-10 TestConnector, SIM-11 Init, SIM-12 Compare, SIM-13 ApplyRandomTests, SIM-14 V2 framing.
/// </summary>
public static class SimHarness
{
    /// <summary>
    /// The one coin-flip draw of <c>init</c>. SIM-04: it selects V1 or V2 for the whole run and the run really uses it
    /// (upstream consumes the draw but falls back to V1 in both branches).
    /// </summary>
    public static UpdateFormat ChooseEncoding(IPrng gen) => Prng.Bool(gen) ? UpdateFormat.V2 : UpdateFormat.V1;

    /// <summary>
    /// JS <c>init</c>: connector, <paramref name="users"/> clients with ids <c>0..n-1</c>, all synced. A <paramref name="recorder"/>
    /// receives the op log of the run (ADR 0017 golden fixtures, draw parity, repro files).
    /// </summary>
    public static SimResult<T> Init<T>(IPrng gen, int users = 5, Func<TestYInstance, T>? initTestObject = null, ISimRecorder? recorder = null)
    {
        var format = ChooseEncoding(gen);
        recorder?.Begin(format);
        var connector = new TestConnector(gen, format, recorder);
        var list = new List<TestYInstance>();
        for (var i = 0; i < users; i++) list.Add(connector.CreateY(i));
        connector.SyncAll();
        var objects = list.Select(u => initTestObject != null ? initTestObject(u) : default!).ToList();
        return new SimResult<T>(connector, list, objects);
    }

    public static SimResult<object?> Init(IPrng gen, int users = 5) => Init<object?>(gen, users);

    /// <summary>
    /// The per-iteration network draws of <c>applyRandomTests</c>, in upstream order and with its short-circuit:
    /// <c>int32(0,100) &lt;= 2</c> then a <c>bool</c>; else <c>int32 &lt;= 1</c>; else <c>int32 &lt;= 50</c>.
    /// </summary>
    public static NetworkAction ChooseNetworkAction(IPrng gen)
    {
        if (Prng.Int32(gen, 0, 100) <= 2) return Prng.Bool(gen) ? NetworkAction.DisconnectRandom : NetworkAction.ReconnectRandom;
        if (Prng.Int32(gen, 0, 100) <= 1) return NetworkAction.FlushAll;
        if (Prng.Int32(gen, 0, 100) <= 50) return NetworkAction.FlushRandom;
        return NetworkAction.None;
    }

    /// <summary>JS <c>applyRandomTests</c> with 5 users: random network events, one random mod per iteration, then <see cref="Compare"/>.</summary>
    public static SimResult<T> ApplyRandomTests<T>(
        IPrng gen,
        IReadOnlyList<Action<TestYInstance, IPrng, T>> mods,
        int iterations,
        Func<TestYInstance, T>? initTestObject = null,
        ISimInternals? internals = null,
        ISimRecorder? recorder = null,
        Action<SimResult<T>>? beforeDestroy = null)
    {
        var result = Init(gen, 5, initTestObject, recorder);
        var connector = result.TestConnector;
        for (var i = 0; i < iterations; i++)
        {
            switch (ChooseNetworkAction(gen))
            {
                case NetworkAction.DisconnectRandom: connector.DisconnectRandom(); break;
                case NetworkAction.ReconnectRandom: connector.ReconnectRandom(); break;
                case NetworkAction.FlushAll: connector.FlushAllMessages(); break;
                case NetworkAction.FlushRandom: connector.FlushRandomMessage(); break;
            }
            var user = (int)Prng.Int32(gen, 0, result.Users.Count - 1);
            var mod = Prng.OneOf(gen, mods);
            mod(result.Users[user], gen, result.TestObjects[user]);
        }
        Compare(result.Users, internals, beforeDestroy == null ? null : () => beforeDestroy(result));
        return result;
    }

    /// <summary>
    /// JS <c>compare</c>: reconnect and flush everything, rebuild one extra replica per user from <c>MergeUpdates</c> of the
    /// updates it saw, then require every consecutive pair of replicas (real and merged) to agree on array, map, XML, text
    /// delta, state vector, snapshot and (through <paramref name="internals"/>) struct store and delete set.
    /// Differences from upstream: the caller's list is not mutated, and the delete-set result is asserted (SIM-02).
    /// All docs are destroyed afterwards; <paramref name="beforeDestroy"/> runs after the checks, while the replicas can still be read
    /// (upstream reads them after <c>compare</c> destroyed them, which only works in JS).
    /// </summary>
    public static void Compare(IReadOnlyList<TestYInstance> users, ISimInternals? internals = null, Action? beforeDestroy = null)
    {
        Assert.True(users.Count >= 1, "compare needs a replica");   // one user is fine: it is compared with its merged rebuild, as upstream (users: 1)
        var connector = users[0].Tc;
        var format = connector.Format;
        foreach (var u in users) u.Connect();
        while (connector.FlushAllMessages()) { }

        var docs = users.Select(u => u.Doc).ToList();
        foreach (var user in users)
        {
            var merged = new ArrayBufferWriter<byte>();
            var status = YUpdate.TryMerge(user.Updates.Select(b => (ReadOnlyMemory<byte>)b).ToList(), merged, format);
            Assert.True(status.Ok, $"MergeUpdates failed for user {user.UserId}: {status.Error}");
            var doc = new Doc();
            doc.ApplyUpdate(merged.WrittenSpan, null, format);
            docs.Add(doc);
        }

        if (internals != null) foreach (var d in docs) internals.AssertNoPending(d);

        var arrays = docs.Select(d => d.GetArray("array").ToJson()).ToList();
        var maps = docs.Select(d => d.GetMap("map").ToJson()).ToList();
        var xmls = docs.Select(d => d.GetXmlElement("xml").ToString()).ToList();
        var texts = docs.Select(d => d.GetText("text").ToDelta()).ToList();

        for (var i = 0; i < docs.Count - 1; i++)
        {
            // SIM-01 spirit: never compare a replica with itself.
            Assert.False(ReferenceEquals(docs[i], docs[i + 1]), "compare must pair different replicas");
            var where = $"replicas {i} and {i + 1}";
            Assert.True(JsEquals(arrays[i], arrays[i + 1]), $"array differs between {where}");
            Assert.True(JsEquals(maps[i], maps[i + 1]), $"map differs between {where}");
            Assert.Equal(xmls[i], xmls[i + 1]);
            AssertDeltasEqual(texts[i], texts[i + 1], where);
            Assert.Equal(StateVectorBytes(docs[i]), StateVectorBytes(docs[i + 1]));
            Assert.Equal(SnapshotBytes(docs[i]), SnapshotBytes(docs[i + 1]));
            internals?.AssertStoresEqual(docs[i], docs[i + 1]);
        }

        beforeDestroy?.Invoke();
        foreach (var d in docs) d.Destroy();
    }

    /// <summary>
    /// lib0 <c>t.compare</c> on toJSON values: object members match by key whatever their order, since YMap.toJSON order follows each replica's
    /// insertion history in JS too. <see cref="YValue.Equals(YValue)"/> is wire equality and keeps member order, so it is too strict here.
    /// </summary>
    public static bool JsEquals(YValue a, YValue b)
    {
        if (a.Kind != b.Kind) return false;
        if (a.TryGetArray(out var xa) && b.TryGetArray(out var xb))
        {
            if (xa.Length != xb.Length) return false;
            for (var i = 0; i < xa.Length; i++) if (!JsEquals(xa[i], xb[i])) return false;
            return true;
        }
        if (a.TryGetObject(out var oa) && b.TryGetObject(out var ob))
        {
            if (oa.Count != ob.Count) return false;
            for (var i = 0; i < oa.Count; i++)
                if (!ob.TryGetValue(oa[i].Key, out var v) || !JsEquals(oa[i].Value, v)) return false;
            return true;
        }
        return a.Equals(b);
    }

    private static byte[] StateVectorBytes(Doc doc)
    {
        var w = new ArrayBufferWriter<byte>();
        doc.EncodeStateVector(w);
        return w.WrittenSpan.ToArray();
    }

    private static byte[] SnapshotBytes(Doc doc)
    {
        var w = new ArrayBufferWriter<byte>();
        YSnapshot.Create(doc).Encode(w);
        return w.WrittenSpan.ToArray();
    }

    private static void AssertDeltasEqual(TextDeltaOp[] a, TextDeltaOp[] b, string where)
    {
        Assert.True(a.Length == b.Length, $"text delta length differs between {where}");
        for (var i = 0; i < a.Length; i++)
        {
            var x = a[i];
            var y = b[i];
            var same = x.Kind == y.Kind && x.Length == y.Length && x.Text == y.Text
                       && x.Attributes.Equals(y.Attributes) && EmbedEquals(x.Embed, y.Embed);
            Assert.True(same, $"text delta op {i} differs between {where}");
        }
    }

    private static bool EmbedEquals(YEntry a, YEntry b) =>
        a.Kind == b.Kind && (a.Kind == YEntryKind.Type ? a.Type!.ToJson().Equals(b.Type!.ToJson()) : a.Value.Equals(b.Value));
}
