using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Structs;

/// <summary>
/// Item.getMissing and Item.integrate, the YATA conflict resolution (docs/research/structs.md sections C and D).
/// Expected strings come from running the same operations on the pinned Yjs.
/// </summary>
[Requires(Feature.Integrate)]
public sealed class IntegrationTests
{
    private static string Text(Doc d) => d.GetText("t").ToString();

    // ------------------------------------------------------------------ C: dependencies

    private static (byte[] U1, byte[] U2, byte[] U3) TwoDependencies()
    {
        var a = Net.Mk(1);
        var b = Net.Mk(2);
        var c = Net.Mk(3);
        var ua = Net.Capture(a);
        var ub = Net.Capture(b);
        var uc = Net.Capture(c);
        a.GetText("t").Insert(0, "A");
        c.GetText("t").Insert(0, "C");
        Net.Sync(a, b);
        Net.Sync(c, b);
        b.GetText("t").Insert(1, "B");      // origin from client 1, right origin from client 3
        return (ua[0], ub[^1], uc[0]);
    }

    [Fact]
    [Covers("STR-13")]
    [Requires(Feature.Text, Feature.Events)]
    public void MissingDependency_IsFoundInOrderOrigin_ThenRightOrigin()
    {
        var (u1, u2, u3) = TwoDependencies();

        var d = Net.Mk(10);
        d.ApplyUpdate(u2);
        Assert.Equal("", Text(d));
        d.ApplyUpdate(u1);                  // the origin's client is now known, the right origin's client is still missing
        Assert.Equal("A", Text(d));
        Assert.Equal(1L, d.GetStateVector()[1]);
        Assert.Equal(0L, d.GetStateVector()[2]);
        d.ApplyUpdate(u3);
        Assert.Equal("ABC", Text(d));

        var e = Net.Mk(11);
        e.ApplyUpdate(u2);
        e.ApplyUpdate(u3);                  // right origin known, origin still missing
        Assert.Equal("C", Text(e));
        Assert.Equal(0L, e.GetStateVector()[2]);
        e.ApplyUpdate(u1);
        Assert.Equal("ABC", Text(e));
    }

    [Fact]
    [Covers("STR-14")]
    [Requires(Feature.Text)]
    public void ResolvingTheOrigin_OriginInsideAStruct_SplitsThatStruct()
    {
        var a = Net.Mk(1, gc: false);
        var b = Net.Mk(2, gc: false);
        a.GetText("t").Insert(0, "abc");
        Net.Sync(a, b);
        b.GetText("t").Insert(1, "X");

        Net.Sync(b, a);                     // a must cut its "abc" after the origin and before the right origin

        var mine = StructsProbe.Of(a, 1);
        Assert.Equal(new[] { (0L, 1L), (1L, 2L) }, mine.Select(s => (s.Clock, s.Len)));
        var x = Assert.Single(StructsProbe.Of(a, 2));
        Assert.Equal(((ulong, long)?)(1UL, 0L), x.Origin);
        Assert.Equal(((ulong, long)?)(1UL, 1L), x.RightOrigin);
        Assert.Equal("aXbc", Text(a));
    }

    [Fact]
    [Covers("STR-14")]
    [Requires(Feature.Text)]
    public void ResolvingTheOrigin_BeforeTheRightOriginSplitsTheSameStruct_KeepsTheOriginAsReceived()
    {
        // "abc" by client 1, then "x" with origin (1, 2) and right origin (1, 1): Yjs takes origin = left.lastId before the right-origin
        // lookup cuts "abc" after "a", so the origin stays (1, 2) (a Nightly differential find of TASK-4.28)
        var d = Net.Mk(9);
        Assert.True(d.TryApplyUpdate(Hex.Bytes("010201000401017403616263c40102010101780000")).Ok);
        Assert.Equal("axbc", Text(d));
        Assert.Equal("01030100040101740161840100026263c401020101017800", Hex.Of(d.EncodeStateAsUpdate()));
    }

    [Fact]
    [Covers("STR-15")]
    [Requires(Feature.Map)]
    public void AnItemWithAnOrigin_InheritsParentAndParentSub_FromItsNeighbour()
    {
        var a = Net.Mk(1);
        var b = Net.Mk(2);
        a.GetMap("m").Set("k", 1);
        a.GetMap("m").Set("k", 2);          // origin = the first entry, so no parent info is written
        Net.Sync(a, b);

        var s = StructsProbe.Of(b, 1);
        Assert.Equal(new[] { "m", "m" }, s.Select(x => x.ParentName));
        Assert.Equal(new[] { "k", "k" }, s.Select(x => x.ParentSub));
        Assert.Equal(((ulong, long)?)(1UL, 0L), s[1].Origin);
        Assert.True(b.GetMap("m").TryGet("k", out var value));
        Assert.Equal(2d, value.Value.AsNumber());
    }

    // ------------------------------------------------------------------ D: YATA

    [Fact]
    [Covers("STR-17")]
    [Requires(Feature.Text, Feature.Events)]
    public void PartiallyKnownStruct_OnIntegration_IntegratesOnlyTheUnseenSuffix()
    {
        var a = Net.Mk(1);
        var ups = Net.Capture(a);
        a.GetText("t").Insert(0, "he");
        a.GetText("t").Insert(2, "llo");

        var d = Net.Mk(9);
        d.ApplyUpdate(ups[0]);
        var seen = Net.Capture(d);
        d.ApplyUpdate(a.EncodeStateAsUpdate());      // one struct "hello" of which "he" is already known

        var emitted = Assert.Single(seen);
        Assert.Equal("01010102840101036c6c6f00", Hex.Of(emitted));   // only "llo", clock 2, origin (1,1)
        Assert.Equal("hello", Text(d));
        var s = Assert.Single(StructsProbe.Of(d, 1));
        Assert.Equal((0L, 5L), (s.Clock, s.Len));
    }

    [Fact]
    [Covers("STR-18", "STR-19", "STR-20")]
    [Requires(Feature.Text)]
    public void SameOrigin_LowerClientIdGoesLeft_WhateverTheDeliveryOrder()
    {
        foreach (var (idA, idB, expected) in new[] { (1u, 2u, "aABb"), (2u, 1u, "aBAb") })
        {
            var a = Net.Mk(idA);
            var b = Net.Mk(idB);
            a.GetText("t").Insert(0, "ab");
            Net.Sync(a, b);
            a.GetText("t").Insert(1, "A");
            b.GetText("t").Insert(1, "B");
            Net.Both(a, b);
            Assert.Equal(expected, Text(a));
            Assert.Equal(expected, Text(b));
        }
    }

    [Fact]
    [Covers("STR-18", "STR-19", "STR-20")]
    [Requires(Feature.Array)]
    public void NullOrigin_ConflictsAtTheStart_ResolveByClientId()
    {
        var a = Net.Mk(5);
        var b = Net.Mk(3);
        var c = Net.Mk(4);
        a.GetArray("arr").Unshift(new YEntry[] { "a1" });
        b.GetArray("arr").Unshift(new YEntry[] { "b1" });
        c.GetArray("arr").Unshift(new YEntry[] { "c1" });
        foreach (var (x, y) in new[] { (a, b), (b, a), (a, c), (c, a), (b, c), (c, b) }) Net.Sync(x, y);

        foreach (var d in new[] { a, b, c })
            Assert.Equal(new[] { "b1", "c1", "a1" }, d.GetArray("arr").ToArray().Select(e => e.Value.AsString()));
    }

    [Fact]
    [Covers("STR-18", "STR-19", "STR-20", "STR-23")]
    [Requires(Feature.Text, Feature.Events)]
    public void ConflictZone_ItemsFromAThirdClient_ConvergesUnderEveryDeliveryOrder()
    {
        // Three clients type at the same place; two of them then type again inside the conflict zone.
        var baseDoc = Net.Mk(1);
        var ups = Net.Capture(baseDoc);
        baseDoc.GetText("t").Insert(0, "ab");
        var a = baseDoc;
        var b = Net.Mk(2);
        var c = Net.Mk(3);
        Net.Sync(a, b);
        Net.Sync(a, c);
        var ub = Net.Capture(b);
        var uc = Net.Capture(c);
        a.GetText("t").Insert(1, "AA");
        b.GetText("t").Insert(1, "BB");
        c.GetText("t").Insert(1, "CC");
        b.GetText("t").Insert(3, "x");
        c.GetText("t").Insert(2, "y");
        var updates = new[] { ups[1], ub[^2], ub[^1], uc[^2], uc[^1] };

        var results = new HashSet<string>();
        var states = new HashSet<string>();
        foreach (var order in Permutations(updates))
        {
            var d = Net.Mk(99);
            d.ApplyUpdate(ups[0]);
            foreach (var u in order) d.ApplyUpdate(u);
            results.Add(Text(d));
            states.Add(Hex.Of(d.EncodeStateAsUpdate()));
        }
        Assert.Equal(new[] { "aAABBxCyCb" }, results);
        Assert.Single(states);
    }

    [Fact]
    [Covers("STR-18")]
    [Requires(Feature.Text)]
    public void NaturalSpot_Adjacent_NoConflictScanReordersAnything()
    {
        var a = Net.Mk(2);
        var b = Net.Mk(1);
        a.GetText("t").Insert(0, "ab");
        Net.Sync(a, b);
        b.GetText("t").Insert(2, "X");      // appended after the last item: nothing to the right
        b.GetText("t").Insert(0, "Y");      // prepended: nothing to the left
        Net.Sync(b, a);
        Assert.Equal("YabX", Text(a));
    }

    [Fact]
    [Covers("STR-21", "STR-23")]
    [Requires(Feature.Map)]
    public void MapKeyRace_LastWriterByYataWins_AndTheLoserIsStoredDeleted()
    {
        var a = Net.Mk(1);
        var b = Net.Mk(2);
        a.GetMap("m").Set("k", "a1");
        a.GetMap("m").Set("k", "a2");
        b.GetMap("m").Set("k", "b1");
        Net.Both(a, b);

        foreach (var d in new[] { a, b })
        {
            Assert.True(d.GetMap("m").TryGet("k", out var v));
            Assert.Equal("b1", v.Value.AsString());
            var s = StructsProbe.All(d).OrderBy(x => x.Client).ToList();
            Assert.Equal((1UL, 0L, 2L, true), (s[0].Client, s[0].Clock, s[0].Len, s[0].Deleted));   // both of a's entries, merged tombstone
            Assert.Equal((2UL, 0L, 1L, false), (s[1].Client, s[1].Clock, s[1].Len, s[1].Deleted));
        }
        Assert.Equal("[[1,[[0,2]]]]", StructsProbe.DeleteSetJson(StructsProbe.Dump(a)));
    }

    [Fact]
    [Covers("STR-22")]
    [Requires(Feature.Array, Feature.Map)]
    public void ParentLength_DeletedAndUncountableItems_CountsOnlyLiveCountableListItems()
    {
        var a = Net.Mk(1);
        var arr = a.GetArray("arr");
        arr.Push(new YEntry[] { 1, 2, 3 });
        arr.Delete(1, 1);
        var map = a.GetMap("m");
        map.Set("x", 1);
        map.Set("x", 2);
        map.Set("y", 3);

        Assert.Equal(2, arr.Length);        // the deleted item left the count
        Assert.Equal(2, map.Count);         // map entries live outside _length: one live value per key
    }

    [Fact]
    [Covers("STR-23", "STR-24")]
    [Requires(Feature.Array, Feature.Map, Feature.Events)]
    public void ItemIntegratedIntoADeletedParent_IsDeletedAtOnce_OrBecomesGcWhenTheParentWasCollected()
    {
        foreach (var gc in new[] { false, true })
        {
            var a = Net.Mk(1, gc);
            var b = Net.Mk(2);
            var arr = a.GetArray("arr");
            arr.Insert(0, new YEntry[] { new YMap() });
            Net.Sync(a, b);
            ((YMap)b.GetArray("arr")[0].Type!).Set("k", "late");     // concurrent with the deletion below
            arr.Delete(0, 1);

            var inDeleteSet = false;
            a.AfterTransaction += (tx, _) => inDeleteSet |= tx.DeleteSet.Contains(new ItemId(2, 0));
            Net.Sync(b, a);

            Assert.Equal(0, arr.Length);
            Assert.Equal(1L, a.GetStateVector()[2]);                 // the state advances either way
            var late = Assert.Single(StructsProbe.Of(a, 2));
            Assert.True(late.Deleted);
            if (gc)
            {
                Assert.True(late.IsGc);                              // parent resolved to null: replaced by a GC of the full length
                Assert.Equal(1L, late.Len);
            }
            else
            {
                Assert.True(late.IsItem);
                Assert.Equal(((ulong, long)?)(1UL, 0L), late.ParentId);
                Assert.True(inDeleteSet);                            // deleted by its own integration, so it is in the transaction's delete set
                Assert.Equal("[[1,[[0,1]]],[2,[[0,1]]]]", StructsProbe.DeleteSetJson(StructsProbe.Dump(a)));
            }
        }
    }

    [Fact]
    [Covers("STR-25")]
    [Requires(Feature.Array)]
    public void ContentDeleted_IsIntegratedAsDeleted_AndNeverCounts()
    {
        var a = Net.Mk(1, gc: false);
        var arr = a.GetArray("arr");
        arr.Push(new YEntry[] { 1, 2, 3 });
        arr.Delete(0, 3);

        var d = Net.Mk(2);
        d.ApplyUpdate(a.EncodeStateAsUpdate());

        Assert.Equal(0, d.GetArray("arr").Length);
        var s = Assert.Single(StructsProbe.Of(d, 1));
        Assert.Equal((0L, 3L, 1, true), (s.Clock, s.Len, s.Ref, s.Deleted));
        Assert.Equal("[[1,[[0,3]]]]", StructsProbe.DeleteSetJson(StructsProbe.Dump(d)));
    }

    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permutations(rest)) yield return new[] { items[i] }.Concat(tail).ToArray();
        }
    }
}
