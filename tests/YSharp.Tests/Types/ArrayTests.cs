using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Rng;
using YSharp.TestKit.Traceability;
using static YSharp.Tests.Types.TypeTestExtensions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Types;

/// <summary>Behaviour of <see cref="YArray"/> (A-1..A-20) and the list-type tests ported from y-array.tests.js.</summary>
[Requires(Feature.Array)]
public sealed class ArrayTests
{
    private static Doc NewDoc(ulong client = 1) => new(new DocOptions { ClientId = client });

    private static int Rnd(IPrng gen, int min, int max) => (int)Prng.Int32(gen, min, max);

    // ------------------------------------------------------------------ behaviours

    [Fact, Covers("A-1")]
    public void Insert_PlacesContentBeforeTheIndex_AndAtLengthAppends()
    {
        var array = NewDoc().GetArray("a");
        array.Insert(0, new YEntry[] { 1, 2, 3 });
        array.Insert(1, new YEntry[] { "x" });
        array.Insert(array.Length, new YEntry[] { "end" });
        array.Insert(0, new YEntry[] { "first" });
        Assert.Equal("""["first",1,"x",2,3,"end"]""", array.Json());
        Assert.Equal(6, array.Length);
    }

    [Fact, Covers("A-2")]
    public void Insert_BeyondTheLength_Throws_AndChangesNothing()
    {
        var array = NewDoc().GetArray("a");
        array.Push(new YEntry[] { 1, 2 });
        Assert.ThrowsAny<Exception>(() => array.Insert(3, new YEntry[] { 9 }));
        Assert.Equal("[1,2]", array.Json());
        Assert.ThrowsAny<Exception>(() => NewDoc().GetArray("empty").Insert(1, new YEntry[] { 9 }));
    }

    [Fact, Covers("A-3", "TX-3")]
    [Requires(Feature.Events)]
    public void EmptyInsertAndPush_AreNoOps_WithoutEventOrUpdate()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        int events = 0, updates = 0, transactions = 0;
        array.Observe((_, _) => events++);
        doc.Update += (_, _, _, _) => updates++;
        doc.AfterTransaction += (_, _) => transactions++;
        array.Insert(0, ReadOnlySpan<YEntry>.Empty);
        array.Push(ReadOnlySpan<YEntry>.Empty);
        Assert.Equal(0, events);
        Assert.Equal(0, updates);
        Assert.Equal(0, array.Length);
    }

    [Fact, Covers("A-4")]
    public void Push_AppendsAfterTheLastItem_AndUnshiftInsertsAtZero()
    {
        var array = NewDoc().GetArray("a");
        array.Push(new YEntry[] { 1 });
        array.Push(new YEntry[] { 2, 3 });
        array.Unshift(new YEntry[] { 0 });
        array.Unshift(new YEntry[] { -2, -1 });
        Assert.Equal("[-2,-1,0,1,2,3]", array.Json());
    }

    [Fact, Covers("A-5")]
    [Requires(Feature.Events)]
    public void Delete_RemovesCountedElements_AndOverflowKeepsTheDeletedPart()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        array.Push(new YEntry[] { 1, 2, 3, 4, 5 });
        array.Delete(1);
        Assert.Equal("[1,3,4,5]", array.Json());
        array.Delete(0, 2);
        Assert.Equal("[4,5]", array.Json());
        array.Delete(1, 0);
        Assert.Equal("[4,5]", array.Json());
        var events = 0;
        array.Observe((_, _) => events++);
        // JS deletes what exists and then throws "Length exceeded!": the partial effect stays and the observers fire
        Assert.ThrowsAny<Exception>(() => array.Delete(1, 5));
        Assert.Equal("[4]", array.Json());
        Assert.Equal(1, events);
    }

    [Fact, Covers("A-6")]
    public void Delete_OnAnEmptyArray_Throws()
    {
        var array = NewDoc().GetArray("a");
        Assert.ThrowsAny<Exception>(() => array.Delete(0, 1));
        array.Delete(0, 0);
        Assert.ThrowsAny<Exception>(() => array.Delete(1, 1));
    }

    [Fact, Diverges("A-7", "0008")]
    public void A7_IndexOutOfRange_IndexerThrowsWhereJsReturnsUndefined()
    {
        var array = NewDoc().GetArray("a");
        array.Push(new YEntry[] { 1 });
        // JS: get(10) and get(-1) return undefined. y-sharp: the indexer throws (ADR 0008 recorded divergence).
        Assert.Throws<ArgumentOutOfRangeException>(() => array[10]);
        Assert.Throws<ArgumentOutOfRangeException>(() => array[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => array[1]);
        Assert.Equal(1, array[0].Value.AsNumber());
    }

    [Fact, PortOf("y-array.tests.js:testSlice", "A-8")]
    public void Slice_Ranges_ReturnTheElementsLikeJs()
    {
        var doc1 = NewDoc();
        var arr = doc1.GetArray("array");
        arr.Insert(0, new YEntry[] { 1, 2, 3 });
        string Slice(int start, int? end = null) => YValue.Array(arr.Slice(start, end).Select(e => e.Value).ToArray()).Json();
        Assert.Equal("[1,2,3]", Slice(0));
        Assert.Equal("[2,3]", Slice(1));
        Assert.Equal("[1,2]", Slice(0, -1));                 // a negative end counts from the end
        arr.Insert(0, new YEntry[] { 0 });
        Assert.Equal("[0,1,2,3]", Slice(0));
        Assert.Equal("[0,1]", Slice(0, 2));
        Assert.Equal("[2,3]", Slice(-2));                    // a negative start too
        Assert.Equal("[]", Slice(3, 1));                     // an empty range
        Assert.Equal("[1,2,3]", Slice(1, 4));
    }

    [Fact, Covers("A-9")]
    [Requires(Feature.Map)]
    public void NestedTypes_OnRead_AreReturnedByReference()
    {
        var array = NewDoc().GetArray("a");
        var map = new YMap();
        var inner = new YArray();
        array.Push(new YEntry[] { map, inner });
        Assert.Same(map, array[0].Type);
        Assert.Same(inner, array[1].Type);
        Assert.Same(map, array.ToArray()[0].Type);
    }

    [Fact, Covers("A-10")]
    [Requires(Feature.Text, Feature.Map)]
    public void ToJson_ConvertsNestedTypes_AndKeepsBytes()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        var map = new YMap();
        map.Set("k", 1);
        var text = new YText("hi");
        array.Push(new YEntry[] { map, YValue.Bytes(new byte[] { 7 }), YArray.From(new YEntry[] { 1 }), text });
        var json = array.ToJson();
        Assert.True(json.TryGetArray(out var items));
        Assert.Equal(4, items.Length);
        Assert.Equal(YValueKind.Object, items[0].Kind);
        Assert.Equal(YValueKind.Bytes, items[1].Kind);
        Assert.Equal(YValueKind.Array, items[2].Kind);
        Assert.Equal(YValueKind.String, items[3].Kind);
        // ToArray keeps the types un-converted
        Assert.All(new[] { 0, 2, 3 }, i => Assert.Equal(YEntryKind.Type, array.ToArray()[i].Kind));
    }

    [Fact, Covers("A-11", "A-12")]
    public void Enumeration_VisitsLiveElementsInOrder_AndSurvivesMutation()
    {
        // JS forEach/map have no counterpart by decision (A3): foreach and LINQ over the enumerator do the same.
        var array = NewDoc().GetArray("a");
        array.Push(new YEntry[] { 0, 1, 2, 3, 4 });
        array.Delete(1, 2);
        var seen = new List<double>();
        foreach (var e in array) seen.Add(e.Value.AsNumber());
        Assert.Equal(new double[] { 0, 3, 4 }, seen);
        // mutating while iterating is unspecified; it must not crash
        var count = 0;
        foreach (var _ in array)
        {
            if (count++ == 0) array.Push(new YEntry[] { 9 });
        }
        Assert.True(count >= 3);
        Assert.Equal(4, array.Length);
    }

    [Fact, Covers("A-13")]
    [Requires(Feature.Sync)]
    public void Length_StaysConsistent_AfterConcurrentInsertAndDeleteMerges()
    {
        var net = Net.Init(3);
        var (a0, a1, a2) = (net.Peers[0].Array, net.Peers[1].Array, net.Peers[2].Array);
        a0.Push(new YEntry[] { "a", "b", "c", "d", "e", "f" });
        net.Flush();
        a0.Delete(1, 2);
        a1.Insert(3, new YEntry[] { 1, 2, 3 });
        a2.Delete(0, 4);
        a2.Push(new YEntry[] { "z" });
        net.Flush();
        foreach (var p in net.Peers)
        {
            Assert.Equal(p.Array.ToArray().Length, p.Array.Length);
            Assert.Equal(a0.Json(), p.Array.Json());
        }
        net.Compare();
    }

    [Fact, PortOf("y-array.tests.js:testLengthIssue", "A-14")]
    public void SearchMarkers_LengthIssue297_StaysCorrect()
    {
        var doc1 = NewDoc();
        var arr = doc1.GetArray("array");
        arr.Push(new YEntry[] { 0, 1, 2, 3 });
        arr.Delete(0);
        arr.Insert(0, new YEntry[] { 0 });
        Assert.Equal(arr.ToArray().Length, arr.Length);
        doc1.Transact(_ =>
        {
            arr.Delete(1);
            Assert.Equal(arr.ToArray().Length, arr.Length);
            arr.Insert(1, new YEntry[] { 1 });
            Assert.Equal(arr.ToArray().Length, arr.Length);
            arr.Delete(2);
            Assert.Equal(arr.ToArray().Length, arr.Length);
            arr.Insert(2, new YEntry[] { 2 });
            Assert.Equal(arr.ToArray().Length, arr.Length);
        });
        Assert.Equal(arr.ToArray().Length, arr.Length);
        arr.Delete(1);
        Assert.Equal(arr.ToArray().Length, arr.Length);
        arr.Insert(1, new YEntry[] { 1 });
        Assert.Equal(arr.ToArray().Length, arr.Length);
    }

    [Fact, Covers("A-14")]
    public void SearchMarkers_AfterARemoteChange_AreDiscarded_EvenWhenAppliedInsideALocalTransaction()
    {
        // markers set by local index reads must not survive a remote insert ahead of them (Yjs clears them in _callObserver)
        foreach (var nested in new[] { false, true })
        {
            var local = NewDoc(1);
            var remote = NewDoc(2);
            var arr = local.GetArray("a");
            arr.Push(Enumerable.Range(0, 40).Select(i => (YEntry)i).ToArray());
            remote.ApplyUpdate(local.EncodeStateAsUpdate());
            for (var i = 0; i < 40; i += 7) _ = arr[i];   // place markers
            remote.GetArray("a").Insert(0, new YEntry[] { -1, -2 });
            var update = remote.EncodeStateAsUpdate();
            if (nested) local.Transact(_ => local.ApplyUpdate(update));
            else local.ApplyUpdate(update);

            var expected = arr.ToArray();
            Assert.Equal(42, expected.Length);
            for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i].Value, arr[i].Value);
            arr.Insert(20, new YEntry[] { 99 });
            Assert.Equal(99, arr.ToArray()[20].Value.AsNumber());
        }
    }

    [Fact, PortOf("y-array.tests.js:testLengthIssue2", "A-14", "A-2")]
    public void SearchMarkers_LengthIssue314_StaysCorrect()
    {
        var doc = NewDoc();
        var next = doc.GetArray();
        doc.Transact(_ => next.Insert(0, new YEntry[] { "group2" }));
        doc.Transact(_ => next.Insert(1, new YEntry[] { "rectangle3" }));
        doc.Transact(_ =>
        {
            next.Delete(0);
            next.Insert(0, new YEntry[] { "rectangle3" });
        });
        next.Delete(1);
        doc.Transact(_ => next.Insert(1, new YEntry[] { "ellipse4" }));
        doc.Transact(_ => next.Insert(2, new YEntry[] { "ellipse3" }));
        doc.Transact(_ => next.Insert(3, new YEntry[] { "ellipse2" }));
        doc.Transact(_ =>
        {
            doc.Transact(_ =>
            {
                Assert.ThrowsAny<Exception>(() => next.Insert(5, new YEntry[] { "rectangle2" }));
                next.Insert(4, new YEntry[] { "rectangle2" });
            });
            // this should not throw an error message
            doc.Transact(_ => next.Delete(4));
        });
        Assert.Equal(4, next.Length);
    }

    [Fact, Covers("A-14")]
    public void IndexedOperations_RandomSequences_MatchANaiveListModel()
    {
        var gen = Prng.Create(2024);
        var doc = NewDoc();
        var array = doc.GetArray("a");
        var model = new List<int>();
        for (var step = 0; step < 400; step++)
        {
            var op = Rnd(gen, 0, 5);
            var inTx = Prng.Bool(gen);
            void Run(Action body)
            {
                if (inTx) doc.Transact(_ => body());
                else body();
            }
            if (op <= 2 || model.Count == 0)
            {
                var pos = Rnd(gen, 0, model.Count);
                var n = Rnd(gen, 1, 4);
                var items = Enumerable.Range(0, n).Select(i => step * 10 + i).ToArray();
                Run(() => array.Insert(pos, items.Select(i => (YEntry)i).ToArray()));
                model.InsertRange(pos, items);
            }
            else
            {
                var pos = Rnd(gen, 0, model.Count - 1);
                var len = Rnd(gen, 1, Math.Min(3, model.Count - pos));
                Run(() => array.Delete(pos, len));
                model.RemoveRange(pos, len);
            }
            Assert.Equal(model.Count, array.Length);
            if (step % 25 == 0) Assert.Equal(model, array.ToArray().Select(e => (int)e.Value.AsNumber()).ToList());
        }
        Assert.Equal(model, array.ToArray().Select(e => (int)e.Value.AsNumber()).ToList());
    }

    [Fact, Covers("A-15")]
    [Requires(Feature.Sync)]
    public void BinaryContent_IsCopiedOnInsert_AndReadsBackAsBinary()
    {
        var net = Net.Init(2);
        var source = new byte[] { 1, 2, 3 };
        net.Peers[0].Array.Push(new YEntry[] { YValue.Bytes(source), 5 });
        source[1] = 42;
        net.Flush();
        foreach (var p in net.Peers)
        {
            Assert.Equal(YEntryKind.Binary, p.Array[0].Kind);
            Assert.True(p.Array[0].Value.TryGetBytes(out var b));
            Assert.True(b.SequenceEqual(new byte[] { 1, 2, 3 }));
            Assert.Equal(YEntryKind.Any, p.Array[1].Kind);
        }
        net.Compare();
    }

    [Fact, Covers("A-16")]
    [Requires(Feature.Sync)]
    public void MixedRuns_SplitCorrectly_WhenAMiddleElementIsDeleted()
    {
        var net = Net.Init(2);
        var a0 = net.Peers[0].Array;
        a0.Insert(0, new YEntry[] { 1, "a", Obj(("x", 1)), YValue.Array(new YValue[] { 2 }) });
        Assert.Equal(4, a0.Length);
        a0.Delete(1, 1);
        a0.Insert(1, new YEntry[] { "b" });
        a0.Delete(2, 1);
        Assert.Equal("""[1,"b",[2]]""", a0.Json());
        net.Flush();
        Assert.Equal("""[1,"b",[2]]""", net.Peers[1].Array.Json());
        net.Compare();
    }

    [Fact, Covers("A-17")]
    public void Indices_DeletedElements_CountOnlyLiveElements()
    {
        var array = NewDoc().GetArray("a");
        array.Push(new YEntry[] { "a", "b", "c", "d" });
        array.Delete(1, 2);
        Assert.Equal(2, array.Length);
        array.Insert(1, new YEntry[] { "x" });
        array.Insert(3, new YEntry[] { "y" });
        Assert.Equal("""["a","x","d","y"]""", array.Json());
        Assert.ThrowsAny<Exception>(() => array.Insert(5, new YEntry[] { "z" }));
    }

    [Fact, Covers("A-18")]
    [Requires(Feature.Sync)]
    public void ConcurrentInserts_AtTheSameIndex_ConvergeToTheSameOrder()
    {
        var net = Net.Init(3);
        var (a0, a1, a2) = (net.Peers[0].Array, net.Peers[1].Array, net.Peers[2].Array);
        a0.Push(new YEntry[] { "x", "y" });
        net.Flush();
        a0.Insert(1, new YEntry[] { "from-0" });
        a1.Insert(1, new YEntry[] { "from-1" });
        a2.Insert(1, new YEntry[] { "from-2" });
        net.Flush();
        var json = a0.Json();
        Assert.Equal(json, a1.Json());
        Assert.Equal(json, a2.Json());
        // the lowest client id's insertion comes first: ids are 0, 1, 2
        Assert.Equal("""["x","from-0","from-1","from-2","y"]""", json);
        net.Compare();
    }

    [Fact, PortOf("y-array.tests.js:testFailsObjectManipulationInDevMode", "A-19")]
    [Requires(Feature.Map)]
    public void InsertedObjectsAndArrays_OnInsert_AreStoredByValue()
    {
        // JS freezes the inserted array and object in dev mode (the test only runs there). y-sharp values are immutable and the
        // document keeps its own copy: there is nothing to freeze, and a later change to the caller's data cannot leak in.
        var doc = NewDoc();
        var items = new YValue[] { 1, 2, 3 };
        var a = YValue.Array(items);
        var b = Obj(("o", 1));
        doc.GetArray("test").Insert(0, new YEntry[] { a });
        doc.GetMap("map").Set("k", b);
        items[0] = 42;
        Assert.Equal("[[1,2,3]]", doc.GetArray("test").Json());
        Assert.Equal("""{"k":{"o":1}}""", doc.GetMap("map").Json());
        // an inserted array value is an Any element, not a YArray
        Assert.Equal(YEntryKind.Any, doc.GetArray("test")[0].Kind);
    }

    [Fact, Covers("A-20")]
    public void NegativeIndices_Throw_AndFractionalOrNanIndicesDoNotCompile()
    {
        var array = NewDoc().GetArray("a");
        array.Push(new YEntry[] { 1, 2 });
        Assert.Throws<ArgumentOutOfRangeException>(() => array.Insert(-1, new YEntry[] { 9 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => array.Delete(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => array.Delete(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => array[-1]);
        Assert.Equal("[1,2]", array.Json());
    }

    // ------------------------------------------------------------------ ports

    [Fact, PortOf("y-array.tests.js:testBasicUpdate", "A-1")]
    public void Array_InsertThenApplyUpdate_TransfersTheArray()
    {
        var doc1 = NewDoc(1);
        var doc2 = NewDoc(2);
        doc1.GetArray("array").Insert(0, new YEntry[] { "hi" });
        var update = doc1.EncodeStateAsUpdate();
        doc2.ApplyUpdate(update);
        Assert.Equal("""["hi"]""", doc2.GetArray("array").Json());
    }

    [Fact, PortOf("y-array.tests.js:testDeleteInsert", "A-5", "A-6")]
    [Requires(Feature.Sync)]
    public void Array_DeleteOutOfRangeThenInsert_ThrowsAndStaysUsable()
    {
        var net = Net.Init(2);
        var array0 = net.Peers[0].Array;
        array0.Delete(0, 0);
        Assert.ThrowsAny<Exception>(() => array0.Delete(1, 1));
        array0.Insert(0, new YEntry[] { "A" });
        array0.Delete(1, 0);
        net.Compare();
    }

    [Fact, PortOf("y-array.tests.js:testInsertThreeElementsTryRegetProperty", "A-10")]
    [Requires(Feature.Sync)]
    public void Array_InsertThreeValues_ReadBackLocallyAndOnTheRemote()
    {
        var net = Net.Init(2);
        net.Peers[0].Array.Insert(0, new YEntry[] { 1, true, false });
        Assert.Equal("[1,true,false]", net.Peers[0].Array.Json());
        net.Flush();
        Assert.Equal("[1,true,false]", net.Peers[1].Array.Json());
        net.Compare();
    }

    [Fact, PortOf("y-array.tests.js:testConcurrentInsertWithThreeConflicts", "A-18")]
    [Requires(Feature.Sync)]
    public void Array_ThreeConcurrentInserts_ConvergeToTheSameOrder()
    {
        var net = Net.Init(3);
        net.Peers[0].Array.Insert(0, new YEntry[] { 0 });
        net.Peers[1].Array.Insert(0, new YEntry[] { 1 });
        net.Peers[2].Array.Insert(0, new YEntry[] { 2 });
        net.Compare();
    }

    [Fact, PortOf("y-array.tests.js:testConcurrentInsertDeleteWithThreeConflicts", "A-18")]
    [Requires(Feature.Sync)]
    public void Array_ThreeConcurrentInsertsAndDeletes_Converge()
    {
        var net = Net.Init(3);
        var (array0, array1, array2) = (net.Peers[0].Array, net.Peers[1].Array, net.Peers[2].Array);
        array0.Insert(0, new YEntry[] { "x", "y", "z" });
        net.Flush();
        array0.Insert(1, new YEntry[] { 0 });
        array1.Delete(0);
        array1.Delete(1, 1);
        array2.Insert(1, new YEntry[] { 2 });
        net.Compare();
    }

    [Fact, PortOf("y-array.tests.js:testInsertionsInLateSync", "A-18")]
    [Requires(Feature.Sync)]
    public void Array_InsertionsMadeWhileDisconnected_ConvergeOnLateSync()
    {
        var net = Net.Init(3);
        var (array0, array1, array2) = (net.Peers[0].Array, net.Peers[1].Array, net.Peers[2].Array);
        array0.Insert(0, new YEntry[] { "x", "y" });
        net.Flush();
        net.Users[1].Disconnect();
        net.Users[2].Disconnect();
        array0.Insert(1, new YEntry[] { "user0" });
        array1.Insert(1, new YEntry[] { "user1" });
        array2.Insert(1, new YEntry[] { "user2" });
        net.Users[1].Connect();
        net.Users[2].Connect();
        net.Flush();
        net.Compare();
    }

    [Fact, PortOf("y-array.tests.js:testDisconnectReallyPreventsSendingMessages", "A-13")]
    [Requires(Feature.Sync)]
    public void Array_DisconnectedPeer_ReceivesNothingUntilReconnected()
    {
        var net = Net.Init(3);
        var (array0, array1) = (net.Peers[0].Array, net.Peers[1].Array);
        array0.Insert(0, new YEntry[] { "x", "y" });
        net.Flush();
        net.Users[1].Disconnect();
        net.Users[2].Disconnect();
        array0.Insert(1, new YEntry[] { "user0" });
        array1.Insert(1, new YEntry[] { "user1" });
        Assert.Equal("""["x","user0","y"]""", array0.Json());
        Assert.Equal("""["x","user1","y"]""", array1.Json());
        net.Users[1].Connect();
        net.Users[2].Connect();
        net.Compare();
    }

    [Fact, PortOf("y-array.tests.js:testDeletionsInLateSync", "A-13")]
    [Requires(Feature.Sync)]
    public void Array_DeletionsMadeWhileDisconnected_ConvergeOnLateSync()
    {
        var net = Net.Init(2);
        var (array0, array1) = (net.Peers[0].Array, net.Peers[1].Array);
        array0.Insert(0, new YEntry[] { "x", "y" });
        net.Flush();
        net.Users[1].Disconnect();
        array1.Delete(1, 1);
        array0.Delete(0, 2);
        net.Users[1].Connect();
        net.Compare();
    }

    [Fact, PortOf("y-array.tests.js:testInsertThenMergeDeleteOnSync", "A-13")]
    [Requires(Feature.Sync)]
    public void Array_InsertThenDeleteWhileDisconnected_MergeOnSync()
    {
        var net = Net.Init(2);
        var (array0, array1) = (net.Peers[0].Array, net.Peers[1].Array);
        array0.Insert(0, new YEntry[] { "x", "y", "z" });
        net.Flush();
        net.Users[0].Disconnect();
        array1.Delete(0, 3);
        net.Users[0].Connect();
        net.Compare();
    }

    [Fact, PortOf("y-array.tests.js:testGarbageCollector", "A-13")]
    [Requires(Feature.Sync)]
    public void Array_DeletedItemsWithGc_AreCollectedAndStillConverge()
    {
        var net = Net.Init(3);
        var array0 = net.Peers[0].Array;
        array0.Insert(0, new YEntry[] { "x", "y", "z" });
        net.Flush();
        net.Users[0].Disconnect();
        array0.Delete(0, 3);
        net.Users[0].Connect();
        net.Flush();
        net.Compare();
    }

    [Fact, PortOf("y-array.tests.js:testIteratingArrayContainingTypes", "A-12")]
    [Requires(Feature.Map)]
    public void Array_IteratingNestedTypes_VisitsEveryElementInOrder()
    {
        var y = NewDoc();
        var arr = y.GetArray("arr");
        const int numItems = 10;
        for (var i = 0; i < numItems; i++)
        {
            var map = new YMap();
            map.Set("value", i);
            arr.Push(new YEntry[] { map });
        }
        var cnt = 0;
        foreach (var item in arr)
        {
            Assert.Equal(cnt++, (int)((YMap)item.Type!).Get("value").AsNumber());
        }
        Assert.Equal(numItems, cnt);
        y.Destroy();
    }
}
