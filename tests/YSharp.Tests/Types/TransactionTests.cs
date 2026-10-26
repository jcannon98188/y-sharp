using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Traceability;
using static YSharp.Tests.Types.TypeTestExtensions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Types;

/// <summary>Transaction lifecycle and observer ordering (TX-*, Q23), after docs/research/types.md section 6.</summary>
[Requires(Feature.Events)]
public sealed class TransactionTests
{
    private static Doc NewDoc(ulong client = 1) => new(new DocOptions { ClientId = client });

    /// <summary>Subscribes a logger to every doc-level transaction event.</summary>
    private static List<string> LogDocEvents(Doc doc)
    {
        var log = new List<string>();
        doc.BeforeAllTransactions += (_, _) => log.Add("beforeAllTransactions");
        doc.BeforeTransaction += (_, _) => log.Add("beforeTransaction");
        doc.BeforeObserverCalls += (_, _) => log.Add("beforeObserverCalls");
        doc.AfterTransaction += (_, _) => log.Add("afterTransaction");
        doc.AfterTransactionCleanup += (_, _) => log.Add("afterTransactionCleanup");
        doc.Update += (_, _, _, _) => log.Add("update");
        doc.UpdateV2 += (_, _, _, _) => log.Add("updateV2");
        doc.AfterAllTransactions += (_, _) => log.Add("afterAllTransactions");
        return log;
    }

    [Fact, Covers("TX-1", "Q23")]
    [Requires(Feature.Text, Feature.V2)]
    public void EventOrder_SingleTransaction_IsTheDocumentedOne()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        var log = LogDocEvents(doc);
        text.Observe((_, _) => log.Add("observe"));
        text.Doc!.GetText("t").ObserveDeep((_, _) => log.Add("observeDeep"));
        doc.Transact(_ =>
        {
            log.Add("user");
            text.Insert(0, "a");
        });
        Assert.Equal(
            new[]
            {
                "beforeAllTransactions", "beforeTransaction", "user", "beforeObserverCalls", "observe", "observeDeep",
                "afterTransaction", "afterTransactionCleanup", "update", "updateV2", "afterAllTransactions",
            },
            log);
    }

    [Fact, Covers("TX-2", "Q23")]
    [Requires(Feature.Map)]
    public void ObserveHandlers_AllChangedTypes_RunBeforeAnyDeepHandler()
    {
        var doc = NewDoc();
        var a = doc.GetMap("a");
        var c = a.Set("c", new YMap());
        var log = new List<string>();
        c.Observe((_, _) => log.Add("observe c"));
        c.ObserveDeep((_, _) => log.Add("deep c"));
        a.Observe((_, _) => log.Add("observe a"));
        a.ObserveDeep((_, _) => log.Add("deep a"));
        doc.Transact(_ =>
        {
            c.Set("x", 1);
            a.Set("y", 1);
        });
        Assert.Equal(new[] { "observe c", "observe a", "deep c", "deep a" }, log);
    }

    [Fact, Covers("TX-3")]
    public void AnEmptyTransaction_EmitsTheLifecycleEvents_ButNoUpdate()
    {
        var doc = NewDoc();
        var log = LogDocEvents(doc);
        doc.Transact(_ => { });
        Assert.Equal(
            new[] { "beforeAllTransactions", "beforeTransaction", "beforeObserverCalls", "afterTransaction", "afterTransactionCleanup", "afterAllTransactions" },
            log);
    }

    [Fact, Covers("TX-4")]
    [Requires(Feature.Text)]
    public void Transaction_DefaultsToANullOriginAndLocal_AndApplyUpdateIsRemote()
    {
        var doc = NewDoc(1);
        var origins = new List<(object? Origin, bool Local, bool Write)>();
        doc.AfterTransaction += (tx, _) => origins.Add((tx.Origin, tx.IsLocal, tx.IsWrite));
        doc.GetText("t").Insert(0, "a");
        doc.Transact(_ => doc.GetText("t").Insert(0, "b"), "origin");
        var other = NewDoc(2);
        other.GetText("t").Insert(0, "x");
        doc.ApplyUpdate(other.EncodeStateAsUpdate(), "remote");
        Assert.Equal(new (object?, bool, bool)[] { (null, true, true), ("origin", true, true), ("remote", false, true) }, origins);
        Assert.Equal(3, doc.Transact(_ => doc.GetText("t").Length));                      // JS transact returns the callback's value (A4)
        Assert.Equal("seen", doc.Transact(tx => (string)tx.Origin!, "seen"));
        Assert.Equal(7, doc.Transact(5, static (n, _) => n + 2));                          // the state-passing form
    }

    [Fact, PortOf("y-text.tests.js:testBasicInsertAndDelete", "TX-5")]
    [Requires(Feature.Text)]
    public void NestedTransact_JoinsTheOuterTransaction_AndTheInnerOriginIsIgnored()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        var events = 0;
        var updates = 0;
        var origins = new List<object?>();
        text.Observe((_, _) => events++);
        doc.Update += (_, _, _, _) => updates++;
        doc.AfterTransaction += (tx, _) => origins.Add(tx.Origin);
        doc.Transact(_ =>
        {
            text.Insert(0, "a");
            doc.Transact(_ => text.Insert(1, "b"), "inner");
            text.Insert(2, "c");
        }, "outer");
        Assert.Equal(1, events);
        Assert.Equal(1, updates);
        Assert.Equal(new object?[] { "outer" }, origins);
        Assert.Equal("abc", text.ToString());
    }

    [Fact, PortOf("doc.tests.js:testClientIdDuplicateChange", "TX-16")]
    [Requires(Feature.Array)]
    public void RemoteUpdate_UsingOurClientId_ChangesTheClientId()
    {
        var doc1 = NewDoc();
        doc1.ClientId = 0;
        var doc2 = NewDoc();
        doc2.ClientId = 0;
        Assert.Equal(doc1.ClientId, doc2.ClientId);
        doc1.GetArray("a").Insert(0, new YEntry[] { 1, 2 });
        doc2.ApplyUpdate(doc1.EncodeStateAsUpdate());
        Assert.NotEqual(doc1.ClientId, doc2.ClientId);
    }

    [Fact, Covers("TX-6")]
    [Requires(Feature.Array)]
    public void MutationsInsideObservers_RunAsFollowUpTransactions_NeverNested()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        var log = new List<string>();
        var running = 0;
        var maxDepth = 0;
        var afterAll = 0;
        var round = new List<object?>();
        doc.AfterTransaction += (tx, _) => log.Add($"after:{tx.Origin}");
        doc.AfterAllTransactions += (txs, _) =>
        {
            afterAll++;
            round.AddRange(txs.Select(tx => tx.Origin));   // the list is pooled: copy what is needed during dispatch (A5)
        };
        array.Observe((_, tx) =>
        {
            running++;
            maxDepth = Math.Max(maxDepth, running);
            log.Add($"observe:{tx.Origin}:{array.Length}");
            if (array.Length < 3) doc.Transact(_ => array.Push(new YEntry[] { array.Length }), $"follow{array.Length}");
            running--;
        });
        doc.Transact(_ => array.Push(new YEntry[] { 0 }), "first");
        Assert.Equal(1, maxDepth);
        Assert.Equal(3, array.Length);
        Assert.Equal(1, afterAll); // once for the whole cycle
        Assert.Equal(new object?[] { "first", "follow1", "follow2" }, round);   // with every transaction of the cycle, in order
        Assert.Equal(
            new[] { "observe:first:1", "after:first", "observe:follow1:2", "after:follow1", "observe:follow2:3", "after:follow2" },
            log);
    }

    [Fact, Covers("TX-10", "TX-18")]
    [Requires(Feature.Array, Feature.Map)]
    public void Observe_OncePerTransaction_AndEveryAncestorGetsTheDeepEvent()
    {
        var doc = NewDoc();
        var root = doc.GetMap("root");
        var list = root.Set("list", new YArray());
        var item = list.Insert(new YMap());
        var counts = new Dictionary<string, int>();
        void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        item.Observe((_, _) => Count("item"));
        list.Observe((_, _) => Count("list"));
        root.Observe((_, _) => Count("root"));
        var deep = new List<int>();
        root.ObserveDeep((events, _) => deep.Add(events.Count));
        doc.Transact(_ =>
        {
            item.Set("a", 1);
            item.Set("b", 2);
            item.Set("a", 3);
            list.Push(new YEntry[] { 9 });
            list.Push(new YEntry[] { 10 });
        });
        Assert.Equal(1, counts["item"]);
        Assert.Equal(1, counts["list"]);
        Assert.False(counts.ContainsKey("root")); // the root map itself did not change
        // the item's event and the list's event both reach the root's deep observer
        Assert.Equal(new[] { 2 }, deep);
    }

    [Fact, Covers("Q25", "TX-1")]
    [Requires(Feature.Map)]
    public void DeepEvents_OfEqualPathLength_KeepTheOrderTheTypesChanged()
    {
        // JS Array.prototype.sort is stable (ES2019); an unstable sort would reorder ties once there are enough of them
        var doc = NewDoc();
        var root = doc.GetMap("root");
        const int n = 40;
        var children = Enumerable.Range(0, n).Select(i => root.Set($"c{i:D2}", new YMap())).ToArray();
        var seen = new List<string>();
        root.ObserveDeep((events, _) => seen.AddRange(events.Select(e => string.Join("", e.GetPath().Select(p => p.Key)))));
        doc.Transact(_ =>
        {
            for (var i = n - 1; i >= 0; i -= 1) children[i].Set("k", i);
            root.Set("own", 1);
        });
        var expected = new List<string> { "" };
        expected.AddRange(Enumerable.Range(0, n).Reverse().Select(i => $"c{i:D2}"));
        // the root's own event has the shortest path and comes first; the 40 equal-depth events keep their change order
        Assert.Equal(expected.Count, seen.Count);
        Assert.Equal(expected, seen);
    }

    [Fact, Covers("TX-11")]
    [Requires(Feature.Array, Feature.Map)]
    public void DeepObserver_NoObserveHandlerOnTheSameType_Works()
    {
        var doc = NewDoc();
        var map = doc.GetMap("m");
        var child = map.Set("child", new YArray());
        var calls = 0;
        map.ObserveDeep((events, _) => calls += events.Count);
        child.Push(new YEntry[] { 1 });
        Assert.Equal(1, calls);
    }

    [Fact, Covers("TX-12")]
    [Requires(Feature.Array)]
    public void HandlerListChanges_DuringDispatch_TakeEffectFromTheNextTrigger()
    {
        // JS probe (yjs 13.6.33): a handler removed while dispatching still runs in that dispatch, and one added while dispatching is
        // first called by the next trigger. ADR 0006 CC-13 adopts the same rule.
        var array = NewDoc().GetArray("a");
        var log = new List<string>();
        YEventHandler<YEvent> h3 = (_, _) => log.Add("h3");
        YEventHandler<YEvent> h2 = (_, _) => log.Add("h2");
        YEventHandler<YEvent> h1 = (_, _) =>
        {
            log.Add("h1");
            array.Unobserve(h2);
            array.Observe(h3);
        };
        array.Observe(h1);
        array.Observe(h2);
        array.Push(new YEntry[] { 1 });
        log.Add("--");
        array.Push(new YEntry[] { 2 });
        Assert.Equal(new[] { "h1", "h2", "--", "h1", "h3" }, log);
    }

    [Fact, Covers("TX-14")]
    [Requires(Feature.Text)]
    [Requires(Feature.Sync)]
    public void RemoteUpdates_RunAsNonLocalTransactions_WithTheGivenOrigin()
    {
        var net = Net.Init(2);
        var (text0, text1) = (net.Peers[0].Text, net.Peers[1].Text);
        text0.Insert(0, "hello");
        net.Flush();
        var seen = new List<(bool Local, object? Origin, bool SameTarget, string Delta)>();
        text1.Observe((e, tx) => seen.Add((tx.IsLocal, tx.Origin, ReferenceEquals(e.Target, text1), DeltaJson(((YTextEvent)e).Delta.ToArray()))));
        var source = NewDoc(77);
        source.ApplyUpdate(text0.Doc!.EncodeStateAsUpdate());
        source.GetText("text").Insert(5, "!");
        text1.Doc!.ApplyUpdate(source.EncodeStateAsUpdate(), "from-test");
        var entry = Assert.Single(seen);
        Assert.False(entry.Local);
        Assert.Equal("from-test", entry.Origin);
        Assert.True(entry.SameTarget);
        // the delta is relative to the local state before the apply
        Assert.Equal("""[{"retain":5},{"insert":"!"}]""", entry.Delta);
    }

    [Fact, Covers("TX-15")]
    [Requires(Feature.Text, Feature.Map, Feature.V2)]
    public void UpdateEvents_CarryTheBytesOriginAndTransaction_AndFireOnlyForContentChanges()
    {
        var doc = NewDoc(5);
        var v1 = new List<(byte[] Bytes, object? Origin, bool SameDoc)>();
        var v2 = 0;
        var token = new object();
        doc.Update += (u, origin, d, tx) => v1.Add((u.ToArray(), origin, ReferenceEquals(d, doc) && ReferenceEquals(tx.Doc, doc)));
        doc.UpdateV2 += (_, _, _, _) => v2++;
        doc.Transact(_ => { }, token);
        doc.GetMap("m").Delete("missing");
        Assert.Empty(v1);
        doc.Transact(_ => doc.GetText("t").Insert(0, "a"), token);
        var (bytes, origin, sameDoc) = Assert.Single(v1);
        Assert.Same(token, origin);
        Assert.True(sameDoc);
        Assert.Equal(1, v2);
        // applying the event's bytes to a fresh replica reproduces the change
        var replica = NewDoc(6);
        replica.ApplyUpdate(bytes);
        Assert.Equal("a", replica.GetText("t").ToString());
        // a handler that is removed stops receiving updates
        var calls = 0;
        UpdateHandler counter = (_, _, _, _) => calls++;
        doc.Update += counter;
        doc.GetText("t").Insert(1, "b");
        doc.Update -= counter;
        doc.GetText("t").Insert(2, "c");
        Assert.Equal(1, calls);
    }

    [Fact, Covers("TX-17")]
    [Requires(Feature.Array)]
    public void EventData_AfterOutermostTransactionDispatched_IsInvalid()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        YEvent? stored = null;
        Transaction? storedTx = null;
        array.Observe((e, tx) =>
        {
            stored = e;
            storedTx = tx;
            Assert.True(e.IsValid);
            Assert.True(tx.IsValid);
        });
        array.Push(new YEntry[] { 1 });
        Assert.False(stored!.IsValid);
        Assert.False(storedTx!.IsValid);
    }

    [Fact, Covers("TX-13")]
    [Requires(Feature.Map)]
    public void UnobserveDeep_AfterObserveDeep_IsSymmetricalToUnobserve()
    {
        var map = NewDoc().GetMap("m");
        var calls = 0;
        YDeepEventHandler handler = (_, _) => calls++;
        map.ObserveDeep(handler);
        map.ObserveDeep(handler);
        map.Set("a", 1);
        Assert.Equal(2, calls);
        map.UnobserveDeep(handler);
        map.Set("a", 2);
        Assert.Equal(2, calls);
        map.UnobserveDeep(handler);
    }
}
