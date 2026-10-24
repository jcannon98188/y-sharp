using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.DocFeatures;

/// <summary>
/// Subdocuments (SUB-01..SUB-14, docs/research/doc-features.md section 2) and the ports of the upstream subdoc tests. Wire bytes come from
/// running the same operations on the pinned Yjs; the oracle fixtures under <c>fixtures/subdocs</c> cover the same entries end to end.
/// </summary>
public sealed class SubdocTests
{
    private static Doc Sub(string guid, bool? gc = null, bool autoLoad = false, bool shouldLoad = true, YValue meta = default, string? collectionId = null) =>
        new(new DocOptions { Guid = guid, Gc = gc ?? true, AutoLoad = autoLoad, ShouldLoad = shouldLoad, Meta = meta, CollectionId = collectionId });

    private static string UpdateHex(Doc parent, UpdateFormat format = UpdateFormat.V1) => Net.Hex(parent.EncodeStateAsUpdate(default, format));

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
    public void ContentDoc_IsRef9_WithGuidAndAnOptionsMap_AndHasLengthOne() => Impl.Run(() =>
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
    });

    [Theory]
    [InlineData("default", false, false, null, "010101000901036172720167760000")]
    [InlineData("gc-false", true, false, null, "01010100090103617272016776010267637900")]
    [InlineData("auto-load", false, true, null, "0101010009010361727201677601086175746f4c6f61647800")]
    [InlineData("meta", false, false, "m", "0101010009010361727201677601046d65746177016d00")]
    [InlineData("all", true, true, "m", "010101000901036172720167760302676379086175746f4c6f616478046d65746177016d00")]
    [Covers("SUB-02")]
    public void Options_OnlyNonDefaultFlagsAreWritten_InTheOrderGcAutoLoadMeta(string label, bool gcOff, bool autoLoad, string? meta, string expectedHex) => Impl.Run(() =>
    {
        _ = label;
        var parent = Net.Mk(1);
        parent.GetArray("arr").Push(new YEntry[] { Sub("g", gc: !gcOff, autoLoad: autoLoad, meta: meta) });
        Assert.Equal(expectedHex, UpdateHex(parent));
    });

    [Fact]
    [Covers("SUB-02")]
    public void Options_ShouldLoadAndCollectionId_AreNotSerialised() => Impl.Run(() =>
    {
        var parent = Net.Mk(1);
        parent.GetArray("arr").Push(new YEntry[] { Sub("g", shouldLoad: false, collectionId: "c") });
        Assert.Equal("010101000901036172720167760000", UpdateHex(parent));
    });

    [Fact]
    [Covers("SUB-02")]
    public void Options_AnObjectMeta_IsWrittenAsAnAnyValue() => Impl.Run(() =>
    {
        var parent = Net.Mk(1);
        var meta = YValue.Object(new[] { new KeyValuePair<string, YValue>("a", 1) });
        parent.GetArray("arr").Push(new YEntry[] { Sub("g", meta: meta) });
        Assert.Equal("0101010009010361727201677601046d657461760101617d0100", UpdateHex(parent));
    });

    // ------------------------------------------------------------------ SUB-03, SUB-04

    [Fact]
    [Covers("SUB-03")]
    public void RemoteSubdoc_DoesNotLoadUnlessAutoLoad_AndKeepsGcAndMeta() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        a.GetArray("arr").Push(new YEntry[] { Sub("plain"), Sub("auto", autoLoad: true, gc: false, meta: "m") });
        var b = Net.Mk(2);
        var w = new Watch(b);
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        var plain = b.Subdocs.Single(d => d.Guid == "plain");
        var auto = b.Subdocs.Single(d => d.Guid == "auto");
        Assert.False(DocSeams.ShouldLoad(plain));
        Assert.False(DocSeams.AutoLoad(plain));
        Assert.True(DocSeams.ShouldLoad(auto));
        Assert.True(DocSeams.AutoLoad(auto));
        Assert.False(auto.Gc);
        Assert.True(plain.Gc);
        Assert.Equal(new[] { "plain", "auto" }, w.Added.Select(d => d.Guid).ToArray());
        Assert.Equal(new[] { "auto" }, w.Loaded.Select(d => d.Guid).ToArray());
    });

    [Fact]
    [Covers("SUB-04")]
    public void LocalSubdoc_IsReportedAddedAndLoaded_AndPointsAtItsParent() => Impl.Run(() =>
    {
        var parent = Net.Mk(1);
        var w = new Watch(parent);
        var sub = Sub("g");
        Assert.Null(sub.ParentDoc);
        parent.GetArray("arr").Push(new YEntry[] { sub });
        AssertEvent(w, new[] { "g" }, Array.Empty<string>(), new[] { "g" });
        Assert.Same(parent, sub.ParentDoc);
        Assert.Contains(sub, parent.Subdocs);
    });

    // ------------------------------------------------------------------ SUB-05

    [Fact]
    [Covers("SUB-05")]
    public void AddingTheSameDocTwice_IsOnlyLogged_NeverThrown() => Impl.Run(() =>
    {
        var parent = Net.Mk(1);
        var arr = parent.GetArray("arr");
        var sub = Sub("g");
        arr.Push(new YEntry[] { sub });
        var ex = Record.Exception(() => arr.Push(new YEntry[] { sub }));   // JS only calls console.error here
        Assert.Null(ex);
        Assert.Equal(2, arr.Length);
    });

    // ------------------------------------------------------------------ SUB-06

    [Fact]
    [Covers("SUB-06")]
    public void SubdocsEvent_FiresInCleanupAfterTheUpdateEvents_AndAdoptsTheParentsClientId() => Impl.Run(() =>
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
        Assert.Equal(1u, sub.ClientId);                                 // adopted from the parent at add time
        Assert.NotEqual(before, sub.ClientId);
    });

    [Fact]
    [Covers("SUB-06")]
    public void SubdocsEvent_IsNotRaisedWhenNothingChanged() => Impl.Run(() =>
    {
        var parent = Net.Mk(1);
        var w = new Watch(parent);
        parent.GetText("t").Insert(0, "no subdocs here");
        Assert.Equal(0, w.Count);
    });

    // ------------------------------------------------------------------ SUB-07, SUB-08

    [Fact]
    [PortOf("doc.tests.js:testSubdoc", "SUB-03", "SUB-04", "SUB-06", "SUB-07", "SUB-08", "DOC-12", "DOC-16")]
    public void Port_Subdoc() => Impl.Run(() =>
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

            Assert.Equal(new[] { "a", "c" }, doc.Subdocs.Select(d => d.Guid).Distinct().ToArray());
        }

        var doc2 = new Doc();
        {
            Assert.Empty(doc2.Subdocs);
            var w = new Watch(doc2);
            doc2.ApplyUpdate(doc.EncodeStateAsUpdate());
            AssertEvent(w, new[] { "a", "a", "c" }, Array.Empty<string>(), Array.Empty<string>());

            DocAt(doc2.GetMap("mysubdocs"), "a").Load();
            AssertEvent(w, Array.Empty<string>(), Array.Empty<string>(), new[] { "a" });

            Assert.Equal(new[] { "a", "c" }, doc2.Subdocs.Select(d => d.Guid).Distinct().ToArray());

            doc2.GetMap("mysubdocs").Delete("a");
            AssertEvent(w, Array.Empty<string>(), new[] { "a" }, Array.Empty<string>());
            Assert.Equal(new[] { "a", "c" }, doc2.Subdocs.Select(d => d.Guid).Distinct().ToArray());   // key "b" still holds a doc with guid a
        }
    });

    [Fact]
    [Covers("SUB-07")]
    public void SameGuid_AtSeveralPositions_GivesEachItsOwnInstance() => Impl.Run(() =>
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
    });

    [Fact]
    [Covers("SUB-08")]
    public void AddAndDelete_InOneTransaction_CancelOut() => Impl.Run(() =>
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
        if (w.Count > 0)                                                 // JS still reports an event, but one with no added, removed or loaded docs
        {
            Assert.Empty(w.Added);
            Assert.Empty(w.Removed);
            Assert.Empty(w.Loaded);
        }
    });

    [Fact]
    [Covers("SUB-08")]
    public void Delete_ReportsTheSubdocRemoved_AndDestroysIt() => Impl.Run(() =>
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
    });

    // ------------------------------------------------------------------ SUB-09

    [Fact]
    [Covers("SUB-09")]
    public void DeletedSubdocItem_IsCollectedLikeAnyDeletedItem_AndDoesNotReachANewReplica() => Impl.Run(() =>
    {
        var parent = Net.Mk(1);
        var arr = parent.GetArray("arr");
        arr.Push(new YEntry[] { Sub("g") });
        arr.Delete(0, 1);
        Assert.Equal("01010100010103617272010101010001", UpdateHex(parent));
        var fresh = Net.Mk(2);
        fresh.ApplyUpdate(parent.EncodeStateAsUpdate());
        Assert.Empty(fresh.Subdocs);
    });

    // ------------------------------------------------------------------ SUB-10

    [Fact]
    [PortOf("doc.tests.js:testSubdocsUndo", "SUB-10", "UM-19")]
    public void Port_SubdocsUndo_UndoThenRedoLeavesOneElement() => Impl.Run(() =>
    {
        // Upstream inserts the doc into a Y.XmlFragment; the API stub only lets lists and maps hold a Doc, so this port uses a Y.Array (API gap, see the task notes).
        var ydoc = new Doc();
        var elems = ydoc.GetArray("elems");
        var undoManager = new UndoManager(elems);
        var subdoc = new Doc();
        elems.Insert(0, new YEntry[] { subdoc });
        undoManager.Undo();
        undoManager.Redo();
        Assert.Equal(1, elems.Length);
    });

    [Fact]
    [Covers("SUB-10")]
    public void Redo_CreatesANewDocWithTheSameGuidAndOptions() => Impl.Run(() =>
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
        Assert.True(DocSeams.AutoLoad(copy));
        Assert.False(copy.Gc);
        Assert.Equal(YValueKind.String, DocSeams.Meta(copy).Kind);
        Assert.True(DocSeams.ShouldLoad(copy));                          // derived from autoLoad
    });

    // ------------------------------------------------------------------ SUB-11, SUB-12

    [Fact]
    [PortOf("doc.tests.js:testSubdocLoadEdgeCases", "SUB-03", "SUB-04", "SUB-11", "DOC-12")]
    public void Port_SubdocLoadEdgeCases() => Impl.Run(() =>
    {
        var ydoc = new Doc();
        var yarray = ydoc.GetArray();
        var subdoc1 = new Doc();
        var w = new Watch(ydoc);
        yarray.Insert(0, new YEntry[] { subdoc1 });
        Assert.True(DocSeams.ShouldLoad(subdoc1));
        Assert.False(DocSeams.AutoLoad(subdoc1));
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
        Assert.False(DocSeams.ShouldLoad(subdoc3));
        Assert.False(DocSeams.AutoLoad(subdoc3));
        Assert.Contains(subdoc3, w2.Added);
        Assert.DoesNotContain(subdoc3, w2.Loaded);
        // load
        subdoc3.Load();
        Assert.True(DocSeams.ShouldLoad(subdoc3));
        Assert.DoesNotContain(subdoc3, w2.Added);
        Assert.Contains(subdoc3, w2.Loaded);
    });

    [Fact]
    [PortOf("doc.tests.js:testSubdocLoadEdgeCasesAutoload", "SUB-03", "SUB-11")]
    public void Port_SubdocLoadEdgeCasesAutoload() => Impl.Run(() =>
    {
        var ydoc = new Doc();
        var yarray = ydoc.GetArray();
        var subdoc1 = new Doc(new DocOptions { AutoLoad = true });
        var w = new Watch(ydoc);
        yarray.Insert(0, new YEntry[] { subdoc1 });
        Assert.True(DocSeams.ShouldLoad(subdoc1));
        Assert.True(DocSeams.AutoLoad(subdoc1));
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
        Assert.True(DocSeams.ShouldLoad(subdoc1));
        Assert.True(DocSeams.AutoLoad(subdoc1));
        Assert.Contains(subdoc3, w2.Added);
        Assert.Contains(subdoc3, w2.Loaded);
    });

    [Fact]
    [Covers("SUB-11")]
    public void Destroy_OnASubdoc_ResurrectsAFreshInstanceThatIsAddedButNotLoaded() => Impl.Run(() =>
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
    });

    [Fact]
    [Covers("SUB-12")]
    public void Destroy_OfASubdocWhoseItemWasDeleted_DoesNotReportAReplacementAsAdded() => Impl.Run(() =>
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
    });

    // ------------------------------------------------------------------ SUB-13, SUB-14

    [Fact]
    [Covers("SUB-13")]
    public void DestroyingTheParent_DestroysEverySubdoc() => Impl.Run(() =>
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
    });

    [Fact]
    [Covers("SUB-14")]
    public void SubdocContent_IsIndependent_AndNeverPartOfTheParentUpdate() => Impl.Run(() =>
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
    });
}
