using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Traceability;
using static YSharp.Tests.Types.TypeTestExtensions;

namespace YSharp.Tests.Types;

/// <summary>Type events: YArrayEvent, YMapEvent, YTextEvent, the common event surface and deep paths (A-E*, M-E*, Y-*, V-*).</summary>
public sealed class EventTests
{
    private static Doc NewDoc(uint client = 1) => new(new DocOptions { ClientId = client });

    /// <summary>The event path as JSON, e.g. <c>["my-array",3]</c>. Computed inside the handler: events are valid only during dispatch.</summary>
    private static string PathOf(YEvent e) =>
        "[" + string.Join(",", e.GetPath().Select(s => s.Key != null ? $"\"{s.Key}\"" : s.Index.ToString())) + "]";

    private static string ListDelta(YEvent e) => ListDeltaJson(((YArrayEvent)e).Delta.ToArray());

    private static string KeysOf(YEvent e) =>
        string.Join(";", e.Keys.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k =>
            $"{k.Key}:{k.Value.Action.ToString().ToLowerInvariant()}:{(k.Value.OldValue is { } old ? EntryJson(old).Json() : "-")}"));

    // ------------------------------------------------------------------ array events

    [Fact, PortOf("y-array.tests.js:testInsertAndDeleteEventsForTypes2", "A-E1", "V-8")]
    public void TestInsertAndDeleteEventsForTypes2() => Stub.Run(() =>
    {
        var net = Net.Init(2);
        var array0 = net.Peers[0].Array;
        var events = 0;
        array0.Observe((_, _) => events++);
        array0.Insert(0, new YEntry[] { "hi", new YMap() });
        Assert.Equal(1, events); // triggered exactly once for the insertion of two elements
        array0.Delete(1);
        Assert.Equal(2, events); // exactly once for the deletion
        net.Compare();
    });

    [Fact, PortOf("y-array.tests.js:testChangeEvent", "A-E2", "A-E3", "V-4", "V-6")]
    public void TestChangeEvent_Array() => Stub.Run(() =>
    {
        var net = Net.Init(2);
        var array0 = net.Peers[0].Array;
        string? delta = null;
        bool addedFirst = false, addedLast = false, deletedFirst = false, deletedSecond = false, deletedThird = false;
        array0.Observe((e, _) =>
        {
            delta = ListDelta(e);
            var first = new ItemId(0, 0);
            addedFirst = e.Adds(first);
            addedLast = e.Adds(new ItemId(0, 2));
            deletedFirst = e.Deletes(first);
            deletedSecond = e.Deletes(new ItemId(0, 1));
            deletedThird = e.Deletes(new ItemId(0, 2));
        });
        var newArr = new YArray();
        array0.Insert(0, new YEntry[] { newArr, 4, "dtrn" });
        // JS counts the Items: the type and the one ContentAny run (added.size === 2). Items are not public; Adds/Deletes project them.
        Assert.True(addedFirst && addedLast);
        Assert.False(deletedFirst);
        Assert.Equal("""[{"insert":[[],4,"dtrn"]}]""", delta);
        delta = null;
        array0.Delete(0, 2);
        Assert.False(addedFirst);
        Assert.True(deletedFirst && deletedSecond);
        Assert.False(deletedThird);
        Assert.Equal("""[{"delete":2}]""", delta);
        delta = null;
        array0.Insert(1, new YEntry[] { 0.1 });
        Assert.Equal("""[{"retain":1},{"insert":[0.1]}]""", delta);
        net.Compare();
    });

    [Fact, PortOf("y-array.tests.js:testInsertAndDeleteEvents", "A-E1")]
    public void TestInsertAndDeleteEvents() => Stub.Run(() =>
    {
        var net = Net.Init(2);
        var array0 = net.Peers[0].Array;
        var fired = false;
        array0.Observe((_, _) => fired = true);
        array0.Insert(0, new YEntry[] { 0, 1, 2 });
        Assert.True(fired);
        fired = false;
        array0.Delete(0);
        Assert.True(fired);
        fired = false;
        array0.Delete(0, 2);
        Assert.True(fired);
        net.Compare();
    });

    [Fact, PortOf("y-array.tests.js:testInsertAndDeleteEventsForTypes", "A-E1")]
    public void TestInsertAndDeleteEventsForTypes() => Stub.Run(() =>
    {
        var net = Net.Init(2);
        var array0 = net.Peers[0].Array;
        var fired = false;
        array0.Observe((_, _) => fired = true);
        array0.Insert(0, new YEntry[] { new YArray() });
        Assert.True(fired);
        fired = false;
        array0.Delete(0);
        Assert.True(fired);
        net.Compare();
    });

    [Fact, Covers("A-E4", "Y-5")]
    public void InsertThenDeleteInOneTransaction_FiresWithAnEmptyDelta() => Stub.Run(() =>
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        var deltas = new List<string>();
        array.Observe((e, _) => deltas.Add(ListDelta(e)));
        doc.Transact(_ =>
        {
            array.Insert(0, new YEntry[] { 1, 2 });
            array.Delete(0, 2);
        });
        Assert.Equal(new[] { "[]" }, deltas);
    });

    [Fact, Covers("A-E5", "Y-1")]
    public void ArrayEvents_HaveNoKeys() => Stub.Run(() =>
    {
        var array = NewDoc().GetArray("a");
        var keys = -1;
        array.Observe((e, _) => keys = e.Keys.Count);
        array.Push(new YEntry[] { 1 });
        Assert.Equal(0, keys);
    });

    [Fact, PortOf("y-array.tests.js:testEventTargetIsSetCorrectlyOnLocal", "A-E6")]
    public void TestEventTargetIsSetCorrectlyOnLocal() => Stub.Run(() =>
    {
        var net = Net.Init(3);
        var array0 = net.Peers[0].Array;
        YType? target = null, current = null;
        array0.Observe((e, _) => { target = e.Target; current = e.CurrentTarget; });
        array0.Insert(0, new YEntry[] { "stuff" });
        Assert.Same(array0, target);
        Assert.Same(array0, current);
        net.Compare();
    });

    [Fact, PortOf("y-array.tests.js:testEventTargetIsSetCorrectlyOnRemote", "A-E6", "TX-14")]
    public void TestEventTargetIsSetCorrectlyOnRemote() => Stub.Run(() =>
    {
        var net = Net.Init(3);
        var (array0, array1) = (net.Peers[0].Array, net.Peers[1].Array);
        YType? target = null;
        var isLocal = true;
        array0.Observe((e, tx) => { target = e.Target; isLocal = tx.IsLocal; });
        array1.Insert(0, new YEntry[] { "stuff" });
        net.Flush();
        Assert.Same(array0, target);
        Assert.False(isLocal);
        net.Compare();
    });

    [Fact, PortOf("y-array.tests.js:testNestedObserverEvents", "A-E7", "TX-6")]
    public void TestNestedObserverEvents() => Stub.Run(() =>
    {
        var net = Net.Init(2);
        var array0 = net.Peers[0].Array;
        var vals = new List<int>();
        array0.Observe((_, _) =>
        {
            if (array0.Length == 1)
            {
                // inserting calls this observer again; it must run after this handler finished
                array0.Insert(1, new YEntry[] { 1 });
                vals.Add(0);
            }
            else
            {
                // called the second time an element is inserted (above)
                vals.Add(1);
            }
        });
        array0.Insert(0, new YEntry[] { 0 });
        Assert.Equal(new[] { 0, 1 }, vals);
        Assert.Equal("[0,1]", array0.Json());
        net.Compare();
    });

    [Fact, PortOf("y-array.tests.js:testNewChildDoesNotEmitEventInTransaction", "A-E8", "V-8")]
    public void TestNewChildDoesNotEmitEventInTransaction() => Stub.Run(() =>
    {
        var net = Net.Init(2);
        var array0 = net.Peers[0].Array;
        var fired = false;
        net.Users[0].Doc.Transact(_ =>
        {
            var newMap = new YMap();
            newMap.Observe((_, _) => fired = true);
            array0.Insert(0, new YEntry[] { newMap });
            newMap.Set("tst", 42);
        });
        Assert.False(fired, "Event does not trigger");
    });

    [Fact, Covers("A-E9", "TX-9")]
    public void DeletedTypes_FireNoEvents_AndDeepObserversFilterTheirChildren() => Stub.Run(() =>
    {
        var doc = NewDoc();
        var root = doc.GetMap("m");
        var child = root.Set("child", new YArray());
        child.Push(new YEntry[] { 1 });
        var childEvents = 0;
        var rootEvents = 0;
        var deepTargets = new List<string>();
        child.Observe((_, _) => childEvents++);
        root.Observe((_, _) => rootEvents++);
        root.ObserveDeep((events, _) => deepTargets.AddRange(events.Select(e => e.Target == root ? "root" : "child")));
        doc.Transact(_ =>
        {
            child.Push(new YEntry[] { 2 });
            root.Delete("child");
        });
        Assert.Equal(0, childEvents);
        Assert.Equal(1, rootEvents);
        Assert.Equal(new[] { "root" }, deepTargets);
    });

    [Fact, Covers("A-E10", "TX-12", "TX-13")]
    public void ObserveAndUnobserve_RegisterHandlersByIdentityAndRemoveAllCopies() => Stub.Run(() =>
    {
        var array = NewDoc().GetArray("a");
        var count = 0;
        YEventHandler<YEvent> handler = (_, _) => count++;
        array.Observe(handler);
        array.Observe(handler);
        array.Push(new YEntry[] { 1 });
        Assert.Equal(2, count);
        array.Unobserve(handler);
        array.Push(new YEntry[] { 2 });
        Assert.Equal(2, count);
        array.Unobserve(handler); // a handler that is not registered: no throw

        var deepCount = 0;
        YDeepEventHandler deep = (_, _) => deepCount++;
        array.ObserveDeep(deep);
        array.Push(new YEntry[] { 3 });
        array.UnobserveDeep(deep);
        array.Push(new YEntry[] { 4 });
        Assert.Equal(1, deepCount);

        var subscribed = 0;
        var sub = array.Observe((_, _) => subscribed++);
        array.Push(new YEntry[] { 5 });
        sub.Dispose();
        array.Push(new YEntry[] { 6 });
        Assert.Equal(1, subscribed);
    });

    // ------------------------------------------------------------------ list deltas and paths

    [Fact, Covers("V-6")]
    public void ListDelta_MergesRuns_AndDropsTheTrailingRetain() => Stub.Run(() =>
    {
        string Run(Action<YArray, Doc> body)
        {
            var doc = NewDoc();
            var array = doc.GetArray("a");
            array.Push(new YEntry[] { 1, 2, 3, 4, 5 });
            var delta = "none";
            array.Observe((e, _) => delta = ListDelta(e));
            body(array, doc);
            return delta;
        }
        Assert.Equal("""[{"insert":["x"]}]""", Run((a, _) => a.Insert(0, new YEntry[] { "x" })));
        Assert.Equal("""[{"retain":2},{"insert":["x","y"]}]""", Run((a, _) => a.Insert(2, new YEntry[] { "x", "y" })));
        Assert.Equal("""[{"retain":5},{"insert":["x"]}]""", Run((a, _) => a.Insert(5, new YEntry[] { "x" })));
        Assert.Equal("""[{"delete":2}]""", Run((a, _) => a.Delete(0, 2)));
        Assert.Equal("""[{"retain":1},{"delete":2}]""", Run((a, _) => a.Delete(1, 2)));
        Assert.Equal("""[{"retain":3},{"delete":2}]""", Run((a, _) => a.Delete(3, 2)));
        // a replaced run: items are visited in list order, so the insert comes before the deleted run
        Assert.Equal("""[{"retain":1},{"insert":["x"]},{"delete":2}]""", Run((a, d) => d.Transact(_ =>
        {
            a.Delete(1, 2);
            a.Insert(1, new YEntry[] { "x" });
        })));
        Assert.Equal("""[{"insert":["x"]},{"retain":3},{"delete":1}]""", Run((a, d) => d.Transact(_ =>
        {
            a.Insert(0, new YEntry[] { "x" });
            a.Delete(4, 1);
        })));
    });

    [Fact, Covers("V-7")]
    public void DeletedThenReinsertedValues_CountSeparately_NoUpdateForLists() => Stub.Run(() =>
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        array.Push(new YEntry[] { 1, 2, 3 });
        var delta = "";
        array.Observe((e, _) => delta = ListDelta(e));
        doc.Transact(_ =>
        {
            array.Delete(0, 1);
            array.Insert(0, new YEntry[] { 1 });
        });
        Assert.Equal("""[{"insert":[1]},{"delete":1}]""", delta);
    });

    [Fact, PortOf("y-array.tests.js:testObservedeepIndexes", "V-2", "TX-19")]
    public void TestObservedeepIndexes() => Stub.Run(() =>
    {
        var doc = NewDoc();
        var map = doc.GetMap();
        // create a field with the array as value
        map.Set("my-array", new YArray());
        // fill the array with some strings and our map
        map.ArrayOf("my-array").Push(new YEntry[] { "a", "b", "c", new YMap() });
        var eventPath = "";
        map.ObserveDeep((events, _) => eventPath = PathOf(events[0]));
        // set a value on the map inside of our array
        map.ArrayOf("my-array").MapAt(3).Set("hello", "world");
        Assert.Equal("""["my-array",3]""", eventPath);
    });

    [Fact, PortOf("y-array.tests.js:testObserveDeepEventOrder", "TX-10", "TX-2")]
    public void TestObserveDeepEventOrder() => Stub.Run(() =>
    {
        var net = Net.Init(2);
        var array0 = net.Peers[0].Array;
        var lengths = new List<int>();
        array0.ObserveDeep((events, _) =>
        {
            lengths.Clear();
            lengths.AddRange(events.Select(e => e.GetPath().Count));
        });
        array0.Insert(0, new YEntry[] { new YMap() });
        net.Users[0].Doc.Transact(_ =>
        {
            array0.MapAt(0).Set("a", "a");
            array0.Insert(0, new YEntry[] { 0 });
        });
        Assert.Equal(2, lengths.Count);
        for (var i = 1; i < lengths.Count; i++) Assert.True(lengths[i - 1] <= lengths[i], "path size increases, fire top-level events first");
    });

    // ------------------------------------------------------------------ map events

    [Fact, PortOf("y-map.tests.js:testThrowsAddAndUpdateAndDeleteEvents", "M-E1", "V-1")]
    public void TestThrowsAddAndUpdateAndDeleteEvents() => Stub.Run(() =>
    {
        var net = Net.Init(2);
        var map0 = net.Peers[0].Map;
        var seen = new List<(bool SameTarget, string Keys)>();
        map0.Observe((e, _) => seen.Add((ReferenceEquals(e.Target, map0), string.Join(",", ((YMapEvent)e).KeysChanged.OrderBy(k => k, StringComparer.Ordinal)))));
        map0.Set("stuff", 4);
        // update, oldValue is in contents
        map0.Set("stuff", new YArray());
        // update, oldValue is in opContents
        map0.Set("stuff", 5);
        // delete
        map0.Delete("stuff");
        Assert.Equal(4, seen.Count);
        Assert.All(seen, s => Assert.True(s.SameTarget));
        Assert.All(seen, s => Assert.Equal("stuff", s.Keys));
        net.Compare();
    });

    [Fact, PortOf("y-map.tests.js:testThrowsDeleteEventsOnClear", "M-E1", "M-5")]
    public void TestThrowsDeleteEventsOnClear() => Stub.Run(() =>
    {
        var net = Net.Init(2);
        var map0 = net.Peers[0].Map;
        string keys = "";
        YType? target = null;
        var events = 0;
        map0.Observe((e, _) =>
        {
            events++;
            keys = string.Join(",", ((YMapEvent)e).KeysChanged.OrderBy(k => k, StringComparer.Ordinal));
            target = e.Target;
        });
        map0.Set("stuff", 4);
        map0.Set("otherstuff", new YArray());
        events = 0;
        map0.Clear();
        Assert.Equal(1, events); // clear is one transaction, one event
        Assert.Equal("otherstuff,stuff", keys);
        Assert.Same(map0, target);
        net.Compare();
    });

    [Fact, PortOf("y-map.tests.js:testChangeEvent", "M-E1", "M-E2", "V-5")]
    public void TestChangeEvent_Map() => Stub.Run(() =>
    {
        var net = Net.Init(2);
        var map0 = net.Peers[0].Map;
        string keys = "", keysChanged = "";
        map0.Observe((e, _) =>
        {
            keys = KeysOf(e);
            keysChanged = string.Join(",", ((YMapEvent)e).KeysChanged.OrderBy(k => k, StringComparer.Ordinal));
        });
        map0.Set("a", 1);
        Assert.Equal("a:add:-", keys);
        map0.Set("a", 2);
        Assert.Equal("a:update:1", keys);
        net.Users[0].Doc.Transact(_ =>
        {
            map0.Set("a", 3);
            map0.Set("a", 4);
        });
        Assert.Equal("a:update:2", keys);
        net.Users[0].Doc.Transact(_ =>
        {
            map0.Set("b", 1);
            map0.Set("b", 2);
        });
        Assert.Equal("b:add:-", keys);
        net.Users[0].Doc.Transact(_ =>
        {
            map0.Set("c", 1);
            map0.Delete("c");
        });
        // keysChanged contains 'c' although the net effect is nothing; the key changes omit it
        Assert.Equal("c", keysChanged);
        Assert.Equal("", keys);
        net.Users[0].Doc.Transact(_ =>
        {
            map0.Set("d", 1);
            map0.Set("d", 2);
        });
        Assert.Equal("d:add:-", keys);
        net.Compare();
    });

    [Fact, Covers("M-E2", "V-5", "M-E3")]
    public void KeyChanges_CarryTheValueBeforeTheTransaction() => Stub.Run(() =>
    {
        var doc = NewDoc();
        var map = doc.GetMap("m");
        map.Set("a", 1);
        var keys = "";
        map.Observe((e, _) => keys = KeysOf(e));
        doc.Transact(_ =>
        {
            map.Delete("a");
            map.Set("a", 5);
        });
        Assert.Equal("a:update:1", keys); // delete then set: update with the value before the transaction
        doc.Transact(_ =>
        {
            map.Set("a", 6);
            map.Delete("a");
        });
        Assert.Equal("a:delete:5", keys); // update then delete: delete with the value before the transaction
        map.Set("sub", new YMap());
        map.Set("sub", "str");
        // the old value of a replaced nested type is the type itself (here already deleted)
        Assert.Equal("sub:update:{}", keys);
        var oldTypeSeen = (YType?)null;
        map.Set("t", new YMap());
        map.Observe((e, _) => oldTypeSeen = e.Keys["t"].OldValue?.Type);
        map.Delete("t");
        Assert.NotNull(oldTypeSeen);
    });

    [Fact, Covers("M-E4")]
    public void MapEvents_HaveAnEmptyListDelta() => Stub.Run(() =>
    {
        // YMapEvent has no Delta: the sketch exposes list deltas only on YArrayEvent, YTextEvent and YXmlEvent, which is the JS behaviour
        // (the JS delta of a map event is []). Added and deleted item sets are not public either (A-E3).
        var map = NewDoc().GetMap("m");
        var changed = -1;
        map.Observe((e, _) => changed = ((YMapEvent)e).KeysChanged.Count);
        map.Set("a", 1);
        Assert.Equal(1, changed);
        Assert.False(typeof(YMapEvent).GetProperty("Delta") != null);
    });

    [Fact, PortOf("y-map.tests.js:testMapEventError", "M-E5", "TX-17")]
    public void TestMapEventError() => Stub.Run(() =>
    {
        // JS: the observer is registered but never fires, so event is null and the two accesses trivially fail. The intended
        // behaviour (yjs#539) is that computing changes after the handler fired is an error; this port makes the event really fire.
        var doc = NewDoc();
        var map = doc.GetMap();
        YEvent? stored = null;
        var computedInside = "";
        map.Observe((e, _) =>
        {
            stored = e;
            computedInside = KeysOf(e); // computing during the callback is fine
            Assert.True(e.IsValid);
        });
        map.Set("k", 1);
        Assert.Equal("k:add:-", computedInside);
        Assert.NotNull(stored);
        Assert.False(stored!.IsValid);
        Assert.ThrowsAny<InvalidOperationException>(() => stored.Keys.Count);
        Assert.ThrowsAny<InvalidOperationException>(() => stored.Keys.Count);
    });

    [Fact, PortOf("y-map.tests.js:testYmapEventHasCorrectValueWhenSettingAPrimitive", "M-E2")]
    public void TestYmapEventHasCorrectValueWhenSettingAPrimitive() => Stub.Run(() =>
    {
        var net = Net.Init(3);
        var map0 = net.Peers[0].Map;
        var matches = false;
        map0.Observe((e, _) =>
        {
            // JS compares the legacy event.value/event.name (undefined on both sides in v13); the reading that matters is
            // that the changed key resolves to the set value on the target
            var key = Assert.Single(((YMapEvent)e).KeysChanged);
            matches = ((YMap)e.Target).Get(key).AsNumber() == 2;
        });
        map0.Set("stuff", 2);
        Assert.True(matches);
        net.Compare();
    });

    [Fact, PortOf("y-map.tests.js:testYmapEventHasCorrectValueWhenSettingAPrimitiveFromOtherUser", "M-E7", "M-9")]
    public void TestYmapEventHasCorrectValueWhenSettingAPrimitiveFromOtherUser() => Stub.Run(() =>
    {
        var net = Net.Init(3);
        var (map0, map1) = (net.Peers[0].Map, net.Peers[1].Map);
        var matches = false;
        YType? target = null;
        map0.Observe((e, _) =>
        {
            var key = Assert.Single(((YMapEvent)e).KeysChanged);
            matches = ((YMap)e.Target).Get(key).AsNumber() == 2;
            target = e.Target;
        });
        map1.Set("stuff", 2);
        net.Flush();
        Assert.True(matches);
        Assert.Same(map0, target); // the event of a remote update carries the local type
        net.Compare();
    });

    // ------------------------------------------------------------------ deep observers

    [Fact, PortOf("y-map.tests.js:testObserveDeepProperties", "TX-19", "V-2")]
    public void TestObserveDeepProperties() => Stub.Run(() =>
    {
        var net = Net.Init(4);
        var (map1, map2, map3) = (net.Peers[1].Map, net.Peers[2].Map, net.Peers[3].Map);
        var map1Inner = map1.Set("map", new YMap());
        var calls = 0;
        map1.ObserveDeep((events, _) =>
        {
            foreach (var e in events)
            {
                calls++;
                Assert.Contains("deepmap", ((YMapEvent)e).KeysChanged);
                Assert.Equal("""["map"]""", PathOf(e));
            }
        });
        net.Flush();
        var map3Inner = map3.MapOf("map");
        map3Inner.Set("deepmap", new YMap());
        net.Flush();
        var map2Inner = map2.MapOf("map");
        map2Inner.Set("deepmap", new YMap());
        net.Flush();
        Assert.True(calls > 0);
        // JS compares the item ids of the three deepmap instances; they must be the same item on every replica. The items are not
        // public, so the replicas must agree on the structure and the doc dump instead.
        Assert.Equal(map1Inner.Json(), map2Inner.Json());
        Assert.Equal(map1Inner.Json(), map3Inner.Json());
        net.Compare();
    });

    [Fact, PortOf("y-map.tests.js:testObserversUsingObservedeep", "TX-19", "V-2")]
    public void TestObserversUsingObservedeep() => Stub.Run(() =>
    {
        var net = Net.Init(2);
        var map0 = net.Peers[0].Map;
        var paths = new List<string>();
        var calls = 0;
        map0.ObserveDeep((events, _) =>
        {
            foreach (var e in events) paths.Add(PathOf(e));
            calls++;
        });
        map0.Set("map", new YMap());
        map0.MapOf("map").Set("array", new YArray());
        map0.MapOf("map").ArrayOf("array").Insert(0, new YEntry[] { "content" });
        Assert.Equal(3, calls);
        Assert.Equal(new[] { "[]", """["map"]""", """["map","array"]""" }, paths);
        net.Compare();
    });

    [Fact, PortOf("y-map.tests.js:testPathsOfSiblingEvents", "V-2", "TX-19")]
    public void TestPathsOfSiblingEvents() => Stub.Run(() =>
    {
        var net = Net.Init(2);
        var map0 = net.Peers[0].Map;
        var doc = net.Users[0].Doc;
        var paths = new List<string>();
        var calls = 0;
        map0.Set("map", new YMap());
        map0.MapOf("map").Set("text1", new YText("initial"));
        map0.ObserveDeep((events, _) =>
        {
            foreach (var e in events) paths.Add(PathOf(e));
            calls++;
        });
        doc.Transact(_ =>
        {
            map0.MapOf("map").TextOf("text1").Insert(0, "post-");
            map0.MapOf("map").Set("text2", new YText("new"));
        });
        Assert.Equal(1, calls);
        Assert.Equal(new[] { """["map"]""", """["map","text1"]""" }, paths);
        net.Compare();
    });

    [Fact, PortOf("y-map.tests.js:testDeepEventPathsWithMultipleDeepObservers", "V-3", "TX-1", "TX-2", "Q23")]
    public void TestDeepEventPathsWithMultipleDeepObservers() => Stub.Run(() =>
    {
        var doc = NewDoc();
        var a = doc.GetMap("a");
        var b = a.Set("b", new YMap());
        var c = a.Set("c", new YMap());
        // order in which observers and the afterTransaction event are called
        var calls = new List<string>();
        var observedA = new List<(string Path, bool CurrentIsA)>();
        a.ObserveDeep((events, _) =>
        {
            calls.Add("observeDeep a");
            foreach (var e in events) observedA.Add((PathOf(e), ReferenceEquals(e.CurrentTarget, a)));
        });
        var observedC = new List<(string Path, bool CurrentIsC)>();
        // a second deep observer at a deeper nesting level must not affect the paths reported to the observer on a
        c.ObserveDeep((events, _) =>
        {
            calls.Add("observeDeep c");
            foreach (var e in events) observedC.Add((PathOf(e), ReferenceEquals(e.CurrentTarget, c)));
        });
        c.Observe((_, _) => calls.Add("observe c"));
        doc.AfterTransaction += (_, _) => calls.Add("afterTransaction");
        doc.Transact(_ =>
        {
            b.Set("foo", "bar");
            c.Set("foo", "bar");
        });
        // afterTransaction must be fired after all (deep) observers were called
        Assert.Equal(new[] { "observe c", "observeDeep a", "observeDeep c", "afterTransaction" }, calls);
        Assert.Equal(2, observedA.Count);
        Assert.Equal("""["b"]""", observedA[0].Path);
        Assert.True(observedA[0].CurrentIsA);
        Assert.Equal("""["c"]""", observedA[1].Path);
        Assert.True(observedA[1].CurrentIsA);
        Assert.Single(observedC);
        Assert.Equal("[]", observedC[0].Path);
        Assert.True(observedC[0].CurrentIsC);
    });

    [Fact, PortOf("y-map.tests.js:testYmapEventExceptionsShouldCompleteTransaction", "M-E6", "TX-7", "Q24"), Diverges("M-E6", "0005"), Diverges("TX-7", "0005"), Diverges("Q24", "0005")]
    public void TestYmapEventExceptionsShouldCompleteTransaction() => Stub.Run(() =>
    {
        // JS: both handlers run, the update event and the data commit happen, and the exception (the last one, Q24) is rethrown from
        // map.set. y-sharp (ADR 0005): the same guarantees, but handler exceptions are reported through ObserverFaulted and never unwind
        // through the mutating call.
        var doc = NewDoc();
        var map = doc.GetMap("map");
        var updateCalled = false;
        var throwingObserverCalled = false;
        var throwingDeepObserverCalled = false;
        var faults = new List<Exception>();
        doc.Update += (_, _, _, _) => updateCalled = true;
        doc.ObserverFaulted += (fault, _) => faults.Add(fault);
        map.Observe((_, _) =>
        {
            throwingObserverCalled = true;
            throw new InvalidOperationException("Failure");
        });
        map.ObserveDeep((_, _) =>
        {
            throwingDeepObserverCalled = true;
            throw new InvalidOperationException("Failure");
        });

        map.Set("y", "2"); // does not throw

        Assert.True(updateCalled);
        Assert.True(throwingObserverCalled);
        Assert.True(throwingDeepObserverCalled);
        Assert.Equal(2, faults.Count);
        Assert.All(faults, f => Assert.Equal("Failure", f.Message));

        // check if it works again
        updateCalled = false;
        throwingObserverCalled = false;
        throwingDeepObserverCalled = false;
        map.Set("z", "3");
        Assert.True(updateCalled);
        Assert.True(throwingObserverCalled);
        Assert.True(throwingDeepObserverCalled);
        Assert.Equal("3", map.Get("z").AsString());
        Assert.Equal(4, faults.Count);
    });

    // ------------------------------------------------------------------ common event surface

    [Fact, Covers("V-1", "V-2")]
    public void EventFields_AreSetDuringDispatch_AndPathsMixKeysAndIndices() => Stub.Run(() =>
    {
        var doc = NewDoc();
        var root = doc.GetMap("m");
        var list = root.Set("list", new YArray());
        list.Push(new YEntry[] { "x", new YMap() });
        var inner = list.MapAt(1);
        string path = "", cur = "";
        Transaction? tx = null;
        var deep = false;
        root.ObserveDeep((events, t) =>
        {
            var e = events.Single();
            path = PathOf(e);
            deep = ReferenceEquals(e.Target, inner) && ReferenceEquals(e.CurrentTarget, root);
            tx = t;
        });
        inner.Observe((e, t) =>
        {
            cur = PathOf(e);
            Assert.Same(inner, e.Target);
            Assert.Same(inner, e.CurrentTarget);
            Assert.Same(t, e.Transaction);
        });
        inner.Set("k", 1);
        Assert.Equal("""["list",1]""", path);
        Assert.Equal("[]", cur);
        Assert.True(deep);
        Assert.NotNull(tx);
        // the path reaches the target by walking keys and indices
        Assert.Same(inner, root.ArrayOf("list").MapAt(1));
    });

    [Fact, Covers("V-8", "V-9", "TX-8")]
    public void Transaction_ListsChangedTypesAndTheirAncestors() => Stub.Run(() =>
    {
        var doc = NewDoc();
        var root = doc.GetMap("m");
        var child = root.Set("child", new YMap());
        var leaf = child.Set("leaf", new YArray());
        var other = doc.GetArray("other");
        IReadOnlyCollection<YType>? changed = null, parents = null;
        var before = 0L;
        var after = 0L;
        doc.AfterTransaction += (tx, _) =>
        {
            changed = tx.ChangedTypes.ToArray();
            parents = tx.ChangedParentTypes.ToArray();
            before = tx.BeforeState[1];
            after = tx.AfterState[1];
        };
        doc.Transact(_ =>
        {
            leaf.Push(new YEntry[] { 1, 2, 3 });
            other.Push(new YEntry[] { "x" });
        });
        Assert.NotNull(changed);
        Assert.Contains(leaf, changed!);
        Assert.Contains(other, changed!);
        Assert.DoesNotContain(child, changed!);
        // a type that only had its children changed is a changed parent, not a changed type (deep observers only)
        Assert.Contains(child, parents!);
        Assert.Contains(root, parents!);
        Assert.Contains(leaf, parents!);
        Assert.Equal(4, after - before);
    });

    [Fact, Covers("Y-1", "Y-3", "Y-7", "X-1")]
    public void TextEvents_ReportChildListChangesAndAttributeKeys() => Stub.Run(() =>
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abc");
        var seen = new List<string>();
        text.Observe((e, _) =>
        {
            var te = (YTextEvent)e;
            seen.Add($"{te.ChildListChanged}|{string.Join(",", te.KeysChanged.OrderBy(k => k, StringComparer.Ordinal))}|{KeysOf(e)}");
        });
        text.Insert(3, "d");
        text.SetAttribute("align", "left");
        doc.Transact(_ =>
        {
            text.Delete(0, 1);
            text.SetAttribute("align", "right");
            text.RemoveAttribute("gone");
        });
        text.RemoveAttribute("align");
        Assert.Equal(
            new[]
            {
                "True||",
                "False|align|align:add:-",
                "True|align|align:update:\"left\"",
                "False|align|align:delete:\"right\"",
            },
            seen);
    });

    [Fact, Covers("Y-2")]
    public void TextEventDelta_IsQuillStyle_WithTrailingPlainRetainsDropped() => Stub.Run(() =>
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abcdef");
        string delta = "";
        text.Observe((e, _) => delta = DeltaJson(((YTextEvent)e).Delta.ToArray()));
        text.Insert(2, "X", Obj(("bold", true)));
        Assert.Equal("""[{"retain":2},{"insert":"X","attributes":{"bold":true}}]""", delta);
        text.Delete(0, 2);
        Assert.Equal("""[{"delete":2}]""", delta);
        text.Format(1, 2, Obj(("i", true)));
        Assert.Equal("""[{"retain":1},{"retain":2,"attributes":{"i":true}}]""", delta);
        text.InsertEmbed(1, Obj(("e", 1)));
        Assert.Equal("""[{"retain":1},{"insert":{"e":1}}]""", delta);
    });

    [Fact, Covers("Y-4")]
    public void ReadingTheTextDeltaInTheHandler_DoesNotChangeTheEncodedState() => Stub.Run(() =>
    {
        // JS computes event.delta lazily and the computation may delete redundant format items. The state a replica ends in must not
        // depend on whether an observer read the delta.
        byte[] Run(bool readDelta)
        {
            var net = Net.Init(2);
            var (text0, text1) = (net.Peers[0].Text, net.Peers[1].Text);
            text0.Insert(0, "abcde");
            net.Flush();
            text0.Format(0, 3, Obj(("bold", true)));
            text1.Format(2, 2, Obj(("bold", true)));
            if (readDelta) text1.Observe((e, _) => GC.KeepAlive(((YTextEvent)e).Delta.Length));
            net.Flush();
            var state = text1.Doc!.EncodeStateAsUpdate();
            Assert.Equal(text0.DeltaJson(), text1.DeltaJson());
            return state;
        }
        Assert.Equal(Run(false), Run(true));
    });
}
