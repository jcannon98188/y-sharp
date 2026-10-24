using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Time;
using YSharp.TestKit.Traceability;
using YSharp.Tests.Structs;
using static YSharp.Tests.DocFeatures.Ds;

namespace YSharp.Tests.DocFeatures;

/// <summary>
/// UndoManager behaviour (UM-01..UM-20, docs/research/doc-features.md section 3) beyond the upstream ports. Expected values come from running the
/// same operations on the pinned Yjs; the oracle fixtures under <c>fixtures/undo</c> replay the same behaviour end to end.
/// </summary>
public sealed class UndoManagerTests
{
    private static void Wait(VirtualTimeProvider clock, int ms) => clock.Advance(TimeSpan.FromMilliseconds(ms));

    // ------------------------------------------------------------------ UM-01 tracked origins

    [Fact]
    [Covers("UM-01")]
    public void TrackedOrigins_TheCallersSetIsMutated_ItGainsTheManagerAndLosesItOnDispose() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var set = Origins("x");
        var um = Um(doc.GetText("t"), trackedOrigins: set);
        Assert.Contains(um, set);
        Assert.Equal(2, set.Count);
        um.Dispose();
        Assert.DoesNotContain(um, set);
        Assert.Single(set);
    });

    [Fact]
    [Covers("UM-01")]
    public void TrackedOrigins_DefaultToNull_SoRemoteUpdatesWithoutAnOriginAreCapturedToo() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var b = Net.Mk(2);
        var ta = a.GetText("t");
        var um = Um(ta);
        ta.Insert(0, "abc");
        Net.Sync(a, b);
        b.GetText("t").Insert(1, "X");
        b.GetText("t").Insert(1, "Y");
        a.ApplyUpdate(b.EncodeStateAsUpdate(Net.StateVector(a)));   // origin null: tracked by default
        Assert.Equal("aYXbc", ta.ToString());
        Assert.Single(um.UndoStack);
        um.Undo();
        Assert.Equal("", ta.ToString());                           // the remote characters were part of the captured change
    });

    [Fact]
    [Covers("UM-01", "UM-20")]
    public void UndoManager_RemoteEditsInsideAnUndoneRangeWithUntrackedOrigin_Remain() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var b = Net.Mk(2);
        var ta = a.GetText("t");
        var tb = b.GetText("t");
        var um = Um(ta);
        ta.Insert(0, "abc");
        Net.Sync(a, b);
        tb.Insert(1, "X");
        Net.Sync(b, a);                                            // Net.Sync applies with origin "sync": not tracked
        Assert.Equal("aXbc", ta.ToString());
        um.Undo();
        Assert.Equal("X", ta.ToString());
        um.Redo();
        Assert.Equal("aXbc", ta.ToString());
    });

    [Fact]
    [Covers("UM-01", "UM-16")]
    public void TrackedOrigins_ChangedAtRuntime_TakeEffect() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        var um = Um(t, trackedOrigins: Origins("x"));
        doc.Transact(_ => t.Insert(0, "a"), "y");
        doc.Transact(_ => t.Insert(1, "b"), "x");
        Assert.Single(um.UndoStack);                               // "y" was not tracked
        um.AddTrackedOrigin("y");
        doc.Transact(_ => t.Insert(2, "c"), "y");
        Assert.Single(um.UndoStack);                               // tracked now, merged into the open stack item
        um.RemoveTrackedOrigin("y");
        um.StopCapturing();
        doc.Transact(_ => t.Insert(3, "d"), "y");
        Assert.Single(um.UndoStack);                               // untracked again
    });

    // ------------------------------------------------------------------ UM-02 scope

    [Fact]
    [Covers("UM-02")]
    public void Scope_ChangesToANestedTypeCountForAnAncestorScope_AndUnrelatedRootsDoNot() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var arr = doc.GetArray("a");
        var other = doc.GetArray("other");
        var um = Um(arr);
        arr.Push(new YEntry[] { new YMap() });
        um.StopCapturing();
        ((YMap)arr[0].Type!).Set("k", 1);
        Assert.Equal(2, um.UndoStack.Count);
        other.Push(new YEntry[] { 1 });
        Assert.Equal(2, um.UndoStack.Count);
    });

    [Fact]
    [Covers("UM-02")]
    public void CaptureTransaction_Predicate_CanRejectATransaction() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var arr = doc.GetArray("a");
        var um = new UndoManager(arr, Opts(captureTransaction: tx => !Equals(tx.Origin, "skip")));
        arr.Push(new YEntry[] { 1 });
        um.StopCapturing();
        doc.Transact(_ => arr.Push(new YEntry[] { 2 }), "skip");
        doc.Transact(_ => arr.Push(new YEntry[] { 3 }));
        Assert.Equal(2, um.UndoStack.Count);
    });

    // ------------------------------------------------------------------ UM-03 capture merging (virtual clock)

    [Fact]
    [Covers("UM-03")]
    public void CaptureTimeout_MergesChangesCloserThanTheTimeout_AndStopCapturingStartsANewItem() => Impl.Run(() =>
    {
        var clock = new VirtualTimeProvider();
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        var um = Um(t, clock);
        t.Insert(0, "a");
        Wait(clock, 100);
        t.Insert(1, "b");
        Wait(clock, 499);
        t.Insert(2, "c");                                          // 499 ms after the last change: still merged
        Assert.Single(um.UndoStack);
        Wait(clock, 500);
        t.Insert(3, "d");                                          // exactly the timeout: a new item
        Assert.Equal(2, um.UndoStack.Count);
        um.StopCapturing();
        t.Insert(4, "e");
        Assert.Equal(3, um.UndoStack.Count);
        um.Undo();
        Assert.Equal("abcd", t.ToString());
        um.Undo();
        Assert.Equal("abc", t.ToString());
        um.Undo();
        Assert.Equal("", t.ToString());
    });

    [Fact]
    [Covers("UM-03")]
    public void CaptureTimeout_Zero_NeverMerges() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var arr = doc.GetArray("a");
        var um = Um(arr, captureTimeoutMs: 0);
        arr.Push(new YEntry[] { 1 });
        arr.Push(new YEntry[] { 2 });
        arr.Push(new YEntry[] { 3 });
        Assert.Equal(3, um.UndoStack.Count);
    });

    [Fact]
    [Covers("UM-03")]
    public void UndoThenRedo_AnyItem_ReturnsTheItemToTheUndoStack() => Impl.Run(() =>
    {
        var clock = new VirtualTimeProvider();
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        var um = Um(t, clock);
        t.Insert(0, "a");
        Wait(clock, 600);
        t.Insert(1, "b");
        um.Undo();
        um.Redo();
        Assert.Equal("ab", t.ToString());
        Assert.Equal(2, um.UndoStack.Count);
    });

    // ------------------------------------------------------------------ UM-04 events

    [Fact]
    [Covers("UM-04")]
    public void StackItemEvents_DistinguishNewItemsFromMergedOnes_AndCarryOriginAndChangedTypes() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        var um = Um(t);
        var log = new List<string>();
        um.StackItemAdded += e =>
        {
            Assert.Contains(t, e.ChangedParentTypes);
            log.Add($"added:{(e.IsRedo ? "redo" : "undo")}:{e.Origin ?? "null"}");
        };
        um.StackItemUpdated += e => log.Add($"updated:{(e.IsRedo ? "redo" : "undo")}");
        um.StackItemPopped += e => log.Add($"popped:{(e.IsRedo ? "redo" : "undo")}:{ReferenceEquals(e.Origin, um)}");
        doc.Transact(_ => t.Insert(0, "a"), null);
        t.Insert(1, "b");
        um.Undo();
        um.Redo();
        Assert.Equal(6, log.Count);
        Assert.Equal("added:undo:null", log[0]);
        Assert.Equal("updated:undo", log[1]);
        Assert.StartsWith("added:redo:", log[2]);        // the redo stack is filled while undoing, and the undo transaction's origin is the manager
        Assert.Equal("popped:undo:True", log[3]);
        Assert.StartsWith("added:undo:", log[4]);
        Assert.Equal("popped:redo:True", log[5]);
    });

    // ------------------------------------------------------------------ UM-05 redo stack

    [Fact]
    [Covers("UM-05")]
    public void ATrackedUserChange_ClearsTheRedoStack_AndUndoSeparatesConsecutiveItems() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        var um = Um(t, captureTimeoutMs: 0);
        t.Insert(0, "a");
        t.Insert(1, "b");
        um.Undo();
        Assert.Single(um.UndoStack);
        Assert.Single(um.RedoStack);
        t.Insert(1, "c");
        Assert.Equal(2, um.UndoStack.Count);
        Assert.Empty(um.RedoStack);
        Assert.False(um.CanRedo);
    });

    [Fact]
    [Covers("UM-05")]
    public void Undo_StopsCapturing_SoTheNextChangeAfterAnUndoIsAFreshItem() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        var um = Um(t);                                            // default timeout, virtual clock never advances
        t.Insert(0, "a");
        t.Insert(1, "b");
        Assert.Single(um.UndoStack);
        um.Undo();
        t.Insert(0, "c");
        t.Insert(1, "d");
        Assert.Single(um.UndoStack);
        um.StopCapturing();
        t.Insert(2, "e");
        Assert.Equal(2, um.UndoStack.Count);
    });

    // ------------------------------------------------------------------ UM-06, UM-07

    [Fact]
    [Covers("UM-06")]
    public void StackItem_RecordsInsertionsAndDeletionsAsDeleteSets_AndKeepsCallerMeta() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        var um = Um(t);
        t.Insert(0, "ab");
        t.Insert(2, "cd");
        t.Delete(0, 1);
        var item = Assert.Single(um.UndoStack);
        foreach (var clock in new long[] { 0, 1, 2, 3 }) Assert.True(item.Insertions.Contains(new ItemId(1, clock)));
        Assert.False(item.Insertions.Contains(new ItemId(1, 4)));
        Assert.True(item.Deletions.Contains(new ItemId(1, 0)));
        Assert.False(item.Deletions.Contains(new ItemId(1, 1)));
        item.Meta["owner"] = "me";
        Assert.Equal("me", um.UndoStack[0].Meta["owner"]);
    });

    [Fact]
    [Covers("UM-07")]
    public void DeletedItemsInScope_AreKeptFromGc_UntilClearIsCalled() => Impl.Run(() =>
    {
        var doc = Net.Mk(1, gc: true);
        var t = doc.GetText("t");
        var um = Um(t);
        t.Insert(0, "abc");
        t.Delete(0, 3);
        var kept = StructsProbe.Of(doc, 1).Single();
        Assert.True(kept.IsItem && kept.Deleted && kept.Keep);     // not collected despite gc: the undo manager may need the content
        Assert.Equal(4, kept.Ref);                                  // still ContentString
        um.Clear();
        var after = StructsProbe.Of(doc, 1).Single();
        Assert.False(after.Keep);                                   // un-kept, but only a later deletion touching it will collect it (GC-02)
        Assert.Equal(4, after.Ref);
    });

    [Fact]
    [Covers("UM-07")]
    public void Keep_DeletedNestedTypes_AlsoCoversTheirParents() => Impl.Run(() =>
    {
        var doc = Net.Mk(1, gc: true);
        var arr = doc.GetArray("a");
        var um = Um(arr);
        var map = new YMap();
        arr.Push(new YEntry[] { map });
        map.Set("k", "v");
        arr.Delete(0, 1);
        Assert.All(StructsProbe.Of(doc, 1), s => Assert.True(s.Keep && s.Deleted));
        um.Undo();
        Assert.Equal("[]", J(arr));                                  // inserted and deleted inside one stack item: nothing to restore
    });

    // ------------------------------------------------------------------ UM-08

    [Fact]
    [Covers("UM-08")]
    public void UndoRedo_ReturnTheStackItemOrNull_AndCanUndoCanRedoFollowTheStacks() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        var um = Um(t);
        Assert.Null(um.Undo());
        Assert.Null(um.Redo());
        Assert.False(um.CanUndo);
        Assert.False(um.CanRedo);
        t.Insert(0, "a");
        Assert.True(um.CanUndo);
        Assert.False(um.CanRedo);
        var popped = um.Undo();
        Assert.NotNull(popped);
        Assert.False(um.CanUndo);
        Assert.True(um.CanRedo);
        Assert.NotNull(um.Redo());
        Assert.Equal("a", t.ToString());
    });

    [Fact]
    [Covers("UM-08")]
    public void UndoTransaction_AnyUndo_HasTheManagerAsItsOrigin() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        var um = Um(t);
        object? seen = null;
        t.Insert(0, "a");
        t.Observe((_, tx) => seen = tx.Origin);
        um.Undo();
        Assert.Same(um, seen);
    });

    // ------------------------------------------------------------------ UM-11

    [Fact]
    [Covers("UM-11")]
    public void RedoBookkeeping_AfterRedo_IsNotSentOverTheWire() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var b = Net.Mk(2);
        var ta = a.GetText("t");
        ta.Insert(0, "abc");
        Net.Sync(a, b);
        var um = Um(ta);
        ta.Delete(1, 1);
        um.Undo();
        um.Redo();
        um.Undo();
        Net.Both(a, b);
        Assert.Equal("abc", b.GetText("t").ToString());
        HexAssert.Equal(a.EncodeStateAsUpdate(), b.EncodeStateAsUpdate(), "replicas encode identically; redone and keep are runtime-only");
    });

    // ------------------------------------------------------------------ UM-12

    [Fact]
    [Covers("UM-12")]
    public void RedoingAMapEntry_IsANoOpWhenARemoteValueIsNewer_UnlessRemoteChangesAreIgnored() => Impl.Run(() =>
    {
        foreach (var (ignore, expected, popped) in new[] { (true, 2.0, true), (false, 4.0, false) })
        {
            var doc = new Doc();
            var doc2 = new Doc();
            doc.Update += (u, _, _, _) => doc2.ApplyUpdate(u, doc);
            doc2.Update += (u, _, _, _) => doc.ApplyUpdate(u, doc2);
            var map1 = doc.GetMap();
            var map2 = doc2.GetMap();
            var um1 = Um(map1, ignoreRemoteMapChanges: ignore);
            map1.Set("x", 1);
            map2.Set("x", 2);
            map1.Set("x", 3);
            map2.Set("x", 4);
            var result = um1.Undo();
            Assert.Equal(popped, result != null);
            Assert.Equal(expected, Get(map1, "x").Value.AsNumber());
            Assert.Equal(expected, Get(map2, "x").Value.AsNumber());
        }
    });

    // ------------------------------------------------------------------ UM-13

    [Fact]
    [Covers("UM-13")]
    public void PositionCaches_UndoAndRedo_AreInvalidated() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var arr = doc.GetArray("a");
        var um = Um(arr);
        for (var i = 0; i < 100; i++) arr.Push(new YEntry[] { i });
        Assert.Equal(10, arr[10].Value.AsNumber());                  // random access builds search markers
        Assert.Equal(50, arr[50].Value.AsNumber());
        Assert.Equal(90, arr[90].Value.AsNumber());
        um.Undo();
        Assert.Equal(0, arr.Length);
        um.Redo();
        Assert.Equal(100, arr.Length);
        Assert.Equal(0, arr[0].Value.AsNumber());
        Assert.Equal(50, arr[50].Value.AsNumber());
        Assert.Equal(99, arr[99].Value.AsNumber());
    });

    // ------------------------------------------------------------------ UM-14

    [Fact]
    [Covers("UM-14")]
    public void PoppedEvent_FiresAfterTheTransaction_WithTheManagerAsOriginAndTheStackItemMeta() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        var um = Um(t);
        var order = new List<string>();
        t.Observe((_, tx) => { if (ReferenceEquals(tx.Origin, um)) order.Add("observer"); });
        um.StackItemAdded += e => e.Item.Meta["tag"] = "m";
        StackItemEvent popped = default;
        um.StackItemPopped += e => { order.Add("popped"); popped = e; };
        t.Insert(0, "a");
        um.Undo();
        Assert.Equal(new[] { "observer", "popped" }, order);
        Assert.Equal("m", popped.Item.Meta["tag"]);
        Assert.Contains(t, popped.ChangedParentTypes);
        Assert.Same(um, popped.Origin);
        Assert.False(popped.IsRedo);
    });

    // ------------------------------------------------------------------ UM-15

    [Fact]
    [Covers("UM-15")]
    public void Clear_ActsOnlyOnNonEmptyStacks_AndReportsTheRequestedFlags() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        var um = Um(t, captureTimeoutMs: 0);
        var cleared = new List<(bool, bool)>();
        um.StackCleared += (u, r) => cleared.Add((u, r));
        um.Clear();                                                  // both stacks empty: nothing happens
        Assert.Empty(cleared);
        t.Insert(0, "a");
        t.Insert(1, "b");
        um.Undo();
        um.Clear(undo: true, redo: false);
        Assert.Equal(new[] { (true, false) }, cleared);
        Assert.Empty(um.UndoStack);
        Assert.Single(um.RedoStack);
        um.Clear(undo: false, redo: true);
        Assert.Equal(new[] { (true, false), (false, true) }, cleared);
        Assert.Empty(um.RedoStack);
        um.Clear(undo: false, redo: true);                           // redo stack already empty
        Assert.Equal(2, cleared.Count);
    });

    // ------------------------------------------------------------------ UM-16

    [Fact]
    [Covers("UM-16")]
    public void AddToScope_SameTypeTwice_DedupesByIdentity() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        var um = Um(t);
        um.AddToScope(t);
        um.AddToScope(t);
        t.Insert(0, "a");
        Assert.Single(um.UndoStack);
        var um2 = Um(doc.GetArray("a"));
        um2.AddToScope(t);
        t.Insert(1, "b");
        Assert.Single(um2.UndoStack);
    });

    [Fact]
    [Covers("UM-16")]
    public void AddToScope_ATypeOfAnotherDoc_IsAcceptedButItsChangesAreNeverSeen() => Impl.Run(() =>
    {
        var d1 = Net.Mk(1);
        var d2 = Net.Mk(2);
        var um = Um(d1.GetArray("a"));
        um.AddToScope(d2.GetArray("b"));                             // JS logs [yjs#509] Not same Y.Doc and adds it anyway
        d1.GetArray("a").Push(new YEntry[] { 1 });
        d2.GetArray("b").Push(new YEntry[] { 2 });
        Assert.Single(um.UndoStack);
    });

    // ------------------------------------------------------------------ UM-17

    [Fact]
    [Covers("UM-17")]
    public void Dispose_AfterwardsEdits_AreNoLongerCaptured() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        var um = Um(t, trackedOrigins: Origins("x"));
        doc.Transact(_ => t.Insert(0, "a"), "x");
        um.Dispose();
        doc.Transact(_ => t.Insert(1, "b"), "x");
        Assert.Single(um.UndoStack);
    });

    [Fact]
    [Covers("UM-17")]
    public void Doc_Destroyed_DestroysTheManagerToo() => Impl.Run(() =>
    {
        var doc = Net.Mk(1);
        var t = doc.GetText("t");
        var set = Origins("x");
        var um = Um(t, trackedOrigins: set);
        doc.Destroy();
        Assert.DoesNotContain(um, set);
    });

    // ------------------------------------------------------------------ UM-18, UM-20

    [Fact]
    [Covers("UM-18")]
    public void Undo_DeletedNestedType_RecreatesItWithItsContent() => Impl.Run(() =>
    {
        var doc = new Doc(new DocOptions { Gc = false });
        var arr = doc.GetArray("a");
        var um = Um(arr, captureTimeoutMs: 0);
        var map = new YMap();
        map.Set("k", "v");
        arr.Push(new YEntry[] { map });
        arr.Delete(0, 1);
        Assert.Equal("[]", J(arr));
        um.Undo();
        Assert.Equal("[{\"k\":\"v\"}]", J(arr));
        um.Redo();
        Assert.Equal("[]", J(arr));
    });

    [Fact]
    [Covers("UM-20", "UM-10")]
    public void Undo_RemoteInsertionsInsideTheRange_DeletesOnlyTheCapturedOnes() => Impl.Run(() =>
    {
        var u = Users.Init(2);
        var t0 = u.Text(0);
        var t1 = u.Text(1);
        var um = Um(t0);
        t0.Insert(0, "abc");
        u.SyncAll();
        t1.Insert(1, "X");
        u.SyncAll();
        um.Undo();
        u.SyncAll();
        Assert.Equal("X", t0.ToString());
        Assert.Equal("X", t1.ToString());
    });
}
