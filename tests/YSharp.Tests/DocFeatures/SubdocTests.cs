using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.DocFeatures;

/// <summary>
/// Subdocuments (SUB-01..SUB-14, docs/research/doc-features.md section 2) and the ports of the upstream subdoc tests. Wire bytes come from
/// running the same operations on the pinned Yjs; the oracle fixtures under <c>fixtures/subdocs</c> cover the same entries end to end.
/// </summary>
[Requires(Feature.Subdocs)]
public sealed class SubdocTests
{
    private static Doc Sub(string guid, bool? gc = null, bool autoLoad = false, bool shouldLoad = true, YValue meta = default, string? collectionId = null) =>
        new(new DocOptions { Guid = guid, Gc = gc ?? true, AutoLoad = autoLoad, ShouldLoad = shouldLoad, Meta = meta, CollectionId = collectionId });

    private static string UpdateHex(Doc parent, UpdateFormat format = UpdateFormat.V1) => Hex.Of(parent.EncodeStateAsUpdate(default, format));

    /// <summary>The last <c>subdocs</c> event as plain lists (the change object is only valid during the callback).</summary>
    private sealed class Watch
    {
        public Watch(Doc doc) => doc.SubdocsChanged += (c, _, _) =>
        {
            Count++;
            Added = c.Added.ToArray();
            Removed = c.Removed.ToArray();
            Loaded = c.Loaded.ToArray();
        };

        public int Count { get; private set; }
        public Doc[] Added { get; private set; } = Array.Empty<Doc>();
        public Doc[] Removed { get; private set; } = Array.Empty<Doc>();
        public Doc[] Loaded { get; private set; } = Array.Empty<Doc>();
        public string[][]? Guids => Count == 0 ? null : new[] { Added.Select(d => d.Guid).ToArray(), Removed.Select(d => d.Guid).ToArray(), Loaded.Select(d => d.Guid).ToArray() };
        public void Reset() { Count = 0; Added = Removed = Loaded = Array.Empty<Doc>(); }
    }

    private static void AssertEvent(Watch w, string[] added, string[] removed, string[] loaded)
    {
        Assert.NotNull(w.Guids);
        Assert.Equal(added, w.Guids![0]);
        Assert.Equal(removed, w.Guids[1]);
        Assert.Equal(loaded, w.Guids[2]);
    }

    private static Doc DocAt(YMap map, string key) => map.TryGet(key, out var e) ? e.Doc! : throw new InvalidOperationException(key);

    // ------------------------------------------------------------------ SUB-01, SUB-02 wire format

    [Fact]
    [Covers("SUB-01")]
    [Requires(Feature.Array, Feature.V2)]
    public void ContentDoc_IsRef9_WithGuidAndAnOptionsMap_AndHasLengthOne()
    {
        var parent = Net.Mk(1);
        parent.GetArray("arr").Push(new YEntry[] { Sub("g") });
        Assert.Equal("010101000901036172720167760000", UpdateHex(parent));
        Assert.Equal("0000010100000109070461727267030101010000010100760000", UpdateHex(parent, UpdateFormat.V2));
        Assert.Equal(1, parent.GetArray("arr").Length);
        parent.GetArray("arr").Push(new YEntry[] { Sub("h") });         // never merges with its neighbour (two structs, not one run)
        var remote = Net.Mk(2);
        remote.ApplyUpdate(parent.EncodeStateAsUpdate());
        Assert.Equal(new[] { "g", "h" }, remote.Subdocs.Select(d => d.Guid).ToArray());
    }

    [Theory]
    [InlineData("default", false, false, null, "010101000901036172720167760000")]
    [InlineData("gc-false", true, false, null, "01010100090103617272016776010267637900")]
    [InlineData("auto-load", false, true, null, "0101010009010361727201677601086175746f4c6f61647800")]
    [InlineData("meta", false, false, "m", "0101010009010361727201677601046d65746177016d00")]
    [InlineData("all", true, true, "m", "010101000901036172720167760302676379086175746f4c6f616478046d65746177016d00")]
    [Covers("SUB-02")]
    [Requires(Feature.Array)]
    public void Options_OnlyNonDefaultFlagsAreWritten_InTheOrderGcAutoLoadMeta(string label, bool gcOff, bool autoLoad, string? meta, string expectedHex)
    {
        _ = label;
        var parent = Net.Mk(1);
        parent.GetArray("arr").Push(new YEntry[] { Sub("g", gc: !gcOff, autoLoad: autoLoad, meta: meta) });
        Assert.Equal(expectedHex, UpdateHex(parent));
    }

    [Fact]
    [Covers("SUB-02")]
    [Requires(Feature.Array)]
    public void Options_ShouldLoadAndCollectionId_AreNotSerialised()
    {
        var parent = Net.Mk(1);
        parent.GetArray("arr").Push(new YEntry[] { Sub("g", shouldLoad: false, collectionId: "c") });
        Assert.Equal("010101000901036172720167760000", UpdateHex(parent));
    }

    [Fact]
    [Covers("SUB-02")]
    [Requires(Feature.Array)]
    public void Options_AnObjectMeta_IsWrittenAsAnAnyValue()
    {
        var parent = Net.Mk(1);
        var meta = YValue.Object(new[] { new KeyValuePair<string, YValue>("a", 1) });
        parent.GetArray("arr").Push(new YEntry[] { Sub("g", meta: meta) });
        Assert.Equal("0101010009010361727201677601046d657461760101617d0100", UpdateHex(parent));
    }

    // ------------------------------------------------------------------ SUB-03, SUB-04

    [Fact]
    [Covers("SUB-03")]
    [Requires(Feature.Array)]
    public void RemoteSubdoc_DoesNotLoadUnlessAutoLoad_AndKeepsGcAndMeta()
    {
        var a = Net.Mk(1);
        a.GetArray("arr").Push(new YEntry[] { Sub("plain"), Sub("auto", autoLoad: true, gc: false, meta: "m") });
        var b = Net.Mk(2);
        var w = new Watch(b);
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        var plain = b.Subdocs.Single(d => d.Guid == "plain");
        var auto = b.Subdocs.Single(d => d.Guid == "auto");
        Assert.False(plain.ShouldLoad);
        Assert.False(plain.AutoLoad);
        Assert.True(auto.ShouldLoad);
        Assert.True(auto.AutoLoad);
        Assert.False(auto.Gc);
        Assert.True(plain.Gc);
        Assert.Equal(new[] { "plain", "auto" }, w.Added.Select(d => d.Guid).ToArray());
        Assert.Equal(new[] { "auto" }, w.Loaded.Select(d => d.Guid).ToArray());
    }

    [Fact]
    [Covers("SUB-04")]
    [Requires(Feature.Array)]
    public void LocalSubdoc_IsReportedAddedAndLoaded_AndPointsAtItsParent()
    {
        var parent = Net.Mk(1);
        var w = new Watch(parent);
        var sub = Sub("g");
        Assert.Null(sub.ParentDoc);
        parent.GetArray("arr").Push(new YEntry[] { sub });
        AssertEvent(w, new[] { "g" }, Array.Empty<string>(), new[] { "g" });
        Assert.Same(parent, sub.ParentDoc);
        Assert.Contains(sub, parent.Subdocs);
    }

    // ------------------------------------------------------------------ SUB-05

    [Fact]
    [Covers("SUB-05")]
    [Requires(Feature.Array)]
    public void AddingTheSameDocTwice_IsOnlyLogged_NeverThrown()
    {
        var parent = Net.Mk(1);
        var arr = parent.GetArray("arr");
        var sub = Sub("g");
        arr.Push(new YEntry[] { sub });
        var ex = Record.Exception(() => arr.Push(new YEntry[] { sub }));   // JS only calls console.error here
        Assert.Null(ex);
        Assert.Equal(2, arr.Length);
    }

    // ------------------------------------------------------------------ SUB-06

    [Fact]
    [Covers("SUB-06")]
    [Requires(Feature.Array, Feature.V2, Feature.Events)]
    public void SubdocsEvent_FiresInCleanupAfterTheUpdateEvents_AndAdoptsTheParentsClientId()
    {
        var parent = Net.Mk(1);
        var log = new List<string>();
        parent.Update += (_, _, _, _) => log.Add("update");
        parent.UpdateV2 += (_, _, _, _) => log.Add("updateV2");
        parent.SubdocsChanged += (_, _, _) => log.Add("subdocs");
        var sub = Sub("g");
        var before = sub.ClientId;
        parent.GetArray("arr").Push(new YEntry[] { sub });
        Assert.True(log.IndexOf("update") < log.IndexOf("subdocs"));
        Assert.True(log.IndexOf("updateV2") < log.IndexOf("subdocs"));
        Assert.Equal(1UL, sub.ClientId);                                 // adopted from the parent at add time
        Assert.NotEqual(before, sub.ClientId);
    }

    [Fact]
    [Covers("SUB-06")]
    [Requires(Feature.Text)]
    public void SubdocsEvent_NothingChanged_IsNotRaised()
    {
        var parent = Net.Mk(1);
        var w = new Watch(parent);
        parent.GetText("t").Insert(0, "no subdocs here");
        Assert.Equal(0, w.Count);
    }

    // ------------------------------------------------------------------ SUB-07, SUB-08

    [Fact]
    [PortOf("doc.tests.js:testSubdoc", "SUB-03", "SUB-04", "SUB-06", "SUB-07", "SUB-08", "DOC-12", "DOC-16")]
    [Requires(Feature.Map)]
    public void Subdoc_AddLoadAndDestroy_RaisesTheDocumentedSubdocsEvents()
    {
        var doc = new Doc();
        doc.Load();                                                      // doesn't do anything
        {
            var w = new Watch(doc);
            var subdocs = doc.GetMap("mysubdocs");
            var docA = Sub("a");
            docA.Load();
            subdocs.Set("a", docA);
            AssertEvent(w, new[] { "a" }, Array.Empty<string>(), new[] { "a" });

            w.Reset();
            DocAt(subdocs, "a").Load();
            Assert.Null(w.Guids);

            w.Reset();
            DocAt(subdocs, "a").Destroy();
            AssertEvent(w, new[] { "a" }, new[] { "a" }, Array.Empty<string>());
            DocAt(subdocs, "a").Load();
            AssertEvent(w, Array.Empty<string>(), Array.Empty<string>(), new[] { "a" });

            subdocs.Set("b", Sub("a", shouldLoad: false));
            AssertEvent(w, new[] { "a" }, Array.Empty<string>(), Array.Empty<string>());
            DocAt(subdocs, "b").Load();
            AssertEvent(w, Array.Empty<string>(), Array.Empty<string>(), new[] { "a" });

            var docC = Sub("c");
            docC.Load();
            subdocs.Set("c", docC);
            AssertEvent(w, new[] { "c" }, Array.Empty<string>(), new[] { "c" });

            Assert.Equal(new[] { "a", "c" }, doc.GetSubdocGuids().OrderBy(g => g, StringComparer.Ordinal).ToArray());
        }

        var doc2 = new Doc();
        {
            Assert.Empty(doc2.Subdocs);
            var w = new Watch(doc2);
            doc2.ApplyUpdate(doc.EncodeStateAsUpdate());
            AssertEvent(w, new[] { "a", "a", "c" }, Array.Empty<string>(), Array.Empty<string>());

            DocAt(doc2.GetMap("mysubdocs"), "a").Load();
            AssertEvent(w, Array.Empty<string>(), Array.Empty<string>(), new[] { "a" });

            Assert.Equal(new[] { "a", "c" }, doc2.GetSubdocGuids().OrderBy(g => g, StringComparer.Ordinal).ToArray());

            doc2.GetMap("mysubdocs").Delete("a");
            AssertEvent(w, Array.Empty<string>(), new[] { "a" }, Array.Empty<string>());
            Assert.Equal(new[] { "a", "c" }, doc2.GetSubdocGuids().OrderBy(g => g, StringComparer.Ordinal).ToArray());   // key "b" still holds a doc with guid a
        }
    }

    [Fact]
    [Covers("SUB-07")]
    [Requires(Feature.Map)]
    public void SameGuid_AtSeveralPositions_GivesEachItsOwnInstance()
    {
        var a = Net.Mk(1);
        var map = a.GetMap("m");
        map.Set("x", Sub("same"));
        map.Set("y", Sub("same"));
        var b = Net.Mk(2);
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        var m = b.GetMap("m");
        Assert.NotSame(DocAt(m, "x"), DocAt(m, "y"));
        Assert.Equal("same", DocAt(m, "x").Guid);
        Assert.Equal(2, b.Subdocs.Count);
    }

    [Fact]
    [Covers("SUB-08")]
    [Requires(Feature.Array)]
    public void AddAndDelete_InOneTransaction_CancelOut()
    {
        var parent = Net.Mk(1);
        var w = new Watch(parent);
        var arr = parent.GetArray("arr");
        parent.Transact(_ =>
        {
            arr.Push(new YEntry[] { Sub("gone") });
            arr.Delete(0, 1);
        });
        Assert.Empty(parent.Subdocs);
        // the add and the delete cancel out, but the load stays: Yjs reports an event with only the loaded doc (fixtures/subdocs/add-and-delete-in-one-transaction)
        AssertEvent(w, Array.Empty<string>(), Array.Empty<string>(), new[] { "gone" });
    }

    [Fact]
    [Covers("SUB-08")]
    [Requires(Feature.Array)]
    public void Delete_ReportsTheSubdocRemoved_AndDestroysIt()
    {
        var parent = Net.Mk(1);
        var arr = parent.GetArray("arr");
        var sub = Sub("g");
        arr.Push(new YEntry[] { sub });
        var w = new Watch(parent);
        arr.Delete(0, 1);
        AssertEvent(w, Array.Empty<string>(), new[] { "g" }, Array.Empty<string>());
        Assert.True(sub.IsDestroyed);
        Assert.Empty(parent.Subdocs);
    }

    [Fact]
    [Covers("SUB-08", "M-6")]
    [Requires(Feature.Map)]
    public void DeletingAMap_RemovesItsSubdocsInTheOrderTheirKeysWereFirstSet()
    {
        // Yjs ContentType.delete walks parent._map, a JS Map, so the removed set follows key insertion order (not key or hash order)
        var parent = Net.Mk(1);
        var root = parent.GetMap("m");
        var keys = new[] { "key", "k0", "zz", "a", "q9", "b", "m", "k1", "x", "c" };
        var holder = root.Set("h", new YMap());
        foreach (var k in keys) holder.Set(k, Sub("g-" + k));
        var removed = new List<string[]>();
        parent.SubdocsChanged += (c, _, _) => removed.Add(c.Removed.Select(d => d.Guid).ToArray());
        root.Delete("h");
        // the deleting transaction's event, then one per removed subdocument as cleanup destroys each, in the same order (SUB-11)
        Assert.Equal(keys.Select(k => "g-" + k).ToArray(), removed[0]);
        Assert.Equal(keys.Select(k => new[] { "g-" + k }), removed.Skip(1));
    }

    // ------------------------------------------------------------------ SUB-09

    [Fact]
    [Covers("SUB-09")]
    [Requires(Feature.Array)]
    public void DeletedSubdocItem_IsCollectedLikeAnyDeletedItem_AndDoesNotReachANewReplica()
    {
        var parent = Net.Mk(1);
        var arr = parent.GetArray("arr");
        arr.Push(new YEntry[] { Sub("g") });
        arr.Delete(0, 1);
        Assert.Equal("01010100010103617272010101010001", UpdateHex(parent));
        var fresh = Net.Mk(2);
        fresh.ApplyUpdate(parent.EncodeStateAsUpdate());
        Assert.Empty(fresh.Subdocs);
    }

    // ------------------------------------------------------------------ SUB-10

    [Fact]
    [PortOf("doc.tests.js:testSubdocsUndo", "SUB-10", "UM-19")]
    [Requires(Feature.Xml, Feature.Undo)]
    public void Port_SubdocsUndo_UndoThenRedoLeavesOneElement()
    {
        var ydoc = new Doc();
        var elems = ydoc.GetXmlFragment("elems");
        var undoManager = new UndoManager(elems);
        var subdoc = new Doc();
        elems.InsertSubdoc(0, subdoc);   // upstream: elems.insert(0, [subdoc]); a fragment holds a subdoc (A8)
        undoManager.Undo();
        undoManager.Redo();
        Assert.Equal(1, elems.Length);
        Assert.Single(ydoc.Subdocs);
    }

    [Fact]
    [Covers("SUB-10")]
    [Requires(Feature.Array, Feature.Undo)]
    public void Redo_AfterUndoOfASubdoc_CreatesANewDocWithTheSameGuidAndOptions()
    {
        var parent = Net.Mk(1);
        var arr = parent.GetArray("arr");
        var undo = new UndoManager(arr);
        var original = Sub("g", autoLoad: true, gc: false, meta: "kept");
        arr.Push(new YEntry[] { original });
        undo.Undo();
        Assert.Empty(parent.Subdocs);
        Assert.True(original.IsDestroyed);
        undo.Redo();
        var copy = parent.Subdocs.Single();
        Assert.NotSame(original, copy);
        Assert.Equal("g", copy.Guid);
        Assert.True(copy.AutoLoad);
        Assert.False(copy.Gc);
        Assert.Equal(YValueKind.String, copy.Meta.Kind);
        Assert.True(copy.ShouldLoad);                          // derived from autoLoad
    }

    // ------------------------------------------------------------------ SUB-11, SUB-12

    [Fact]
    [PortOf("doc.tests.js:testSubdocLoadEdgeCases", "SUB-03", "SUB-04", "SUB-11", "DOC-12")]
    [Requires(Feature.Array)]
    public void Subdoc_LoadEdgeCases_FollowTheLoadRules()
    {
        var ydoc = new Doc();
        var yarray = ydoc.GetArray();
        var subdoc1 = new Doc();
        var w = new Watch(ydoc);
        yarray.Insert(0, new YEntry[] { subdoc1 });
        Assert.True(subdoc1.ShouldLoad);
        Assert.False(subdoc1.AutoLoad);
        Assert.Contains(subdoc1, w.Loaded);
        Assert.Contains(subdoc1, w.Added);
        // destroy and check whether the event adds it again to added (it shouldn't load it)
        subdoc1.Destroy();
        var subdoc2 = yarray[0].Doc!;
        Assert.NotSame(subdoc1, subdoc2);
        Assert.Contains(subdoc2, w.Added);
        Assert.DoesNotContain(subdoc2, w.Loaded);
        // load
        subdoc2.Load();
        Assert.DoesNotContain(subdoc2, w.Added);
        Assert.Contains(subdoc2, w.Loaded);
        // apply from remote
        var ydoc2 = new Doc();
        var w2 = new Watch(ydoc2);
        ydoc2.ApplyUpdate(ydoc.EncodeStateAsUpdate());
        var subdoc3 = ydoc2.GetArray()[0].Doc!;
        Assert.False(subdoc3.ShouldLoad);
        Assert.False(subdoc3.AutoLoad);
        Assert.Contains(subdoc3, w2.Added);
        Assert.DoesNotContain(subdoc3, w2.Loaded);
        // load
        subdoc3.Load();
        Assert.True(subdoc3.ShouldLoad);
        Assert.DoesNotContain(subdoc3, w2.Added);
        Assert.Contains(subdoc3, w2.Loaded);
    }

    [Fact]
    [PortOf("doc.tests.js:testSubdocLoadEdgeCasesAutoload", "SUB-03", "SUB-11")]
    [Requires(Feature.Array)]
    public void Subdoc_AutoloadLoadEdgeCases_FollowTheLoadRules()
    {
        var ydoc = new Doc();
        var yarray = ydoc.GetArray();
        var subdoc1 = new Doc(new DocOptions { AutoLoad = true });
        var w = new Watch(ydoc);
        yarray.Insert(0, new YEntry[] { subdoc1 });
        Assert.True(subdoc1.ShouldLoad);
        Assert.True(subdoc1.AutoLoad);
        Assert.Contains(subdoc1, w.Loaded);
        Assert.Contains(subdoc1, w.Added);
        subdoc1.Destroy();
        var subdoc2 = yarray[0].Doc!;
        Assert.NotSame(subdoc1, subdoc2);
        Assert.Contains(subdoc2, w.Added);
        Assert.DoesNotContain(subdoc2, w.Loaded);
        subdoc2.Load();
        Assert.DoesNotContain(subdoc2, w.Added);
        Assert.Contains(subdoc2, w.Loaded);
        var ydoc2 = new Doc();
        var w2 = new Watch(ydoc2);
        ydoc2.ApplyUpdate(ydoc.EncodeStateAsUpdate());
        var subdoc3 = ydoc2.GetArray()[0].Doc!;
        Assert.True(subdoc1.ShouldLoad);
        Assert.True(subdoc1.AutoLoad);
        Assert.Contains(subdoc3, w2.Added);
        Assert.Contains(subdoc3, w2.Loaded);
    }

    [Fact]
    [Covers("SUB-11")]
    [Requires(Feature.Array)]
    public void Destroy_OnASubdoc_ResurrectsAFreshInstanceThatIsAddedButNotLoaded()
    {
        var parent = Net.Mk(1);
        var arr = parent.GetArray("arr");
        var sub = Sub("g");
        arr.Push(new YEntry[] { sub });
        sub.Destroy();
        var again = arr[0].Doc!;
        Assert.NotSame(sub, again);
        Assert.Equal("g", again.Guid);
        Assert.False(again.IsDestroyed);
        Assert.True(sub.IsDestroyed);
        Assert.Single(parent.Subdocs);
        Assert.Same(again, parent.Subdocs.Single());
    }

    [Fact]
    [Covers("SUB-12")]
    [Requires(Feature.Array)]
    public void Destroy_OfASubdocWhoseItemWasDeleted_DoesNotReportAReplacementAsAdded()
    {
        var parent = Net.Mk(1);
        var arr = parent.GetArray("arr");
        var sub = Sub("g");
        arr.Push(new YEntry[] { sub });
        var w = new Watch(parent);
        parent.Transact(_ =>
        {
            arr.Delete(0, 1);
            sub.Destroy();
        });
        Assert.Empty(w.Added);
        Assert.Equal(new[] { "g" }, w.Removed.Select(d => d.Guid).ToArray());
        Assert.Empty(parent.Subdocs);
    }

    // ------------------------------------------------------------------ SUB-13, SUB-14

    [Fact]
    [Covers("SUB-13")]
    [Requires(Feature.Array, Feature.Map)]
    public void Parent_Destroyed_DestroysEverySubdoc()
    {
        var parent = Net.Mk(1);
        var one = Sub("one");
        var two = Sub("two");
        parent.GetArray("arr").Push(new YEntry[] { one });
        parent.GetMap("m").Set("k", two);
        parent.Destroy();
        Assert.True(one.IsDestroyed);
        Assert.True(two.IsDestroyed);
        Assert.True(parent.IsDestroyed);
    }

    [Fact]
    [Covers("SUB-14")]
    [Requires(Feature.Text, Feature.Array)]
    public void SubdocContent_IsIndependent_AndNeverPartOfTheParentUpdate()
    {
        var parent = Net.Mk(1);
        var sub = Sub("g");
        parent.GetArray("arr").Push(new YEntry[] { sub });
        var before = UpdateHex(parent);
        sub.GetText("x").Insert(0, "inside");
        Assert.Equal(before, UpdateHex(parent));
        Assert.Equal("inside", sub.GetText("x").ToString());
        Assert.DoesNotContain("x", parent.Share.Keys);

        var remote = Net.Mk(2);
        remote.ApplyUpdate(parent.EncodeStateAsUpdate());
        Assert.Equal("", remote.Subdocs.Single().GetText("x").ToString());
    }
}
