using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Traceability;
using static YSharp.Tests.Types.TypeTestExtensions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Types;

/// <summary>Behaviour of <see cref="YMap"/> (M-1..M-12, Q26) and the map tests ported from y-map.tests.js that are not about events.</summary>
[Requires(Feature.Map)]
public sealed class MapTests
{
    private static Doc NewDoc(ulong client = 1) => new(new DocOptions { ClientId = client });

    // ------------------------------------------------------------------ behaviours

    [Fact, Covers("M-1", "M-6")]
    [Requires(Feature.Events)]
    public void Set_ReplacesTheValue_AndCountStaysOne()
    {
        var doc = NewDoc();
        var map = doc.GetMap("m");
        var updates = 0;
        doc.Update += (_, _, _, _) => updates++;
        map.Set("k", 1);
        map.Set("k", 2);
        map.Set("k", 3);
        Assert.Equal(3, map.Get("k").AsNumber());
        Assert.Equal(1, map.Count);
        Assert.Equal(3, updates);
        // the replaced values are separate (deleted) items, so the state vector advanced once per set
        Assert.Equal(3L, doc.GetStateVector()[1]);
    }

    [Fact, Covers("M-2")]
    public void Get_OfAMissingOrDeletedKey_IsAbsent()
    {
        var map = NewDoc().GetMap("m");
        Assert.False(map.TryGet("missing", out _));
        Assert.Equal(YValueKind.Undefined, map.Get("missing").Kind);
        map.Set("k", 1);
        map.Delete("k");
        Assert.False(map.TryGet("k", out _));
        Assert.False(map.Has("k"));
    }

    [Fact, Covers("M-4", "TX-3")]
    [Requires(Feature.Events)]
    public void Delete_OfAMissingKey_ChangesNothing_ButTheTransactionStillRuns()
    {
        var doc = NewDoc();
        var map = doc.GetMap("m");
        int events = 0, updates = 0, after = 0;
        map.Observe((_, _) => events++);
        doc.Update += (_, _, _, _) => updates++;
        doc.AfterTransaction += (_, _) => after++;
        Assert.False(map.Delete("missing"));
        Assert.Equal(0, events);
        Assert.Equal(0, updates);
        Assert.Equal(1, after);
        map.Set("k", 1);
        Assert.True(map.Delete("k"));
        Assert.False(map.Delete("k"));
    }

    [Fact, Covers("M-6", "Q26")]
    public void Keys_FollowFirstInsertionOrder_AndAResetKeepsItsPosition()
    {
        var map = NewDoc().GetMap("m");
        map.Set("b", 1);
        map.Set("a", 2);
        map.Set("10", 3);
        map.Set("2", 4);
        Assert.Equal(new[] { "b", "a", "10", "2" }, map.Keys);
        map.Delete("b");
        map.Set("b", 5);
        map.Set("a", 6);
        Assert.Equal(new[] { "b", "a", "10", "2" }, map.Keys);
        Assert.Equal(new[] { "b", "a", "10", "2" }, TypeDump.Enumerate(map).Select(kv => kv.Key).ToArray());
        map.Delete("a");
        Assert.Equal(new[] { "b", "10", "2" }, map.Keys);
        Assert.Equal(3, map.Count);
    }

    [Fact, Covers("M-6")]
    public void Keys_FollowThisMapsInsertionOrder_EvenWhenAnotherMapSawTheKeysFirst()
    {
        var doc = NewDoc();
        var other = doc.GetMap("other");
        other.Set("b", 1);
        other.Set("a", 2);
        var map = doc.GetMap("m");
        map.Set("a", 3);
        map.Set("b", 4);
        Assert.Equal(new[] { "a", "b" }, map.Keys);
        Assert.Equal(new[] { "b", "a" }, other.Keys);
    }

    [Fact, Covers("M-7")]
    [Requires(Feature.Text, Feature.Array)]
    public void ToJson_ConvertsNestedTypes_AndKeepsUndefinedMembers()
    {
        var map = NewDoc().GetMap("m");
        var nested = map.Set("nested", new YMap());
        nested.Set("deep", new YArray());
        nested.ArrayOf("deep").Push(new YEntry[] { 1, 2 });
        map.Set("text", new YText("t"));
        map.Set("u", default(YValue));
        map.Set("n", 1);
        var json = map.ToJson();
        Assert.True(json.TryGetObject(out var obj));
        Assert.Equal(4, obj.Count);
        Assert.True(obj.TryGetValue("u", out var u));
        Assert.Equal(YValueKind.Undefined, u.Kind);
        Assert.True(obj.TryGetValue("nested", out var nestedJson));
        Assert.Equal("""{"deep":[1,2]}""", nestedJson.Json());
        Assert.True(obj.TryGetValue("text", out var textJson));
        Assert.Equal("\"t\"", textJson.Json());
    }

    [Fact, Covers("M-8")]
    [Requires(Feature.Sync)]
    public void ValueKinds_BetweenReplicas_RoundTrip()
    {
        var net = Net.Init(2);
        var map0 = net.Peers[0].Map;
        map0.Set("null", Null);
        map0.Set("number", 1.5);
        map0.Set("int", 7);
        map0.Set("string", "hello Y");
        map0.Set("true", true);
        map0.Set("false", false);
        map0.Set("object", Obj(("key", Obj(("key2", "value")))));
        map0.Set("array", YValue.Array(new YValue[] { 1, "a", YValue.Null }));
        map0.Set("bytes", YValue.Bytes(new byte[] { 1, 2, 3 }));
        map0.Set("big", YValue.BigInt(1L << 60));
        map0.Set("neg0", -0.0);
        map0.Set("nested", new YMap());
        map0.MapOf("nested").Set("x", 1);
        net.Flush();
        var map1 = net.Peers[1].Map;
        Assert.Equal(map0.Count, map1.Count);
        Assert.Equal(YValueKind.Null, map1.Get("null").Kind);
        Assert.Equal(1.5, map1.Get("number").AsNumber());
        Assert.Equal("hello Y", map1.Get("string").AsString());
        Assert.True(map1.Get("true").AsBool());
        Assert.False(map1.Get("false").AsBool());
        Assert.Equal("""{"key":{"key2":"value"}}""", map1.Get("object").Json());
        Assert.Equal("""[1,"a",null]""", map1.Get("array").Json());
        Assert.Equal(YEntryKind.Binary, map1.TryGet("bytes", out var b) ? b.Kind : default);
        Assert.Equal(YValueKind.BigInt, map1.Get("big").Kind);
        Assert.True(map1.Get("neg0").TryGetNumber(out var n) && double.IsNegative(n) && n == 0);
        Assert.Equal(1, map1.MapOf("nested").Get("x").AsNumber());
        net.Compare();
    }

    [Fact, Covers("M-11", "Q26")]
    [Requires(Feature.Sync)]
    public void KeyEdgeCases_EmptyUnicodeAndProto_AreOrdinaryKeys()
    {
        var net = Net.Init(2);
        var map0 = net.Peers[0].Map;
        foreach (var key in new[] { "", "__proto__", "constructor", "é", "\U0001F600", "a b", "10", "-1", "1.5" })
            map0.Set(key, key.Length);
        net.Flush();
        var map1 = net.Peers[1].Map;
        Assert.Equal(9, map1.Count);
        Assert.Equal(0, map1.Get("").AsNumber());
        // JS would assign the prototype in toJSON; here it is a plain key (a recorded, JS-only hazard that is not replicated)
        Assert.Equal(9, map1.Get("__proto__").AsNumber());
        Assert.Equal(2, map1.Get("\U0001F600").AsNumber());
        Assert.True(map1.ToJson().TryGetObject(out var obj) && obj.TryGetValue("__proto__", out _));
        net.Compare();
    }

    [Fact, Covers("M-12")]
    [Requires(Feature.Array)]
    public void NestedType_Deleted_DeletesItsSubtree()
    {
        var doc = NewDoc();
        var map = doc.GetMap("m");
        var nested = map.Set("nested", new YMap());
        nested.Set("a", 1);
        var list = nested.Set("list", new YArray());
        list.Push(new YEntry[] { 1, 2 });
        map.Delete("nested");
        Assert.False(nested.TryGet("a", out _));
        Assert.Equal(0, nested.Count);
        Assert.Equal(0, list.Length);
        // the type object stays referenced and attached, but it is deleted
        Assert.True(nested.IsIntegrated);
        Assert.Equal("{}", map.Json());
        Assert.False(map.Has("nested"));
    }

    // ------------------------------------------------------------------ ports

    [Fact, PortOf("y-map.tests.js:testBasicMapTests", "M-1", "M-6", "M-8")]
    [Requires(Feature.Array, Feature.Sync)]
    public void Map_EveryValueKind_RoundTripsBetweenPeers()
    {
        var net = Net.Init(3);
        var (map0, map1, map2) = (net.Peers[0].Map, net.Peers[1].Map, net.Peers[2].Map);
        net.Users[2].Disconnect();

        map0.Set("null", Null);
        map0.Set("number", 1);
        map0.Set("string", "hello Y");
        map0.Set("object", Obj(("key", Obj(("key2", "value")))));
        map0.Set("y-map", new YMap());
        map0.Set("boolean1", true);
        map0.Set("boolean0", false);
        var map = map0.MapOf("y-map");
        map.Set("y-array", new YArray());
        var array = map.ArrayOf("y-array");
        array.Insert(0, new YEntry[] { 0 });
        array.Insert(0, new YEntry[] { -1 });

        void Check(YMap m, string who)
        {
            Assert.Equal(YValueKind.Null, m.Get("null").Kind);
            Assert.Equal(1, m.Get("number").AsNumber());
            Assert.Equal("hello Y", m.Get("string").AsString());
            Assert.False(m.Get("boolean0").AsBool());
            Assert.True(m.Get("boolean1").AsBool());
            Assert.Equal("""{"key":{"key2":"value"}}""", m.Get("object").Json());
            Assert.Equal(-1, m.MapOf("y-map").ArrayOf("y-array")[0].Value.AsNumber());
            Assert.Equal(7, m.Count);
        }
        Check(map0, "client 0 computed the change");

        net.Users[2].Connect();
        net.Flush();
        Check(map1, "client 1 received the update");
        Check(map2, "client 2 received the update - was disconnected");
        net.Compare();
    }

    [Fact, PortOf("y-map.tests.js:testGetAndSetOfMapProperty", "M-3", "M-2")]
    [Requires(Feature.Sync)]
    public void Map_GetAndSetOfProperty_ReturnsTheStoredValues()
    {
        var net = Net.Init(2);
        var map0 = net.Peers[0].Map;
        map0.Set("stuff", "stuffy");
        map0.Set("undefined", default(YValue));
        map0.Set("null", Null);
        Assert.Equal("stuffy", map0.Get("stuff").AsString());
        net.Flush();
        foreach (var peer in net.Peers)
        {
            var u = peer.Doc.GetMap("map");
            Assert.Equal("stuffy", u.Get("stuff").AsString());
            Assert.Equal(YValueKind.Undefined, u.Get("undefined").Kind);
            Assert.True(u.Has("undefined"));
            Assert.Equal(YValueKind.Null, u.Get("null").Kind);
        }
        net.Compare();
    }

    [Fact, PortOf("y-map.tests.js:testGetAndSetOfMapPropertySyncs", "M-1")]
    [Requires(Feature.Sync)]
    public void Map_GetAndSetOfProperty_SyncsToThePeer()
    {
        var net = Net.Init(2);
        var map0 = net.Peers[0].Map;
        map0.Set("stuff", "stuffy");
        Assert.Equal("stuffy", map0.Get("stuff").AsString());
        net.Flush();
        foreach (var peer in net.Peers) Assert.Equal("stuffy", peer.Doc.GetMap("map").Get("stuff").AsString());
        net.Compare();
    }

    [Fact, PortOf("y-map.tests.js:testGetAndSetOfMapPropertyWithConflict", "M-9")]
    [Requires(Feature.Sync)]
    public void Map_ConcurrentSetOfTheSameKey_ConvergesOnOneValue()
    {
        var net = Net.Init(3);
        net.Peers[0].Map.Set("stuff", "c0");
        net.Peers[1].Map.Set("stuff", "c1");
        net.Flush();
        foreach (var peer in net.Peers) Assert.Equal("c1", peer.Doc.GetMap("map").Get("stuff").AsString());
        net.Compare();
    }

    [Fact, PortOf("y-map.tests.js:testSizeAndDeleteOfMapProperty", "M-6")]
    [Requires(Feature.Sync)]
    public void Map_DeleteOfAProperty_UpdatesTheSize()
    {
        var map0 = Net.Init(1).Peers[0].Map;
        map0.Set("stuff", "c0");
        map0.Set("otherstuff", "c1");
        Assert.Equal(2, map0.Count);
        map0.Delete("stuff");
        Assert.Equal(1, map0.Count);
        map0.Delete("otherstuff");
        Assert.Equal(0, map0.Count);
    }

    [Fact, PortOf("y-map.tests.js:testGetAndSetAndDeleteOfMapProperty", "M-4")]
    [Requires(Feature.Sync)]
    public void Map_ConcurrentSetAndDelete_ConvergesOnTheDelete()
    {
        var net = Net.Init(3);
        net.Peers[0].Map.Set("stuff", "c0");
        net.Peers[1].Map.Set("stuff", "c1");
        net.Peers[1].Map.Delete("stuff");
        net.Flush();
        foreach (var peer in net.Peers) Assert.False(peer.Doc.GetMap("map").TryGet("stuff", out _));
        net.Compare();
    }

    [Fact, PortOf("y-map.tests.js:testSetAndClearOfMapProperties", "M-5")]
    [Requires(Feature.Sync)]
    public void Map_Clear_RemovesEveryProperty()
    {
        var net = Net.Init(1);
        var map0 = net.Peers[0].Map;
        map0.Set("stuff", "c0");
        map0.Set("otherstuff", "c1");
        map0.Clear();
        net.Flush();
        foreach (var peer in net.Peers)
        {
            var u = peer.Doc.GetMap("map");
            Assert.False(u.TryGet("stuff", out _));
            Assert.False(u.TryGet("otherstuff", out _));
            Assert.Equal(0, u.Count);
        }
        net.Compare();
    }

    [Fact, PortOf("y-map.tests.js:testSetAndClearOfMapPropertiesWithConflicts", "M-5", "M-9")]
    [Requires(Feature.Sync)]
    public void Map_ConcurrentSetAndClear_Converges()
    {
        var net = Net.Init(4);
        var (map0, map1, map2, map3) = (net.Peers[0].Map, net.Peers[1].Map, net.Peers[2].Map, net.Peers[3].Map);
        map0.Set("stuff", "c0");
        map1.Set("stuff", "c1");
        map1.Set("stuff", "c2");
        map2.Set("stuff", "c3");
        net.Flush();
        map0.Set("otherstuff", "c0");
        map1.Set("otherstuff", "c1");
        map2.Set("otherstuff", "c2");
        map3.Set("otherstuff", "c3");
        map3.Clear();
        net.Flush();
        foreach (var peer in net.Peers)
        {
            var u = peer.Doc.GetMap("map");
            Assert.False(u.TryGet("stuff", out _));
            Assert.False(u.TryGet("otherstuff", out _));
            Assert.Equal(0, u.Count);
        }
        net.Compare();
    }

    [Fact, PortOf("y-map.tests.js:testGetAndSetOfMapPropertyWithThreeConflicts", "M-9")]
    [Requires(Feature.Sync)]
    public void Map_ThreeConcurrentSetsOfTheSameKey_ConvergeOnOneValue()
    {
        var net = Net.Init(3);
        net.Peers[0].Map.Set("stuff", "c0");
        net.Peers[1].Map.Set("stuff", "c1");
        net.Peers[1].Map.Set("stuff", "c2");
        net.Peers[2].Map.Set("stuff", "c3");
        net.Flush();
        foreach (var peer in net.Peers) Assert.Equal("c3", peer.Doc.GetMap("map").Get("stuff").AsString());
        net.Compare();
    }

    [Fact, PortOf("y-map.tests.js:testGetAndSetAndDeleteOfMapPropertyWithThreeConflicts", "M-9", "M-4")]
    [Requires(Feature.Sync)]
    public void Map_ThreeConcurrentSetsAndDeletes_Converge()
    {
        var net = Net.Init(4);
        var (map0, map1, map2, map3) = (net.Peers[0].Map, net.Peers[1].Map, net.Peers[2].Map, net.Peers[3].Map);
        map0.Set("stuff", "c0");
        map1.Set("stuff", "c1");
        map1.Set("stuff", "c2");
        map2.Set("stuff", "c3");
        net.Flush();
        map0.Set("stuff", "deleteme");
        map1.Set("stuff", "c1");
        map2.Set("stuff", "c2");
        map3.Set("stuff", "c3");
        map3.Delete("stuff");
        net.Flush();
        foreach (var peer in net.Peers) Assert.False(peer.Doc.GetMap("map").TryGet("stuff", out _));
        net.Compare();
    }
}
