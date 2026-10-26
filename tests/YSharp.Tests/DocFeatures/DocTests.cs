using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.DocFeatures;

/// <summary>
/// Doc behaviour (DOC-01..DOC-18, docs/research/doc-features.md section 1) and the ports of the upstream <c>doc.tests.js</c> tests that
/// exercise it. Expected bytes and event orders were produced by running the same operations on the pinned Yjs.
/// </summary>
[Requires(Feature.DocLifecycle)]
public sealed class DocTests
{
    private static string Json(YValue v)
    {
        var w = new ArrayBufferWriter<byte>();
        Assert.True(v.TryWriteJson(w).Ok);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    // ------------------------------------------------------------------ DOC-01, DOC-02

    [Fact]
    [Covers("DOC-01")]
    [Requires(Feature.Subdocs)]
    public void Constructor_Defaults_AreGcOnAutoLoadOffShouldLoadOnAndEmptyCollections()
    {
        var doc = new Doc();
        Assert.True(doc.Gc);
        Assert.False(doc.IsLoaded);
        Assert.False(doc.IsSynced);
        Assert.False(doc.IsDestroyed);
        Assert.Empty(doc.Subdocs);
        Assert.Empty(doc.Share);
        Assert.Null(doc.ParentDoc);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", doc.Guid);   // uuid v4
        Assert.NotEqual(new Doc().Guid, doc.Guid);
        Assert.True(doc.ShouldLoad);
        Assert.False(doc.AutoLoad);
        Assert.Null(doc.CollectionId);
        Assert.True(doc.Meta.Kind is YValueKind.Undefined or YValueKind.Null);
    }

    [Fact]
    [Covers("DOC-01")]
    public void Constructor_Options_AreHonoured()
    {
        var doc = new Doc(new DocOptions { Guid = "g", Gc = false, ClientId = 7, AutoLoad = true, ShouldLoad = false, CollectionId = "col", Meta = "m" });
        Assert.Equal("g", doc.Guid);
        Assert.False(doc.Gc);
        Assert.Equal(7UL, doc.ClientId);
        Assert.True(doc.AutoLoad);
        Assert.False(doc.ShouldLoad);
        Assert.Equal("col", doc.CollectionId);
        Assert.Equal("\"m\"", Json(doc.Meta));
    }

    [Fact]
    [Covers("DOC-02")]
    [Requires(Feature.Text)]
    public void ClientId_IsAnUnsignedThirtyTwoBitValue_WrittenAsAVarUint()
    {
        var draws = Enumerable.Range(0, 40).Select(_ => new Doc().ClientId).ToList();
        Assert.Equal(draws.Count, draws.Distinct().Count());
        Assert.Contains(draws, id => id > int.MaxValue);   // the range is 0..2^32-1, so about half of the draws exceed 2^31

        var doc = Net.Mk(uint.MaxValue);
        Assert.Equal((ulong)uint.MaxValue, doc.ClientId);
        doc.GetText("t").Insert(0, "x");
        Assert.Equal("01ffffffff0f01", Hex.Of(Net.StateVector(doc)));
        Assert.Equal("0101ffffffff0f0004010174017800", Hex.Of(doc.EncodeStateAsUpdate()));
    }

    // ------------------------------------------------------------------ DOC-03..DOC-06 root types

    [Fact]
    [Covers("DOC-03")]
    [Requires(Feature.Text)]
    public void Get_ReturnsTheSameInstance_AndRegistersItInShare()
    {
        var doc = new Doc();
        var a = doc.Get("x");
        Assert.Same(a, doc.Get("x"));
        Assert.Same(a, doc.Share["x"]);
        var text = doc.GetText("t");
        Assert.Same(text, doc.GetText("t"));
        Assert.Same(text, doc.Get("t"));
        Assert.Same(text, doc.Share["t"]);
        Assert.Equal(new[] { "x", "t" }, doc.Share.Keys.ToArray());   // insertion order
    }

    [Fact]
    [Covers("DOC-04")]
    [Requires(Feature.Text)]
    public void GenericRoot_FromADecodedUpdate_IsPromotedByALaterTypedGet()
    {
        var a = Net.Mk(1);
        a.GetText("t").Insert(0, "hello");
        var b = Net.Mk(2);
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        Assert.IsType<YUntyped>(b.Share["t"]);                        // the update references root "t" without saying what it is
        Assert.Same(b.Share["t"], b.Get("t"));
        var promoted = b.GetText("t");
        Assert.Equal("hello", promoted.ToString());
        Assert.Same(promoted, b.Share["t"]);
        Assert.Same(promoted, b.Get("t"));
        b.GetText("t").Insert(5, "!");
        Assert.Equal("hello!", b.GetText("t").ToString());
        Net.Sync(b, a);
        Assert.Equal("hello!", a.GetText("t").ToString());
    }

    [Fact]
    [Covers("DOC-05")]
    [Requires(Feature.Text, Feature.Array, Feature.Map)]
    public void Get_WithADifferentConcreteType_Throws_AndGenericGetReturnsTheTypedRoot()
    {
        var doc = new Doc();
        var text = doc.GetText("x");
        Assert.Throws<YTypeConflictException>(() => doc.GetArray("x"));
        Assert.Throws<YTypeConflictException>(() => doc.GetMap("x"));
        Assert.Same(text, doc.Get("x"));                              // asking for the generic type never conflicts
        Assert.Same(text, doc.GetText("x"));
    }

    [Fact]
    [Covers("DOC-06")]
    [Requires(Feature.Text, Feature.Array, Feature.Map, Feature.Xml)]
    public void TypedGetters_DefaultToTheEmptyName_WhichIsADistinctKey()
    {
        var doc = new Doc();
        Assert.Same(doc.GetText(), doc.GetText(""));
        Assert.NotSame(doc.GetText(), doc.GetText("a"));
        Assert.Throws<YTypeConflictException>(() => doc.GetArray());   // "" is already a text, as in Yjs
        var arrays = new Doc();
        Assert.Same(arrays.GetArray(), arrays.GetArray(""));
        var maps = new Doc();
        Assert.Same(maps.GetMap(), maps.GetMap(""));
        Assert.Same(doc.GetXmlFragment("f"), doc.GetXmlFragment("f"));
        var element = doc.GetXmlElement("e");
        Assert.Same(element, doc.GetXmlElement("e"));
        Assert.Equal("UNDEFINED", element.NodeName);
        Assert.Contains("", doc.Share.Keys);
    }

    [Fact]
    [PortOf("doc.tests.js:testGetTypeEmptyId", "DOC-03", "DOC-06")]
    [Requires(Feature.Text)]
    public void GetText_EmptyRootName_IsTheSameTypeAsTheDefaultRoot()
    {
        var doc1 = new Doc();
        doc1.GetText("").Insert(0, "h");
        doc1.GetText().Insert(1, "i");
        var doc2 = new Doc();
        doc2.ApplyUpdate(doc1.EncodeStateAsUpdate());
        Assert.Equal("hi", doc2.GetText().ToString());
        Assert.Equal("hi", doc2.GetText("").ToString());
    }

    // ------------------------------------------------------------------ DOC-07 toJSON

    [Fact]
    [PortOf("doc.tests.js:testToJSON", "DOC-07")]
    [Requires(Feature.Array, Feature.Map)]
    public void Port_ToJson_IsEmptyObjectThenArrayAndRecursiveMap()
    {
        var doc = new Doc();
        Assert.Equal("{}", Json(doc.ToJson()));

        var arr = doc.GetArray("array");
        arr.Push(new YEntry[] { "test1" });
        var map = doc.GetMap("map");
        map.Set("k1", "v1");
        var map2 = new YMap();
        map.Set("k2", map2);
        map2.Set("m2k1", "m2v1");

        Assert.Equal("{\"array\":[\"test1\"],\"map\":{\"k1\":\"v1\",\"k2\":{\"m2k1\":\"m2v1\"}}}", Json(doc.ToJson()));
    }

    [Fact]
    [Covers("DOC-07")]
    [Requires(Feature.Text, Feature.Array)]
    public void ToJson_IncludesEmptyTypedRoots_AndTextAsPlainStrings()
    {
        var doc = new Doc();
        doc.GetArray("a");
        doc.GetText("t").Insert(0, "hi");
        var json = Json(doc.ToJson());
        Assert.Contains("\"a\":[]", json);
        Assert.Contains("\"t\":\"hi\"", json);
    }

    // ------------------------------------------------------------------ DOC-08, DOC-09 transactions and event order

    [Fact]
    [Covers("DOC-08")]
    public void Transact_DefaultsToANullOrigin_AndANestedCallJoinsTheOuterTransactionIgnoringItsOrigin()
    {
        var doc = new Doc();
        Transaction? outer = null, inner = null;
        object? outerOrigin = "unset", innerOrigin = "unset", defaultOrigin = "unset";
        doc.Transact(tx => defaultOrigin = tx.Origin);
        doc.Transact(tx =>
        {
            outer = tx;
            outerOrigin = tx.Origin;
            doc.Transact(tx2 => { inner = tx2; innerOrigin = tx2.Origin; }, "inner");
        }, "outer");
        Assert.Null(defaultOrigin);
        Assert.Equal("outer", outerOrigin);
        Assert.Equal("outer", innerOrigin);                            // the inner origin is ignored
        Assert.Same(outer, inner);
    }

    [Fact]
    [PortOf("doc.tests.js:testOriginInTransaction", "DOC-08", "DOC-09")]
    [Requires(Feature.TextFormat, Feature.Text, Feature.Events, Feature.Snapshots)]
    public void Port_OriginInTransaction_ASnapshotReadAddsACleanupTransaction()
    {
        var doc = new Doc();
        var ytext = doc.GetText();
        var origins = new List<object?>();
        doc.AfterTransaction += (tx, _) =>
        {
            origins.Add(tx.Origin);
            if (origins.Count <= 1)
            {
                ytext.ToDelta(YSnapshot.Create(doc));                 // adding a snapshot forces toDelta to create a cleanup transaction
                doc.Transact(_ => ytext.Insert(0, "a"), "nested");
            }
        };
        doc.Transact(_ => ytext.Insert(0, "0"), "first");
        Assert.Equal(new object?[] { "first", "cleanup", "nested" }, origins);
    }

    [Fact]
    [PortOf("doc.tests.js:testAfterTransactionRecursion", "DOC-09")]
    [Requires(Feature.Text, Feature.Xml, Feature.Events)]
    public void Port_AfterTransactionRecursion_ReadingTheTreeFromAnObserverOf15000InsertsDoesNotOverflow()
    {
        var doc = new Doc();
        var xml = doc.GetXmlFragment("");
        doc.AfterTransaction += (tx, _) =>
        {
            if (Equals(tx.Origin, "test")) xml.ToJson();
        };
        doc.Transact(_ =>
        {
            for (var i = 0; i < 15000; i++) xml.Push(new YType[] { new YXmlText("a") });
        }, "test");
        Assert.Equal(15000, xml.Length);
    }

    [Fact]
    [Covers("DOC-09")]
    [Requires(Feature.Text, Feature.V2, Feature.Events, Feature.Subdocs)]
    public void TransactionEvents_AnyTransaction_FireInTheDocumentedOrder()
    {
        var doc = Net.Mk(1);
        var log = new List<string>();
        doc.BeforeAllTransactions += (_, _) => log.Add("beforeAllTransactions");
        doc.BeforeTransaction += (_, _) => log.Add("beforeTransaction");
        doc.BeforeObserverCalls += (_, _) => log.Add("beforeObserverCalls");
        doc.AfterTransaction += (_, _) => log.Add("afterTransaction");
        doc.AfterTransactionCleanup += (_, _) => log.Add("afterTransactionCleanup");
        doc.AfterAllTransactions += (_, _) => log.Add("afterAllTransactions");
        doc.Update += (_, _, _, _) => log.Add("update");
        doc.UpdateV2 += (_, _, _, _) => log.Add("updateV2");
        doc.SubdocsChanged += (_, _, _) => log.Add("subdocs");
        var t = doc.GetText("t");
        t.Observe((_, _) => log.Add("observe"));
        t.ObserveDeep((_, _) => log.Add("observeDeep"));
        doc.Transact(_ => { log.Add("body"); t.Insert(0, "abc"); });
        Assert.Equal(new[]
        {
            "beforeAllTransactions", "beforeTransaction", "body", "beforeObserverCalls", "observe", "observeDeep",
            "afterTransaction", "afterTransactionCleanup", "update", "updateV2", "afterAllTransactions",
        }, log);
    }

    [Fact]
    [Covers("DOC-09")]
    [Requires(Feature.Text, Feature.Events)]
    public void ATransactionOpenedByAnObserver_RunsItsBodyAtOnce_ButItsCleanupWaitsForTheOuterOne()
    {
        var doc = Net.Mk(1);
        var log = new List<string>();
        string O(Transaction tx) => tx.Origin as string ?? "";
        doc.BeforeAllTransactions += (tx, _) => log.Add("beforeAllTransactions:" + O(tx));   // Yjs passes no transaction; y-sharp passes the first one
        doc.BeforeTransaction += (tx, _) => log.Add("beforeTransaction:" + O(tx));
        doc.BeforeObserverCalls += (tx, _) => log.Add("beforeObserverCalls:" + O(tx));
        doc.AfterTransaction += (tx, _) => log.Add("afterTransaction:" + O(tx));
        doc.AfterTransactionCleanup += (tx, _) => log.Add("afterTransactionCleanup:" + O(tx));
        doc.AfterAllTransactions += (txs, _) => log.Add("afterAllTransactions:" + string.Join(",", txs.Select(O)));   // the whole cleanup round (A5)
        doc.Update += (_, origin, _, _) => log.Add("update:" + origin);
        var t = doc.GetText("t");
        var first = true;
        t.Observe((_, _) =>
        {
            log.Add("observe");
            if (!first) return;
            first = false;
            doc.Transact(_ => { log.Add("inner-body"); t.Insert(0, "z"); }, "second");
        });
        doc.Transact(_ => t.Insert(0, "a"), "first");
        Assert.Equal(new[]
        {
            "beforeAllTransactions:first", "beforeTransaction:first", "beforeObserverCalls:first", "observe", "beforeTransaction:second", "inner-body",
            "afterTransaction:first", "afterTransactionCleanup:first", "update:first", "beforeObserverCalls:second", "observe",
            "afterTransaction:second", "afterTransactionCleanup:second", "update:second", "afterAllTransactions:first,second",
        }, log);
    }

    // ------------------------------------------------------------------ DOC-10 update events

    [Fact]
    [Covers("DOC-10")]
    [Requires(Feature.Text, Feature.V2, Feature.Events)]
    public void UpdateEvents_CarryBytesOriginDocAndTransaction_AndFireOnlyForRealChanges()
    {
        var doc = Net.Mk(1);
        var v1 = new List<string>();
        var v2 = new List<string>();
        doc.Update += (u, origin, d, tx) =>
        {
            Assert.Same(doc, d);
            Assert.True(tx.IsLocal);
            v1.Add(Hex.Of(u) + "|" + origin);
        };
        doc.UpdateV2 += (u, _, _, _) => v2.Add(Hex.Of(u));
        var t = doc.GetText("t");
        doc.Transact(_ => { });                                        // no content: no event
        t.Insert(0, "abc");
        doc.Transact(_ => { t.Insert(3, "d"); t.Delete(0, 2); }, "o");
        t.Delete(0, 0);                                                // nothing deleted: no event
        Assert.Equal(new[] { "01010100040101740361626300|", "0101010384010201640101010002|o" }, v1);
        Assert.Equal(new[] { "000001010000010407047461626301030101000001010000", "00000241000104000184030164010000000101030101010001" }, v2);
    }

    [Fact]
    [Covers("DOC-10")]
    [Requires(Feature.Text, Feature.Events)]
    public void UpdateEvents_NoSubscriber_AreNotEncoded()
    {
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        t.Insert(0, "a");                                              // no subscriber: nothing to observe, and nothing breaks
        var seen = 0;
        UpdateHandler handler = (_, _, _, _) => seen++;
        doc.Update += handler;
        t.Insert(1, "b");
        doc.Update -= handler;
        t.Insert(2, "c");
        Assert.Equal(1, seen);
    }

    // ------------------------------------------------------------------ DOC-11 client id collision

    private sealed class ScriptedIds : IClientIdGenerator
    {
        private readonly Queue<ulong> _ids;
        public ScriptedIds(params ulong[] ids) => _ids = new Queue<ulong>(ids);
        public ulong Next() => _ids.Dequeue();
    }

    [Fact]
    [PortOf("doc.tests.js:testClientIdDuplicateChange", "DOC-11")]
    [Requires(Feature.Array)]
    public void Port_ClientIdDuplicateChange_ReceivingAnUpdateFromTheSameClientIdPicksANewId()
    {
        var doc1 = new Doc();
        doc1.ClientId = 0;
        var doc2 = new Doc(new DocOptions { ClientIdGenerator = new ScriptedIds(0, 5) });   // the seam: JS relies on Math.random here
        doc2.ClientId = 0;
        Assert.Equal(doc1.ClientId, doc2.ClientId);
        doc1.GetArray("a").Insert(0, new YEntry[] { 1, 2 });
        doc2.ApplyUpdate(doc1.EncodeStateAsUpdate());
        Assert.NotEqual(doc1.ClientId, doc2.ClientId);
    }

    [Fact]
    [Covers("DOC-11")]
    [Requires(Feature.Text)]
    public void ClientIdRecovery_RemoteUpdateAdvancedOurOwnClock_HappensOnlyThen()
    {
        var generator = new ScriptedIds(1, 77);
        var b = new Doc(new DocOptions { ClientIdGenerator = generator });
        var other = Net.Mk(2);
        other.GetText("t").Insert(0, "x");
        b.ApplyUpdate(other.EncodeStateAsUpdate());                    // a different client id: no collision, no regeneration
        Assert.Equal(1UL, b.ClientId);
        var same = Net.Mk(1);
        same.GetText("t").Insert(0, "y");
        b.ApplyUpdate(same.EncodeStateAsUpdate());
        Assert.Equal(77UL, b.ClientId);
    }

    // ------------------------------------------------------------------ DOC-12..DOC-14 load and sync

    [Fact]
    [Covers("DOC-12")]
    [Requires(Feature.Subdocs)]
    public void Load_OnARootDoc_DoesNothing_AndIsIdempotent()
    {
        var doc = new Doc();
        var events = 0;
        doc.SubdocsChanged += (_, _, _) => events++;
        doc.Load();
        doc.Load();
        Assert.Equal(0, events);
        Assert.True(doc.ShouldLoad);
    }

    [Fact]
    [PortOf("doc.tests.js:testLoadDocsEvent", "DOC-13")]
    public void Port_LoadDocsEvent_IsLoadedFlipsOnTheLoadedEvent()
    {
        var ydoc = new Doc();
        Assert.False(ydoc.IsLoaded);
        var loadedEvent = false;
        ydoc.Loaded += _ => loadedEvent = true;
        ydoc.SignalLoaded();                                   // JS: ydoc.emit('load', [ydoc])
        Assert.True(loadedEvent);
        Assert.True(ydoc.IsLoaded);
    }

    [Fact]
    [PortOf("doc.tests.js:testSyncDocsEvent", "DOC-14")]
    public void Port_SyncDocsEvent_SyncTrueLoadsAndSyncsAndSyncFalseOnlyUnsyncs()
    {
        var ydoc = new Doc();
        Assert.False(ydoc.IsLoaded);
        Assert.False(ydoc.IsSynced);
        var loadedEvents = 0;
        var syncedEvents = new List<bool>();
        ydoc.Loaded += _ => loadedEvents++;
        ydoc.Synced += d => syncedEvents.Add(d.IsSynced);
        ydoc.SignalSynced(true);                             // JS: ydoc.emit('sync', [true, ydoc])
        Assert.Equal(1, loadedEvents);
        Assert.Equal(new[] { true }, syncedEvents);
        Assert.True(ydoc.IsLoaded);
        Assert.True(ydoc.IsSynced);

        ydoc.SignalSynced(false);
        Assert.Equal(1, loadedEvents);                                 // already loaded: no second load event
        Assert.Equal(new[] { true, false }, syncedEvents);
        Assert.True(ydoc.IsLoaded);
        Assert.False(ydoc.IsSynced);
    }

    [Fact]
    [Covers("DOC-13", "DOC-14")]
    public void Synced_WhileAlreadyLoaded_DoesNotRaiseLoadedAgain()
    {
        var doc = new Doc();
        var loaded = 0;
        doc.Loaded += _ => loaded++;
        doc.SignalLoaded();
        doc.SignalSynced(true);
        Assert.Equal(1, loaded);
        Assert.True(doc.IsSynced);
    }

    // ------------------------------------------------------------------ DOC-15..DOC-17

    [Fact]
    [Covers("DOC-15")]
    [Requires(Feature.Array, Feature.Subdocs)]
    public void Destroy_MarksTheDocDestroyed_RaisesTheEventsOnce_AndDestroysSubdocsFirst()
    {
        var parent = Net.Mk(1);
        var arr = parent.GetArray("arr");
        var sub = new Doc(new DocOptions { Guid = "s" });
        arr.Push(new YEntry[] { sub });
        var log = new List<string>();
        sub.Destroying += _ => log.Add("sub-destroying");
        parent.Destroying += d => log.Add("parent-destroying:" + d.IsDestroyed);
        parent.Destroyed += _ => log.Add("parent-destroyed");
        parent.Destroy();
        Assert.True(parent.IsDestroyed);
        Assert.True(sub.IsDestroyed);
        Assert.Equal(new[] { "sub-destroying", "parent-destroying:True", "parent-destroyed" }, log);
    }

    [Fact]
    [Covers("DOC-15")]
    public void Destroy_CalledTwice_DoesNotThrow()
    {
        var doc = new Doc();
        doc.Destroy();
        doc.Destroy();
        Assert.True(doc.IsDestroyed);
    }

    [Fact]
    [Covers("DOC-16")]
    [Requires(Feature.Map, Feature.Subdocs)]
    public void Subdocs_AreTheLiveSet_AndGuidsCollapseDuplicates()
    {
        var doc = Net.Mk(1);
        var map = doc.GetMap("m");
        var subdocs = doc.Subdocs;
        Assert.Empty(subdocs);
        map.Set("a", new Doc(new DocOptions { Guid = "x" }));
        map.Set("b", new Doc(new DocOptions { Guid = "x" }));
        map.Set("c", new Doc(new DocOptions { Guid = "y" }));
        Assert.Equal(3, doc.Subdocs.Count);
        Assert.Equal(3, subdocs.Count);                                // the same collection instance reflects later changes
        var guids = doc.GetSubdocGuids();
        Assert.Equal(new[] { "x", "y" }, guids.OrderBy(g => g, StringComparer.Ordinal).ToArray());   // duplicates collapse
        map.Set("d", new Doc(new DocOptions { Guid = "z" }));
        Assert.Equal(2, guids.Count);                                  // GetSubdocGuids returns a new set, not a live view
        Assert.Equal(3, doc.GetSubdocGuids().Count);
    }

    [Fact]
    [Covers("DOC-17")]
    [Requires(Feature.Array, Feature.Subdocs)]
    public void CollectionId_SubdocHasNone_IsInheritedFromTheParent()
    {
        var parent = new Doc(new DocOptions { ClientId = 1, CollectionId = "parent-col" });
        var inherits = new Doc(new DocOptions { Guid = "i" });
        var keeps = new Doc(new DocOptions { Guid = "k", CollectionId = "own" });
        parent.GetArray("arr").Push(new YEntry[] { inherits, keeps });
        Assert.Equal("parent-col", inherits.CollectionId);
        Assert.Equal("own", keeps.CollectionId);
    }

    [Fact]
    [Covers("DOC-17")]
    [Requires(Feature.Array, Feature.Subdocs)]
    public void Meta_SubdocWithOptions_IsPersisted()
    {
        var parent = Net.Mk(1);
        var sub = new Doc(new DocOptions { Guid = "m", Meta = "meta-value" });
        parent.GetArray("arr").Push(new YEntry[] { sub });
        var remote = Net.Mk(2);
        remote.ApplyUpdate(parent.EncodeStateAsUpdate());
        var copy = remote.Subdocs.Single();
        Assert.Equal("\"meta-value\"", Json(copy.Meta));
        Assert.Null(copy.CollectionId);                      // collectionid is not serialised
    }

    // ------------------------------------------------------------------ DOC-18 event contract

    [Fact]
    [Covers("DOC-18")]
    [Requires(Feature.Text, Feature.Events)]
    public void Handlers_RunInRegistrationOrder_AndStopAfterBeingRemoved()
    {
        var doc = Net.Mk(1);
        var order = new List<string>();
        TransactionHandler first = (_, _) => order.Add("first");
        TransactionHandler second = (_, _) => order.Add("second");
        doc.AfterTransaction += first;
        doc.AfterTransaction += second;
        doc.GetText("t").Insert(0, "a");
        doc.AfterTransaction -= first;
        doc.GetText("t").Insert(1, "b");
        Assert.Equal(new[] { "first", "second", "second" }, order);
    }

    [Fact]
    [Covers("DOC-18")]
    [Requires(Feature.Text, Feature.Events)]
    public void EventArguments_AnyDocEvent_AreTheDocAndATransactionBoundToIt()
    {
        var doc = Net.Mk(1);
        var checkedOnce = false;
        doc.AfterTransaction += (tx, d) =>
        {
            Assert.Same(doc, d);
            Assert.Same(doc, tx.Doc);
            Assert.True(tx.IsLocal);
            Assert.True(tx.IsWrite);
            checkedOnce = true;
        };
        doc.GetText("t").Insert(0, "a");
        Assert.True(checkedOnce);
    }
}
