using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Traceability;
using static YSharp.Tests.DocFeatures.Ds;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.DocFeatures;

/// <summary>
/// Ports of the upstream <c>undo-redo.tests.js</c> tests. Each keeps the upstream operations and expectations; the multi-client ones use the
/// <c>init</c> harness (<see cref="Users"/>). Clocks are virtual (<see cref="Ds.Opts"/>), so capture merging is the same as upstream's
/// "everything inside one millisecond" and never depends on test speed. Deviations are named in the test comments:
/// the stub has no root <c>XmlText</c> getter and no <c>UndoManagerOptions.Doc</c>, so those tests use a <c>YText</c> root / a one-type scope.
/// </summary>
[Requires(Feature.Undo)]
public sealed class UndoPortTests
{
    private static readonly YValue Bold = Attrs(("bold", true));

    // ------------------------------------------------------------------ formatting

    [Fact]
    [PortOf("undo-redo.tests.js:testInconsistentFormat", "UM-18")]
    [Requires(Feature.TextFormat, Feature.Xml)]
    public void Undo_InconsistentFormatMerge_KeepsTheMergedFormatting()
    {
        static void TestYjsMerge(Doc ydoc)
        {
            var content = ydoc.GetXmlText("text");
            content.Format(0, 6, Attrs(("bold", YValue.Null)));
            content.Format(6, 4, Attrs(("type", "text")));
            Assert.Equal("[Merge Test{\"type\":\"text\"}][ After{\"italic\":true,\"type\":\"text\"}]", Delta(content));
        }

        static Doc Initialize()
        {
            var yDoc = new Doc(new DocOptions { Gc = false });
            var content = yDoc.GetXmlText("text");
            content.Insert(0, " After", Attrs(("type", "text"), ("italic", true)));
            content.Insert(0, "Test", Attrs(("type", "text")));
            content.Insert(0, "Merge ", Attrs(("type", "text"), ("bold", true)));
            return yDoc;
        }

        TestYjsMerge(Initialize());
        var initial = Initialize();
        var yDoc = new Doc(new DocOptions { Gc = false });
        yDoc.ApplyUpdate(initial.EncodeStateAsUpdate());
        TestYjsMerge(yDoc);
    }

    // ------------------------------------------------------------------ capture timeout, scopes

    [Fact]
    [PortOf("undo-redo.tests.js:testInfiniteCaptureTimeout", "UM-03")]
    [Requires(Feature.Sync)]
    public void Undo_InfiniteCaptureTimeout_MergesEverythingUntilStopCapturing()
    {
        var u = Users.Init(3);
        var array0 = u.Array(0);
        var undoManager = Um(array0, captureTimeoutMs: 1_000_000_000);   // upstream passes Number.MAX_VALUE
        array0.Push(new YEntry[] { 1, 2, 3 });
        undoManager.StopCapturing();
        array0.Push(new YEntry[] { 4, 5, 6 });
        undoManager.Undo();
        Assert.Equal("[1,2,3]", J(array0));
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testEmptyTypeScope", "UM-02", "UM-16")]
    [Requires(Feature.Array)]
    public void Undo_EmptyTypeScopeExtendedLater_UndoesTheAddedType()
    {
        var ydoc = new Doc();
        var um = new UndoManager(Array.Empty<YType>(), Opts(doc: ydoc));   // upstream: new UndoManager([], { doc: ydoc })
        var yarray = ydoc.GetArray();
        um.AddToScope(yarray);
        yarray.Insert(0, new YEntry[] { 1 });
        um.Undo();
        Assert.Equal(0, yarray.Length);
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testGlobalScope", "UM-02")]
    [Requires(Feature.Array)]
    public void Undo_GlobalScope_UndoesAnyType()
    {
        var ydoc = new Doc();
        var um = new UndoManager(ydoc, Opts());
        var yarray = ydoc.GetArray();
        yarray.Insert(0, new YEntry[] { 1 });
        um.Undo();
        Assert.Equal(0, yarray.Length);
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testTypeScope", "UM-02")]
    [Requires(Feature.Text, Feature.Sync)]
    public void Undo_TypeScope_IgnoresChangesOutsideTheScope()
    {
        var u = Users.Init(3);
        var array0 = u.Array(0);
        var text0 = new YText();
        var text1 = new YText();
        array0.Insert(0, new YEntry[] { text0, text1 });
        var undoManager = Um(text0);
        var undoManagerBoth = Um(new YType[] { text0, text1 });
        text1.Insert(0, "abc");
        Assert.Empty(undoManager.UndoStack);
        Assert.Single(undoManagerBoth.UndoStack);
        Assert.Equal("abc", text1.ToString());
        undoManager.Undo();
        Assert.Equal("abc", text1.ToString());
        undoManagerBoth.Undo();
        Assert.Equal("", text1.ToString());
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testTrackClass", "UM-01")]
    [Requires(Feature.Sync)]
    public void Undo_TrackedOriginClass_TracksOnlyOriginsOfThatType()
    {
        var u = Users.Init(3);
        var text0 = u.Text(0);
        // only track origins that are numbers: a Type in the set matches by the origin's type
        var undoManager = Um(text0, trackedOrigins: Origins(typeof(int)));
        u.Doc(0).Transact(_ => text0.Insert(0, "abc"), 42);
        Assert.Equal("abc", text0.ToString());
        undoManager.Undo();
        Assert.Equal("", text0.ToString());
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testRejectUpdateExample", "UM-01", "UM-17")]
    [Requires(Feature.Array)]
    public void Undo_RejectUpdateExample_RestoresTheRestrictedRoot()
    {
        var tmpydoc1 = new Doc();
        tmpydoc1.GetArray("restricted").Insert(0, new YEntry[] { 1 });
        tmpydoc1.GetArray("public").Insert(0, new YEntry[] { 1 });
        var update1 = tmpydoc1.EncodeStateAsUpdate();
        var tmpydoc2 = new Doc();
        tmpydoc2.GetArray("public").Insert(0, new YEntry[] { 2 });
        var update2 = tmpydoc2.EncodeStateAsUpdate();

        var ydoc = new Doc();
        var restrictedType = ydoc.GetArray("restricted");

        // Incoming updates are applied under the origin "remote change"; an undo manager that tracks only that origin undoes whatever touched the restricted type.
        // (Upstream also saves and restores store.pendingDs/pendingStructs here; both stay empty for these complete updates.)
        void UpdateHandler(byte[] update)
        {
            var um = Um(restrictedType, trackedOrigins: Origins("remote change"));
            try
            {
                ydoc.ApplyUpdate(update, "remote change");
            }
            finally
            {
                while (um.UndoStack.Count > 0) um.Undo();
                um.Dispose();
            }
        }

        UpdateHandler(update1);
        UpdateHandler(update2);
        Assert.Equal(0, restrictedType.Length);
        Assert.Equal(2, ydoc.GetArray("public").Length);
    }

    // ------------------------------------------------------------------ text

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoText", "UM-10", "UM-20", "UM-05")]
    [Requires(Feature.TextFormat, Feature.Sync)]
    public void Undo_Text_UndoesAndRedoesInsertsAndDeletes()
    {
        var u = Users.Init(3);
        var text0 = u.Text(0);
        var text1 = u.Text(1);
        var undoManager = Um(text0);

        // items that are added & deleted in the same transaction won't be undone
        text0.Insert(0, "test");
        text0.Delete(0, 4);
        undoManager.Undo();
        Assert.Equal("", text0.ToString());

        // follow redone items
        text0.Insert(0, "a");
        undoManager.StopCapturing();
        text0.Delete(0, 1);
        undoManager.StopCapturing();
        undoManager.Undo();
        Assert.Equal("a", text0.ToString());
        undoManager.Undo();
        Assert.Equal("", text0.ToString());

        text0.Insert(0, "abc");
        text1.Insert(0, "xyz");
        u.SyncAll();
        undoManager.Undo();
        Assert.Equal("xyz", text0.ToString());
        undoManager.Redo();
        Assert.Equal("abcxyz", text0.ToString());
        u.SyncAll();
        text1.Delete(0, 1);
        u.SyncAll();
        undoManager.Undo();
        Assert.Equal("xyz", text0.ToString());
        undoManager.Redo();
        Assert.Equal("bcxyz", text0.ToString());
        // test marks
        text0.Format(1, 3, Bold);
        Assert.Equal("[b][cxy{\"bold\":true}][z]", Delta(text0));
        undoManager.Undo();
        Assert.Equal("[bcxyz]", Delta(text0));
        undoManager.Redo();
        Assert.Equal("[b][cxy{\"bold\":true}][z]", Delta(text0));
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testDoubleUndo", "UM-05", "UM-10")]
    [Requires(Feature.Text)]
    public void Undo_TwoConsecutiveUndos_RestoreTheOriginalText()
    {
        var doc = new Doc();
        var text = doc.GetText();
        text.Insert(0, "1221");
        var manager = Um(text);
        text.Insert(2, "3");
        text.Insert(3, "3");
        manager.Undo();
        manager.Undo();
        text.Insert(2, "3");
        Assert.Equal("12321", text.ToString());
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoInEmbed", "UM-18")]
    [Requires(Feature.TextFormat, Feature.Text, Feature.Sync)]
    public void Undo_EmbeddedNestedText_UndoesTheEmbedEdits()
    {
        var u = Users.Init(3);
        var text0 = u.Text(0);
        var undoManager = Um(text0);
        var nestedText = new YText("initial text");
        undoManager.StopCapturing();
        text0.InsertEmbed(0, nestedText, Bold);
        Assert.Equal("initial text", nestedText.ToString());
        undoManager.StopCapturing();
        nestedText.Delete(0, nestedText.Length);
        nestedText.Insert(0, "other text");
        Assert.Equal("other text", nestedText.ToString());
        undoManager.Undo();
        Assert.Equal("initial text", nestedText.ToString());
        undoManager.Undo();
        Assert.Equal(0, text0.Length);
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoDeleteTextFormat", "UM-18")]
    [Requires(Feature.TextFormat, Feature.Text)]
    public void Undo_DeletedFormattedText_RestoresTheFormat()
    {
        var doc = new Doc();
        var text = doc.GetText();
        text.Insert(0, "Attack ships on fire off the shoulder of Orion.");
        var doc2 = new Doc();
        var text2 = doc2.GetText();
        doc2.ApplyUpdate(doc.EncodeStateAsUpdate());
        var undoManager = Um(text);

        text.Format(13, 7, Bold);
        undoManager.StopCapturing();
        doc2.ApplyUpdate(doc.EncodeStateAsUpdate());

        text.Format(16, 4, Attrs(("bold", YValue.Null)));
        undoManager.StopCapturing();
        doc2.ApplyUpdate(doc.EncodeStateAsUpdate());

        undoManager.Undo();
        doc2.ApplyUpdate(doc.EncodeStateAsUpdate());

        const string expect = "[Attack ships ][on fire{\"bold\":true}][ off the shoulder of Orion.]";
        Assert.Equal(expect, Delta(text));
        Assert.Equal(expect, Delta(text2));
    }

    // ------------------------------------------------------------------ map

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoMap", "UM-20", "UM-12")]
    [Requires(Feature.Map, Feature.Sync)]
    public void Undo_Map_UndoesAndRedoesKeyChanges()
    {
        var u = Users.Init(2);
        var map0 = u.Map(0);
        var map1 = u.Map(1);
        map0.Set("a", 0);
        var undoManager = Um(map0);
        map0.Set("a", 1);
        undoManager.Undo();
        Assert.Equal(0, Get(map0, "a").Value.AsNumber());
        undoManager.Redo();
        Assert.Equal(1, Get(map0, "a").Value.AsNumber());
        // testing sub-types and if it can restore a whole type
        var subType = new YMap();
        map0.Set("a", subType);
        subType.Set("x", 42);
        Assert.Equal("{\"a\":{\"x\":42}}", J(map0));
        undoManager.Undo();
        Assert.Equal(1, Get(map0, "a").Value.AsNumber());
        undoManager.Redo();
        Assert.Equal("{\"a\":{\"x\":42}}", J(map0));
        u.SyncAll();
        // if content is overwritten by another user, undo operations should be skipped
        map1.Set("a", 44);
        u.SyncAll();
        undoManager.Undo();
        Assert.Equal(44, Get(map0, "a").Value.AsNumber());
        undoManager.Redo();
        Assert.Equal(44, Get(map0, "a").Value.AsNumber());

        // test setting value multiple times
        map0.Set("b", "initial");
        undoManager.StopCapturing();
        map0.Set("b", "val1");
        map0.Set("b", "val2");
        undoManager.StopCapturing();
        undoManager.Undo();
        Assert.Equal("initial", Get(map0, "b").Value.AsString());
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoDeleteInMap", "UM-10", "UM-12")]
    [Requires(Feature.Sync)]
    public void Undo_MapKeyDeletedAndSetAgain_UndoesStepByStep()
    {
        var u = Users.Init(3);
        var map0 = u.Map(0);
        var undoManager = Um(map0, captureTimeoutMs: 0);
        map0.Set("a", "a");
        map0.Delete("a");
        map0.Set("a", "b");
        map0.Delete("a");
        map0.Set("a", "c");
        map0.Delete("a");
        map0.Set("a", "d");
        Assert.Equal("{\"a\":\"d\"}", J(map0));
        undoManager.Undo();
        Assert.Equal("{}", J(map0));
        undoManager.Undo();
        Assert.Equal("{\"a\":\"c\"}", J(map0));
        undoManager.Undo();
        Assert.Equal("{}", J(map0));
        undoManager.Undo();
        Assert.Equal("{\"a\":\"b\"}", J(map0));
        undoManager.Undo();
        Assert.Equal("{}", J(map0));
        undoManager.Undo();
        Assert.Equal("{\"a\":\"a\"}", J(map0));
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testBehaviorOfIgnoreremotemapchangesProperty", "UM-12")]
    [Requires(Feature.Map, Feature.Events)]
    public void Undo_IgnoreRemoteMapChanges_ControlsWhetherRemoteKeyChangesAreUndone()
    {
        var doc = new Doc();
        var doc2 = new Doc();
        doc.Update += (update, _, _, _) => doc2.ApplyUpdate(update, doc);
        doc2.Update += (update, _, _, _) => doc.ApplyUpdate(update, doc2);
        var map1 = doc.GetMap();
        var map2 = doc2.GetMap();
        var um1 = Um(map1, ignoreRemoteMapChanges: true);
        map1.Set("x", 1);
        map2.Set("x", 2);
        map1.Set("x", 3);
        map2.Set("x", 4);
        um1.Undo();
        Assert.Equal(2, Get(map1, "x").Value.AsNumber());
        Assert.Equal(2, Get(map2, "x").Value.AsNumber());
    }

    // ------------------------------------------------------------------ array

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoArray", "UM-20", "UM-10")]
    [Requires(Feature.Map, Feature.Sync)]
    public void Undo_Array_UndoesAndRedoesInsertsAndDeletes()
    {
        var u = Users.Init(3);
        var array0 = u.Array(0);
        var array1 = u.Array(1);
        var undoManager = Um(array0);
        array0.Insert(0, new YEntry[] { 1, 2, 3 });
        array1.Insert(0, new YEntry[] { 4, 5, 6 });
        u.SyncAll();
        Assert.Equal("[1,2,3,4,5,6]", J(array0));
        undoManager.Undo();
        Assert.Equal("[4,5,6]", J(array0));
        undoManager.Redo();
        Assert.Equal("[1,2,3,4,5,6]", J(array0));
        u.SyncAll();
        array1.Delete(0, 1); // user1 deletes [1]
        u.SyncAll();
        undoManager.Undo();
        Assert.Equal("[4,5,6]", J(array0));
        undoManager.Redo();
        Assert.Equal("[2,3,4,5,6]", J(array0));
        array0.Delete(0, 5);
        // test nested structure
        var ymap = new YMap();
        array0.Insert(0, new YEntry[] { ymap });
        Assert.Equal("[{}]", J(array0));
        undoManager.StopCapturing();
        ymap.Set("a", 1);
        Assert.Equal("[{\"a\":1}]", J(array0));
        undoManager.Undo();
        Assert.Equal("[{}]", J(array0));
        undoManager.Undo();
        Assert.Equal("[2,3,4,5,6]", J(array0));
        undoManager.Redo();
        Assert.Equal("[{}]", J(array0));
        undoManager.Redo();
        Assert.Equal("[{\"a\":1}]", J(array0));
        u.SyncAll();
        ((YMap)array1[0].Type!).Set("b", 2);
        u.SyncAll();
        Assert.Equal("[{\"a\":1,\"b\":2}]", J(array0));
        undoManager.Undo();
        Assert.Equal("[{\"b\":2}]", J(array0));
        undoManager.Undo();
        Assert.Equal("[2,3,4,5,6]", J(array0));
        undoManager.Redo();
        Assert.Equal("[{\"b\":2}]", J(array0));
        undoManager.Redo();
        Assert.Equal("[{\"a\":1,\"b\":2}]", J(array0));
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoDeleteFilter", "UM-10")]
    [Requires(Feature.Map, Feature.Sync)]
    public void Undo_DeleteFilter_KeepsFilteredItemsWhenUndoingTheirInsert()
    {
        var u = Users.Init(3);
        var array0 = u.Array(0);
        // upstream: item => !(item instanceof Item) || (item.content instanceof ContentType && item.content.type._map.size === 0).
        // ItemView exposes only the id, so the filter is keyed on the id of the empty map's item (client 0: map0 is clock 0, its "hi" entry clock 1, map1 clock 2).
        var undoManager = Um(array0, deleteFilter: item => item.Id.Clock == 2);
        var map0 = new YMap();
        map0.Set("hi", 1);
        var map1 = new YMap();
        array0.Insert(0, new YEntry[] { map0, map1 });
        undoManager.Undo();
        Assert.Equal(1, array0.Length);
        Assert.Single(((YMap)array0[0].Type!).Keys);
    }

    // ------------------------------------------------------------------ cross-document and regression cases

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoUntilChangePerformed", "UM-09")]
    [Requires(Feature.Array, Feature.Map, Feature.Events)]
    public void Undo_UntilAChangeIsPerformed_SkipsNoOpStackItems()
    {
        var doc = new Doc();
        var doc2 = new Doc();
        doc.Update += (update, _, _, _) => doc2.ApplyUpdate(update);
        doc2.Update += (update, _, _, _) => doc.ApplyUpdate(update);

        var yArray = doc.GetArray("array");
        var yArray2 = doc2.GetArray("array");
        var yMap = new YMap();
        yMap.Set("hello", "world");
        yArray.Push(new YEntry[] { yMap });
        var yMap2 = new YMap();
        yMap2.Set("key", "value");
        yArray.Push(new YEntry[] { yMap2 });

        var undoManager = Um(new YType[] { yArray }, trackedOrigins: Origins(doc.ClientId));
        var undoManager2 = Um(new YType[] { (YType)doc2.Get("array") }, trackedOrigins: Origins(doc2.ClientId));

        doc.Transact(_ => yMap2.Set("key", "value modified"), doc.ClientId);
        undoManager.StopCapturing();
        doc.Transact(_ => yMap.Set("hello", "world modified"), doc.ClientId);
        doc2.Transact(_ => yArray2.Delete(0), doc2.ClientId);
        undoManager2.Undo();
        undoManager.Undo();
        Assert.Equal("value", Get(yMap2, "key").Value.AsString());
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoNestedUndoIssue", "UM-10", "UM-11")]
    [Requires(Feature.Array, Feature.Map)]
    public void Undo_NestedMapsAndArrays_UndoesEachVersion()
    {
        var doc = new Doc(new DocOptions { Gc = false });
        var design = doc.GetMap();
        var undoManager = Um(design, captureTimeoutMs: 0);
        var text = new YMap();

        var blocks1 = new YArray();
        var blocks1block = new YMap();
        doc.Transact(_ =>
        {
            blocks1block.Set("text", "Type Something");
            blocks1.Push(new YEntry[] { blocks1block });
            text.Set("blocks", blocks1block);
            design.Set("text", text);
        });

        var blocks2 = new YArray();
        var blocks2block = new YMap();
        doc.Transact(_ =>
        {
            blocks2block.Set("text", "Something");
            blocks2.Push(new YEntry[] { blocks2block });
            text.Set("blocks", blocks2block);
        });

        var blocks3 = new YArray();
        var blocks3block = new YMap();
        doc.Transact(_ =>
        {
            blocks3block.Set("text", "Something Else");
            blocks3.Push(new YEntry[] { blocks3block });
            text.Set("blocks", blocks3block);
        });

        Assert.Equal("{\"text\":{\"blocks\":{\"text\":\"Something Else\"}}}", J(design));
        undoManager.Undo();
        Assert.Equal("{\"text\":{\"blocks\":{\"text\":\"Something\"}}}", J(design));
        undoManager.Undo();
        Assert.Equal("{\"text\":{\"blocks\":{\"text\":\"Type Something\"}}}", J(design));
        undoManager.Undo();
        Assert.Equal("{}", J(design));
        undoManager.Redo();
        Assert.Equal("{\"text\":{\"blocks\":{\"text\":\"Type Something\"}}}", J(design));
        undoManager.Redo();
        Assert.Equal("{\"text\":{\"blocks\":{\"text\":\"Something\"}}}", J(design));
        undoManager.Redo();
        Assert.Equal("{\"text\":{\"blocks\":{\"text\":\"Something Else\"}}}", J(design));
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testConsecutiveRedoBug", "UM-10", "UM-11")]
    [Requires(Feature.Map)]
    public void Redo_ConsecutiveRedos_RestoreEachState()
    {
        var doc = new Doc();
        var yRoot = doc.GetMap();
        var undoMgr = Um(yRoot);

        var yPoint = new YMap();
        yPoint.Set("x", 0);
        yPoint.Set("y", 0);
        yRoot.Set("a", yPoint);
        undoMgr.StopCapturing();

        yPoint.Set("x", 100);
        yPoint.Set("y", 100);
        undoMgr.StopCapturing();

        yPoint.Set("x", 200);
        yPoint.Set("y", 200);
        undoMgr.StopCapturing();

        yPoint.Set("x", 300);
        yPoint.Set("y", 300);
        undoMgr.StopCapturing();

        Assert.Equal("{\"x\":300,\"y\":300}", J(yPoint));

        undoMgr.Undo(); // x=200, y=200
        Assert.Equal("{\"x\":200,\"y\":200}", J(yPoint));
        undoMgr.Undo(); // x=100, y=100
        Assert.Equal("{\"x\":100,\"y\":100}", J(yPoint));
        undoMgr.Undo(); // x=0, y=0
        Assert.Equal("{\"x\":0,\"y\":0}", J(yPoint));
        undoMgr.Undo(); // nil
        Assert.True(Absent(yRoot, "a"));

        undoMgr.Redo(); // x=0, y=0
        yPoint = (YMap)Get(yRoot, "a").Type!;

        Assert.Equal("{\"x\":0,\"y\":0}", J(yPoint));
        undoMgr.Redo(); // x=100, y=100
        Assert.Equal("{\"x\":100,\"y\":100}", J(yPoint));
        undoMgr.Redo(); // x=200, y=200
        Assert.Equal("{\"x\":200,\"y\":200}", J(yPoint));
        undoMgr.Redo(); // expected x=300, y=300, actually nil upstream before the fix
        Assert.Equal("{\"x\":300,\"y\":300}", J(yPoint));
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoBlockBug", "UM-10")]
    [Requires(Feature.Array, Feature.Map)]
    public void Undo_VersionedBlocks_UndoesEachBlockVersion()
    {
        var doc = new Doc(new DocOptions { Gc = false });
        var design = doc.GetMap();
        var undoManager = Um(design, captureTimeoutMs: 0);
        var text = new YMap();

        void Version(string value, bool first)
        {
            var blocks = new YArray();
            var block = new YMap();
            doc.Transact(_ =>
            {
                block.Set("text", value);
                blocks.Push(new YEntry[] { block });
                text.Set("blocks", block);
                if (first) design.Set("text", text);
            });
        }

        Version("1", true);
        Version("2", false);
        Version("3", false);
        Version("4", false);

        // {"text":{"blocks":{"text":"4"}}}
        undoManager.Undo(); // {"text":{"blocks":{"3"}}}
        undoManager.Undo(); // {"text":{"blocks":{"text":"2"}}}
        undoManager.Undo(); // {"text":{"blocks":{"text":"1"}}}
        undoManager.Undo(); // {}
        undoManager.Redo(); // {"text":{"blocks":{"text":"1"}}}
        undoManager.Redo(); // {"text":{"blocks":{"text":"2"}}}
        undoManager.Redo(); // {"text":{"blocks":{"text":"3"}}}
        undoManager.Redo(); // {"text":{}}
        Assert.Equal("{\"text\":{\"blocks\":{\"text\":\"4\"}}}", J(design));
    }

    // ------------------------------------------------------------------ xml

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoXml", "UM-18")]
    [Requires(Feature.TextFormat, Feature.Text, Feature.Xml, Feature.Sync)]
    public void Undo_Xml_UndoesAndRedoesTreeEdits()
    {
        var u = Users.Init(3);
        var xml0 = u.Xml(0);
        var undoManager = Um(xml0);
        var child = new YXmlElement("p");
        xml0.Insert(0, new YType[] { child });
        var textchild = new YXmlText("content");
        child.Insert(0, new YType[] { textchild });
        Assert.Equal("<undefined><p>content</p></undefined>", xml0.ToString());
        // format textchild and revert that change
        undoManager.StopCapturing();
        textchild.Format(3, 4, Attrs(("bold", YValue.Object(Array.Empty<KeyValuePair<string, YValue>>()))));
        Assert.Equal("<undefined><p>con<bold>tent</bold></p></undefined>", xml0.ToString());
        undoManager.Undo();
        Assert.Equal("<undefined><p>content</p></undefined>", xml0.ToString());
        undoManager.Redo();
        Assert.Equal("<undefined><p>con<bold>tent</bold></p></undefined>", xml0.ToString());
        xml0.Delete(0, 1);
        Assert.Equal("<undefined></undefined>", xml0.ToString());
        undoManager.Undo();
        Assert.Equal("<undefined><p>con<bold>tent</bold></p></undefined>", xml0.ToString());
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoXmlBug", "UM-18")]
    [Requires(Feature.Xml)]
    public void Undo_XmlElementCreatedAndEdited_UndoesCleanly()
    {
        const string origin = "origin";
        var doc = new Doc();
        var fragment = doc.GetXmlFragment("t");
        var undoManager = Um(fragment, captureTimeoutMs: 0, trackedOrigins: Origins(origin));

        // create element
        doc.Transact(_ =>
        {
            var e = new YXmlElement("test-node");
            e.SetAttribute("a", "100");
            e.SetAttribute("b", "0");
            fragment.Insert(fragment.Length, new YType[] { e });
        }, origin);

        // change one attribute
        doc.Transact(_ => ((YXmlElement)fragment[0]).SetAttribute("a", "200"), origin);

        // change both attributes
        doc.Transact(_ =>
        {
            var e = (YXmlElement)fragment[0];
            e.SetAttribute("a", "180");
            e.SetAttribute("b", "50");
        }, origin);

        undoManager.Undo();
        undoManager.Undo();
        undoManager.Undo();

        undoManager.Redo();
        undoManager.Redo();
        undoManager.Redo();
        Assert.Equal("<test-node a=\"180\" b=\"50\"></test-node>", fragment.ToString());
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testSpecialDeletionCase", "UM-10", "UM-18")]
    [Requires(Feature.Xml)]
    public void Undo_SpecialDeletionOfAnElement_RestoresTheElement()
    {
        const string origin = "undoable";
        var doc = new Doc();
        var fragment = doc.GetXmlFragment();
        var undoManager = Um(fragment, trackedOrigins: Origins(origin));
        doc.Transact(_ =>
        {
            var e = new YXmlElement("test");
            e.SetAttribute("a", "1");
            e.SetAttribute("b", "2");
            fragment.Insert(0, new YType[] { e });
        });
        Assert.Equal("<test a=\"1\" b=\"2\"></test>", fragment.ToString());
        doc.Transact(_ =>
        {
            // change attribute "b" and delete test-node
            var e = (YXmlElement)fragment[0];
            e.SetAttribute("b", "3");
            fragment.Delete(0);
        }, origin);
        Assert.Equal("", fragment.ToString());
        undoManager.Undo();
        Assert.Equal("<test a=\"1\" b=\"2\"></test>", fragment.ToString());
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoSetAttributeAndDeleteSyncsAttributes", "UM-12", "UM-18")]
    [Requires(Feature.TextFormat, Feature.Text, Feature.Xml, Feature.V2)]
    public void Undo_SetAttributeThenDelete_SyncsTheAttributes()
    {
        var doc = new Doc();
        var root = doc.GetXmlText("sharedRoot");   // upstream: doc.get('sharedRoot', Y.XmlText)
        var button = new YXmlText();
        button.SetAttribute("type", "button");
        button.SetAttribute("test", true);
        button.Insert(0, "Click me");
        root.InsertEmbed(0, button);

        var undoManager = Um(root);
        undoManager.StopCapturing();

        button.SetAttribute("type", "paragraph");
        root.Delete(0, 1);
        undoManager.Undo();

        static void AssertButton(YText rootText)
        {
            var embedded = (YText)rootText.ToDelta()[0].Embed.Type!;
            Assert.True(embedded.TryGetAttribute("type", out var type));
            Assert.Equal("button", type.AsString());
            Assert.True(embedded.TryGetAttribute("test", out var test));
            Assert.True(test.AsBool());
            Assert.Equal("Click me", embedded.ToString());
        }

        AssertButton(root);

        var remoteDoc = new Doc();
        remoteDoc.ApplyUpdate(doc.EncodeStateAsUpdate(default, UpdateFormat.V2), null, UpdateFormat.V2);
        AssertButton(remoteDoc.GetXmlText("sharedRoot"));
    }

    // ------------------------------------------------------------------ events

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoEvents", "UM-04", "UM-14")]
    [Requires(Feature.Sync)]
    public void Undo_StackEvents_FireWithTheExpectedMetadata()
    {
        var u = Users.Init(3);
        var text0 = u.Text(0);
        var undoManager = Um(text0);
        var counter = 0;
        var receivedMetadata = -1;
        undoManager.StackItemAdded += e =>
        {
            Assert.Contains(text0, e.ChangedParentTypes);
            e.Item.Meta["test"] = counter++;
        };
        undoManager.StackItemPopped += e =>
        {
            Assert.Contains(text0, e.ChangedParentTypes);
            receivedMetadata = (int)e.Item.Meta["test"]!;
        };
        text0.Insert(0, "abc");
        undoManager.Undo();
        Assert.Equal(0, receivedMetadata);
        undoManager.Redo();
        Assert.Equal(1, receivedMetadata);
    }

    [Fact]
    [PortOf("undo-redo.tests.js:testUndoDoingStackItem", "UM-14")]
    [Requires(Feature.Text, Feature.Events)]
    public void Undo_DoingStackItem_ExposesTheItemMetaDuringUndoAndRedo()
    {
        var doc = new Doc();
        var text = doc.GetText("text");
        var undoManager = Um(text);
        undoManager.StackItemAdded += e => e.Item.Meta["str"] = "42";
        object? metaUndo = null;
        object? metaRedo = null;
        text.Observe((_, tx) =>
        {
            if (!ReferenceEquals(tx.Origin, undoManager)) return;
            if (undoManager.Undoing) metaUndo = undoManager.CurrStackItem?.Meta["str"];
            else if (undoManager.Redoing) metaRedo = undoManager.CurrStackItem?.Meta["str"];
        });
        text.Insert(0, "abc");
        undoManager.Undo();
        undoManager.Redo();
        Assert.Equal("42", metaUndo);   // currStackItem is accessible while undoing
        Assert.Equal("42", metaRedo);   // and while redoing
        Assert.Null(undoManager.CurrStackItem);   // and null after the transaction
    }
}
