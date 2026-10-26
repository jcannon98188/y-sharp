using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Traceability;
using static YSharp.Tests.DocFeatures.Ds;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.DocFeatures;

/// <summary>
/// Snapshots (SNAP-01..SNAP-11, docs/research/doc-features.md section 4) and the ports of the upstream <c>snapshot.tests.js</c> tests.
/// Wire bytes and delta strings come from running the same operations on the pinned Yjs; the oracle fixtures under <c>fixtures/snapshot</c>
/// replay the same behaviour end to end.
/// </summary>
[Requires(Feature.Snapshots)]
public sealed class SnapshotTests
{
    private static Doc NoGc(ulong client = 1) => Net.Mk(client, gc: false);

    private static byte[] Encode(YSnapshot s, UpdateFormat format = UpdateFormat.V1)
    {
        var w = new ArrayBufferWriter<byte>();
        s.Encode(w, format);
        return w.WrittenSpan.ToArray();
    }

    private static YSnapshot Decode(string hex, UpdateFormat format = UpdateFormat.V1)
    {
        Assert.True(YSnapshot.TryDecode(Hex.Bytes(hex), format, out var snapshot).Ok);
        return snapshot;
    }

    // ------------------------------------------------------------------ SNAP-01

    [Fact]
    [Covers("SNAP-01")]
    [Requires(Feature.Text)]
    public void Create_AnyDoc_CapturesTheStateVectorAndTheDeleteSet()
    {
        var doc = NoGc();
        var t = doc.GetText("t");
        t.Insert(0, "world!");
        t.Delete(1, 2);
        var s = YSnapshot.Create(doc);
        Assert.Equal(6, s.StateVector[1]);
        Assert.Equal(0, s.StateVector[2]);
        Assert.False(s.DeleteSet.Contains(new ItemId(1, 0)));
        Assert.True(s.DeleteSet.Contains(new ItemId(1, 1)));
        Assert.True(s.DeleteSet.Contains(new ItemId(1, 2)));
        Assert.False(s.DeleteSet.Contains(new ItemId(1, 3)));
    }

    [Fact]
    [Covers("SNAP-01")]
    [Requires(Feature.Array, Feature.Map)]
    public void Create_CollectedStructs_AreCountedAsDeleted()
    {
        var doc = Net.Mk(1, gc: true);
        var arr = doc.GetArray("a");
        arr.Push(new YEntry[] { new YMap() });
        arr.Delete(0, 1);                                              // the deleted type's children are collected into GC structs
        var s = YSnapshot.Create(doc);
        Assert.True(s.DeleteSet.Contains(new ItemId(1, 0)));
        Assert.Equal(1, s.StateVector[1]);
    }

    // ------------------------------------------------------------------ SNAP-02 wire format

    [Theory]
    [InlineData("empty", "0000", "0000")]
    [InlineData("world", "00010106", "00010106")]
    [InlineData("world-with-deletion", "0101010102010106", "0101010101010106")]
    [Covers("SNAP-02")]
    [Requires(Feature.Text, Feature.V2)]
    public void Encode_AnySnapshot_WritesTheDeleteSetFirstAndThenTheStateVector(string label, string v1, string v2)
    {
        var doc = NoGc();
        if (label != "empty") doc.GetText("t").Insert(0, "world!");
        if (label == "world-with-deletion") doc.GetText("t").Delete(1, 2);
        var s = YSnapshot.Create(doc);
        Assert.Equal(v1, Hex.Of(Encode(s)));
        Assert.Equal(v2, Hex.Of(Encode(s, UpdateFormat.V2)));
        Assert.True(YSnapshot.TryDecode(Encode(s), UpdateFormat.V1, out var back1).Ok);
        Assert.True(s.Equals(back1));
        Assert.True(YSnapshot.TryDecode(Encode(s, UpdateFormat.V2), UpdateFormat.V2, out var back2).Ok);
        Assert.True(s.Equals(back2));
    }

    [Fact]
    [Covers("SNAP-02")]
    public void TryDecode_TruncatedBytes_AreRejected()
    {
        Assert.False(YSnapshot.TryDecode(Hex.Bytes("010101"), UpdateFormat.V1, out _).Ok);
        Assert.False(YSnapshot.TryDecode(ReadOnlySpan<byte>.Empty, UpdateFormat.V1, out _).Ok);
    }

    // ------------------------------------------------------------------ SNAP-03

    [Fact]
    [Covers("SNAP-03")]
    [Requires(Feature.Text)]
    public void Equals_ComparesStateVectorsAndDeleteSets_WithoutNormalising()
    {
        var doc = NoGc();
        var t = doc.GetText("t");
        t.Insert(0, "abcdef");
        var s1 = YSnapshot.Create(doc);
        var s1Again = YSnapshot.Create(doc);
        Assert.True(s1.Equals(s1Again));
        t.Insert(6, "g");
        Assert.False(s1.Equals(YSnapshot.Create(doc)));                // different state vector
        var other = NoGc(2);
        other.GetText("t").Insert(0, "abcdef");
        Assert.False(s1.Equals(YSnapshot.Create(other)));              // different client set
        t.Delete(0, 1);
        Assert.False(YSnapshot.Create(doc).Equals(s1Again));           // different delete set

        // two runs [0,1] [1,1] describe the same deletion as one run [0,2], but no normalisation happens
        var unmerged = Decode("01010200010101" + "010102");
        var merged = Decode("0101010002" + "010102");
        Assert.False(unmerged.Equals(merged));
        Assert.True(unmerged.Equals(Decode("01010200010101" + "010102")));
    }

    // ------------------------------------------------------------------ SNAP-04

    [Fact]
    [Covers("SNAP-04")]
    public void Empty_AnyUse_IsAnEmptySnapshotThatCannotBeMutated()
    {
        var empty = YSnapshot.Empty;
        Assert.Equal(0, empty.StateVector[1]);
        Assert.False(empty.DeleteSet.Contains(new ItemId(1, 0)));
        Assert.Equal("0000", Hex.Of(Encode(empty)));
        Assert.True(empty.Equals(YSnapshot.Create(new Doc())));
        Assert.True(YSnapshot.Empty.Equals(empty));                    // the JS module singleton is shared and mutable; the contract here is content only
    }

    // ------------------------------------------------------------------ SNAP-05

    [Fact]
    [Covers("SNAP-05")]
    [Requires(Feature.Text)]
    public void ReadingWithASnapshot_AfterLaterEdits_ShowsOnlyWhatWasVisibleThen()
    {
        var doc = NoGc();
        var t = doc.GetText("t");
        t.Insert(0, "abc");
        var s1 = YSnapshot.Create(doc);
        t.Insert(3, "def");
        t.Delete(0, 1);
        Assert.Equal("[abc]", Delta(t, s1));
        Assert.Equal("[bcdef]", Delta(t));
    }

    [Fact]
    [PortOf("snapshot.tests.js:testBasicXmlAttributes", "SNAP-05")]
    [Requires(Feature.TextFormat, Feature.Map, Feature.Xml)]
    public void Snapshot_XmlAttributeChanges_ReadAsOfEachSnapshot()
    {
        var ydoc = NoGc(0);
        var yxml = ydoc.GetMap().Set("el", new YXmlElement("div"));
        var snapshot1 = YSnapshot.Create(ydoc);
        yxml.SetAttribute("a", "1");
        var snapshot2 = YSnapshot.Create(ydoc);
        yxml.SetAttribute("a", "2");
        Assert.Equal(new Dictionary<string, string> { ["a"] = "2" }, yxml.GetAttributes().ToDictionary(p => p.Key, p => p.Value.AsString()));
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1" }, yxml.GetAttributes(snapshot2).ToDictionary(p => p.Key, p => p.Value.AsString()));
        Assert.Empty(yxml.GetAttributes(snapshot1));
    }

    // ------------------------------------------------------------------ SNAP-06, SNAP-07

    [Fact]
    [Covers("SNAP-06")]
    [Requires(Feature.Text, Feature.Events)]
    public void ReadingWithASnapshot_MidItemBoundary_SplitsInternallyButChangesNeitherStateNorEvents()
    {
        var doc = NoGc();
        var t = doc.GetText("t");
        t.Insert(0, "hello world");
        var s = YSnapshot.Create(doc);
        t.Insert(5, " brave");
        var before = Hex.Of(doc.EncodeStateAsUpdate());
        var svBefore = Hex.Of(Net.StateVector(doc));
        var updates = 0;
        doc.Update += (_, _, _, _) => updates++;
        Assert.Equal("[hello world]", Delta(t, s));
        Assert.Equal(0, updates);
        Assert.Equal(before, Hex.Of(doc.EncodeStateAsUpdate()));
        Assert.Equal(svBefore, Hex.Of(Net.StateVector(doc)));
    }

    [Fact]
    [Covers("SNAP-07")]
    [Requires(Feature.TextFormat, Feature.Text)]
    public void ToDelta_MarksAddedAndRemovedRuns_WithTheDefaultOrACustomYChange()
    {
        var doc = NoGc();
        var t = doc.GetText("t");
        t.Insert(0, "abc");
        var s1 = YSnapshot.Create(doc);
        t.Insert(3, "def", Attrs(("bold", true)));
        t.Delete(0, 1);
        var s2 = YSnapshot.Create(doc);
        t.Insert(0, "Z");
        Assert.Equal("[bc][def{\"bold\":true}]", Delta(t, s2));
        Assert.Equal("[a{\"ychange\":{\"type\":\"removed\"}}][bc][def{\"bold\":true,\"ychange\":{\"type\":\"added\"}}]", Delta(t, s2, s1));
        Assert.Equal("[a][Z{\"ychange\":{\"type\":\"added\"}}][bc][def{\"bold\":true,\"ychange\":{\"type\":\"added\"}}]", Delta(t, null, s1));
        var custom = string.Concat(t.ToDelta(s2, s1, (type, id) => Attrs(("type", type), ("who", id.Client))).Select(op =>
            "[" + op.Text + (op.Attributes.Kind == YValueKind.Undefined ? "" : Json(op.Attributes)) + "]"));
        Assert.Equal("[a{\"ychange\":{\"type\":\"removed\",\"who\":1}}][bc][def{\"bold\":true,\"ychange\":{\"type\":\"added\",\"who\":1}}]", custom);
    }

    [Fact]
    [Covers("SNAP-07")]
    [Requires(Feature.TextFormat, Feature.Text)]
    public void ToDelta_YChangeWithAUserField_JoinsTheSameClientsRuns_WhereTheDefaultSplitsEveryItem()
    {
        // from the pinned Yjs: toDelta compares cur.user and cur.type, and the default { type } has no user
        var doc = NoGc();
        var t = doc.GetText("t");
        t.Insert(0, "b");
        t.Insert(0, "a");                                              // "a" (clock 1) cannot merge with "b" (clock 0)
        var s1 = YSnapshot.Create(doc);
        t.Delete(0, 2);
        var s2 = YSnapshot.Create(doc);
        Assert.Equal("0101010002010102", Hex.Of(Encode(s2)));
        Assert.Equal("[a{\"ychange\":{\"type\":\"removed\"}}][b{\"ychange\":{\"type\":\"removed\"}}]", Delta(t, s2, s1));
        string WithUser(YSnapshot? s, YSnapshot? p) => string.Concat(t.ToDelta(s, p, (type, id) => Attrs(("type", type), ("user", id.Client))).Select(op =>
            "[" + op.Text + (op.Attributes.Kind == YValueKind.Undefined ? "" : Json(op.Attributes)) + "]"));
        Assert.Equal("[ab{\"ychange\":{\"type\":\"removed\",\"user\":1}}]", WithUser(s2, s1));
        Assert.Equal("[ab{\"ychange\":{\"type\":\"added\",\"user\":1}}]", WithUser(s1, s2));
    }

    // ------------------------------------------------------------------ SNAP-08, SNAP-09

    [Fact]
    [PortOf("snapshot.tests.js:testBasic", "SNAP-08", "SNAP-01")]
    [Requires(Feature.Text)]
    public void Snapshot_TextInsertedAfterwards_RestoresTheEarlierText()
    {
        var ydoc = new Doc(new DocOptions { Gc = false });
        ydoc.GetText().Insert(0, "world!");
        var snapshot = YSnapshot.Create(ydoc);
        ydoc.GetText().Insert(0, "hello ");
        var restored = YSnapshot.CreateDoc(ydoc, snapshot);
        Assert.Equal("world!", restored.GetText().ToString());
    }

    [Fact]
    [PortOf("snapshot.tests.js:testBasicRestoreSnapshot", "SNAP-08")]
    [Requires(Feature.Array)]
    public void Snapshot_ArrayInsertedAfterwards_RestoresTheEarlierArray()
    {
        var doc = new Doc(new DocOptions { Gc = false });
        doc.GetArray("array").Insert(0, new YEntry[] { "hello" });
        var snap = YSnapshot.Create(doc);
        doc.GetArray("array").Insert(1, new YEntry[] { "world" });

        var docRestored = YSnapshot.CreateDoc(doc, snap);

        Assert.Equal("[\"hello\"]", J(docRestored.GetArray("array")));
        Assert.Equal("[\"hello\",\"world\"]", J(doc.GetArray("array")));
    }

    [Fact]
    [PortOf("snapshot.tests.js:testEmptyRestoreSnapshot", "SNAP-08", "SNAP-04")]
    [Requires(Feature.Array)]
    public void Snapshot_UnknownClientStateVector_RestoresAnEmptyArray()
    {
        var doc = new Doc(new DocOptions { Gc = false });
        // upstream: snap.sv.set(9999, 0). The snapshot API is read-only, so the same snapshot is decoded from its bytes: ds empty, sv {9999: 0}.
        var snap = Decode("00018f4e00");
        Assert.Equal(0, snap.StateVector[9999]);
        doc.GetArray().Insert(0, new YEntry[] { "world" });

        var docRestored = YSnapshot.CreateDoc(doc, snap);

        Assert.Equal("[]", J(docRestored.GetArray()));
        Assert.Equal("[\"world\"]", J(doc.GetArray()));

        // now this snapshot reflects the latest state. It should still work.
        var snap2 = YSnapshot.Create(doc);
        var docRestored2 = YSnapshot.CreateDoc(doc, snap2);
        Assert.Equal("[\"world\"]", J(docRestored2.GetArray()));
    }

    [Fact]
    [PortOf("snapshot.tests.js:testRestoreSnapshotWithSubType", "SNAP-08")]
    [Requires(Feature.Array, Feature.Map)]
    public void Snapshot_NestedMapEditedAfterwards_RestoresTheEarlierMap()
    {
        var doc = new Doc(new DocOptions { Gc = false });
        doc.GetArray("array").Insert(0, new YEntry[] { new YMap() });
        var subMap = (YMap)doc.GetArray("array")[0].Type!;
        subMap.Set("key1", "value1");

        var snap = YSnapshot.Create(doc);
        subMap.Set("key2", "value2");

        var docRestored = YSnapshot.CreateDoc(doc, snap);

        Assert.Equal("[{\"key1\":\"value1\"}]", J(docRestored.GetArray("array")));
        Assert.Equal("[{\"key1\":\"value1\",\"key2\":\"value2\"}]", J(doc.GetArray("array")));
    }

    [Fact]
    [PortOf("snapshot.tests.js:testRestoreDeletedItem1", "SNAP-08", "SNAP-05")]
    [Requires(Feature.Array)]
    public void Snapshot_ItemDeletedAfterwards_RestoresTheDeletedItem()
    {
        var doc = new Doc(new DocOptions { Gc = false });
        doc.GetArray("array").Insert(0, new YEntry[] { "item1", "item2" });

        var snap = YSnapshot.Create(doc);
        doc.GetArray("array").Delete(0);

        var docRestored = YSnapshot.CreateDoc(doc, snap);

        Assert.Equal("[\"item1\",\"item2\"]", J(docRestored.GetArray("array")));
        Assert.Equal("[\"item2\"]", J(doc.GetArray("array")));
    }

    [Fact]
    [PortOf("snapshot.tests.js:testRestoreLeftItem", "SNAP-08")]
    [Requires(Feature.Array, Feature.Map)]
    public void Snapshot_LeftNeighbourDeletedAfterwards_RestoresTheLeftItem()
    {
        var doc = new Doc(new DocOptions { Gc = false });
        doc.GetArray("array").Insert(0, new YEntry[] { "item1" });
        doc.GetMap("map").Set("test", 1);
        doc.GetArray("array").Insert(0, new YEntry[] { "item0" });

        var snap = YSnapshot.Create(doc);
        doc.GetArray("array").Delete(1);

        var docRestored = YSnapshot.CreateDoc(doc, snap);

        Assert.Equal("[\"item0\",\"item1\"]", J(docRestored.GetArray("array")));
        Assert.Equal("[\"item0\"]", J(doc.GetArray("array")));
    }

    [Fact]
    [PortOf("snapshot.tests.js:testDeletedItemsBase", "SNAP-08")]
    [Requires(Feature.Array)]
    public void Snapshot_ItemDeletedBeforeAndInsertAfter_RestoresAnEmptyArray()
    {
        var doc = new Doc(new DocOptions { Gc = false });
        doc.GetArray("array").Insert(0, new YEntry[] { "item1" });
        doc.GetArray("array").Delete(0);
        var snap = YSnapshot.Create(doc);
        doc.GetArray("array").Insert(0, new YEntry[] { "item0" });

        var docRestored = YSnapshot.CreateDoc(doc, snap);

        Assert.Equal("[]", J(docRestored.GetArray("array")));
        Assert.Equal("[\"item0\"]", J(doc.GetArray("array")));
    }

    [Fact]
    [PortOf("snapshot.tests.js:testDeletedItems2", "SNAP-08")]
    [Requires(Feature.Array)]
    public void Snapshot_MiddleItemDeletedBeforeAndInsertAfter_RestoresTheRemainingItems()
    {
        var doc = new Doc(new DocOptions { Gc = false });
        doc.GetArray("array").Insert(0, new YEntry[] { "item1", "item2", "item3" });
        doc.GetArray("array").Delete(1);
        var snap = YSnapshot.Create(doc);
        doc.GetArray("array").Insert(0, new YEntry[] { "item0" });

        var docRestored = YSnapshot.CreateDoc(doc, snap);

        Assert.Equal("[\"item1\",\"item3\"]", J(docRestored.GetArray("array")));
        Assert.Equal("[\"item0\",\"item1\",\"item3\"]", J(doc.GetArray("array")));
    }

    [Fact]
    [PortOf("snapshot.tests.js:testDependentChanges", "SNAP-08", "SNAP-05")]
    [Requires(Feature.Array, Feature.Sync)]
    public void Snapshot_DependentChangesFromTwoClients_RestoreConsistently()
    {
        var u = Users.Init(2);
        var array0 = u.Array(0);
        var array1 = u.Array(1);
        var doc0 = array0.Doc ?? throw new InvalidOperationException("no document 0");
        var doc1 = array1.Doc ?? throw new InvalidOperationException("no document 1");

        doc0.Gc = false;
        doc1.Gc = false;

        array0.Insert(0, new YEntry[] { "user1item1" });
        u.SyncAll();
        array1.Insert(1, new YEntry[] { "user2item1" });
        u.SyncAll();

        var snap = YSnapshot.Create(doc0);

        array0.Insert(2, new YEntry[] { "user1item2" });
        u.SyncAll();
        array1.Insert(3, new YEntry[] { "user2item2" });
        u.SyncAll();

        var docRestored0 = YSnapshot.CreateDoc(doc0, snap);
        Assert.Equal("[\"user1item1\",\"user2item1\"]", J(docRestored0.GetArray("array")));

        var docRestored1 = YSnapshot.CreateDoc(doc1, snap);
        Assert.Equal("[\"user1item1\",\"user2item1\"]", J(docRestored1.GetArray("array")));
    }

    [Fact]
    [Covers("SNAP-08")]
    [Requires(Feature.Text, Feature.Events)]
    public void CreateDoc_RequiresGcOff_AndAppliesIntoTheGivenTargetWithTheSnapshotOrigin()
    {
        var gcOn = Net.Mk(1, gc: true);
        gcOn.GetText("t").Insert(0, "a");
        Assert.ThrowsAny<Exception>(() => YSnapshot.CreateDoc(gcOn, YSnapshot.Create(gcOn)));

        var origin = NoGc();
        origin.GetText("t").Insert(0, "hello");
        var snap = YSnapshot.Create(origin);
        origin.GetText("t").Insert(5, "!");
        var target = new Doc(new DocOptions { ClientId = 77 });
        object? seen = null;
        target.Update += (_, o, _, _) => seen = o;
        var result = YSnapshot.CreateDoc(origin, snap, target);
        Assert.Same(target, result);
        Assert.Equal("hello", target.GetText("t").ToString());
        Assert.Equal("snapshot", seen);
        Assert.Equal(77UL, target.ClientId);
    }

    [Fact]
    [Covers("SNAP-09")]
    [Requires(Feature.Text)]
    public void CreateDoc_FromASnapshot_LeavesTheOriginsContentStateVectorAndEncodingUnchanged()
    {
        var doc = NoGc();
        var t = doc.GetText("t");
        t.Insert(0, "hello world");
        var snap = YSnapshot.Create(doc);
        t.Insert(5, " brave");
        var before = Hex.Of(doc.EncodeStateAsUpdate());
        var svBefore = Hex.Of(Net.StateVector(doc));
        var restored = YSnapshot.CreateDoc(doc, snap);
        Assert.Equal("hello world", restored.GetText("t").ToString());
        Assert.Equal("hello brave world", t.ToString());
        Assert.Equal(before, Hex.Of(doc.EncodeStateAsUpdate()));      // the origin may split structs internally; that is not observable here
        Assert.Equal(svBefore, Hex.Of(Net.StateVector(doc)));
    }

    // ------------------------------------------------------------------ SNAP-10

    [Fact]
    [PortOf("snapshot.tests.js:testContainsUpdate", "SNAP-10")]
    [Requires(Feature.Array, Feature.Events)]
    public void ContainsUpdate_SuccessiveSnapshots_ReportsWhichUpdatesAreContained()
    {
        var ydoc = new Doc();
        var updates = new List<byte[]>();
        ydoc.Update += (update, _, _, _) => updates.Add(update.ToArray());
        var yarr = ydoc.GetArray();
        var snapshot1 = YSnapshot.Create(ydoc);
        yarr.Insert(0, new YEntry[] { 1 });
        var snapshot2 = YSnapshot.Create(ydoc);
        yarr.Delete(0, 1);
        var snapshotFinal = YSnapshot.Create(ydoc);
        Assert.False(snapshot1.ContainsUpdate(updates[0]));
        Assert.False(snapshot2.ContainsUpdate(updates[1]));
        Assert.True(snapshot2.ContainsUpdate(updates[0]));
        Assert.True(snapshotFinal.ContainsUpdate(updates[0]));
        Assert.True(snapshotFinal.ContainsUpdate(updates[1]));
    }

    [Fact]
    [PortOf("snapshot.tests.js:testContainsUpdate2", "SNAP-10")]
    [Requires(Feature.Text)]
    public void ContainsUpdate_DeletesAppliedToARemote_ReportsTheContainedDeletes()
    {
        var local = new Doc();
        var remote = new Doc();
        local.GetText("t").Insert(0, "abcdefghij");
        local.GetText("t").Delete(0, 3);
        remote.ApplyUpdate(local.EncodeStateAsUpdate());
        var snap = YSnapshot.Create(remote);
        local.GetText("t").Delete(0, 3);
        var update = local.EncodeStateAsUpdate();
        Assert.False(snap.ContainsUpdate(update));
    }

    [Fact]
    [Covers("SNAP-10")]
    [Requires(Feature.Array, Feature.V2, Feature.UpdateFunctions, Feature.V2Functions, Feature.Events)]
    public void ContainsUpdate_V1AndV2Updates_GiveTheSameAnswer()
    {
        var doc = Net.Mk(1);
        var updates = new List<byte[]>();
        doc.Update += (u, _, _, _) => updates.Add(u.ToArray());
        var arr = doc.GetArray("a");
        var s1 = YSnapshot.Create(doc);
        arr.Insert(0, new YEntry[] { 1 });
        var s2 = YSnapshot.Create(doc);
        foreach (var s in new[] { s1, s2 })
            foreach (var update in updates)
            {
                var v2 = new ArrayBufferWriter<byte>();
                Assert.True(YUpdate.TryConvert(update, UpdateFormat.V1, UpdateFormat.V2, v2).Ok);
                Assert.Equal(s.ContainsUpdate(update), s.ContainsUpdate(v2.WrittenSpan, UpdateFormat.V2));
            }
    }

    // ------------------------------------------------------------------ SNAP-11

    [Fact]
    [Covers("SNAP-11")]
    [Requires(Feature.Text)]
    public void SnapshotReads_OnAGcDoc_LoseTheCollectedContent()
    {
        var doc = Net.Mk(1, gc: true);
        var t = doc.GetText("t");
        t.Insert(0, "abc");
        var s1 = YSnapshot.Create(doc);
        t.Delete(1, 1);
        Assert.Equal("[ac]", Delta(t, s1));                             // "b" was visible at the snapshot but its content is gone
        Assert.Equal("[ac]", Delta(t));
    }

    [Fact]
    [Covers("SNAP-11")]
    [Requires(Feature.Text)]
    public void Gc_CanBeToggledAtRuntime_AndOnlyAffectsLaterCollections()
    {
        var doc = Net.Mk(1, gc: false);
        var t = doc.GetText("t");
        t.Insert(0, "abc");
        var s1 = YSnapshot.Create(doc);
        t.Delete(1, 1);                                                // gc off: the content stays
        doc.Gc = true;
        Assert.Equal("[abc]", Delta(t, s1));
        t.Delete(0, 1);                                                // gc on: this deletion is collected, the earlier one is not (GC scans only the current delete set)
        Assert.Equal("[bc]", Delta(t, s1));
        Assert.Equal("[c]", Delta(t));
    }
}
