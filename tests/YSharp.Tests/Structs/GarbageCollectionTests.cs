using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Rng;
using YSharp.TestKit.Sim;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Assertions;

namespace YSharp.Tests.Structs;

/// <summary>
/// Garbage collection, tombstone merging and deletion (docs/research/structs.md E1-E3, J7, J8 and the GC-* entries of
/// docs/research/doc-features.md section 7). Expected structures come from the pinned Yjs.
/// </summary>
public sealed class GarbageCollectionTests
{
    private static string Text(Doc d) => d.GetText("t").ToString();

    // ------------------------------------------------------------------ deletion

    [Fact]
    [Covers("STR-26")]
    public void Delete_IsIdempotent_OneDeleteSetEntryAndTheLengthDecrementsOnce() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var b = Net.Mk(2);
        a.GetText("t").Insert(0, "abc");
        Net.Sync(a, b);
        a.GetText("t").Delete(1, 1);
        b.GetText("t").Delete(1, 1);              // the same character, concurrently
        Net.Both(a, b);

        foreach (var d in new[] { a, b })
        {
            Assert.Equal("ac", Text(d));
            Assert.Equal(2, d.GetText("t").Length);
            Assert.Equal("[[1,[[1,1]]]]", StructsProbe.DeleteSetJson(StructsProbe.Dump(d)));
            Assert.Equal(new[] { false, true, false }, StructsProbe.Of(d, 1).Select(s => s.Deleted));
        }
    });

    // ------------------------------------------------------------------ GC flag, filter, scope, eligibility

    [Fact]
    [Covers("GC-01", "STR-27", "STR-59")]
    public void CollectedItem_KeepsItsIdAndLength_ButLosesItsContent() => Impl.Run(() =>
    {
        var on = Net.Mk(1);
        on.GetText("t").Insert(0, "abc");
        on.GetText("t").Delete(1, 1);
        var collected = StructsProbe.Of(on, 1).Single(s => s.Deleted);
        Assert.Equal((1L, 1L, 1), (collected.Clock, collected.Len, collected.Ref));      // ContentDeleted of length 1

        var off = Net.Mk(2, gc: false);
        off.GetText("t").Insert(0, "abc");
        off.GetText("t").Delete(1, 1);
        var kept = StructsProbe.Of(off, 2).Single(s => s.Deleted);
        Assert.Equal((1L, 1L, "b"), (kept.Clock, kept.Len, kept.Text));                  // gc off: the text stays

        var filtered = new Doc(new DocOptions { ClientId = 3, GcFilter = view => view.Id.Clock != 1 });
        filtered.GetText("t").Insert(0, "abc");
        filtered.GetText("t").Delete(1, 1);
        Assert.Equal("b", StructsProbe.Of(filtered, 3).Single(s => s.Deleted).Text);     // the filter refused this item
    });

    [Fact]
    [Covers("GC-02")]
    public void Gc_OnlyLooksAtTheCurrentTransactionsDeleteSet_SoEarlierTombstonesAreNeverCollectedRetroactively() => Impl.Run(() =>
    {
        var d = Net.Mk(1, gc: false);
        var t = d.GetText("t");
        t.Insert(0, "abc");
        t.Delete(1, 1);                                   // deleted while gc is off
        d.Gc = true;
        t.Insert(0, "z");                                 // an unrelated transaction
        Assert.Equal("b", StructsProbe.Of(d, 1).Single(s => s.Deleted).Text);

        t.Delete(1, 1);                                   // a new deletion is collected; the old tombstone still is not
        var deleted = StructsProbe.Of(d, 1).Where(s => s.Deleted).ToList();
        Assert.Contains(deleted, s => s.Text == "b");
        Assert.Contains(deleted, s => s.Ref == 1);

        // On the receiver of a remote deletion the same rule applies to the delete set of the applying transaction.
        var a = Net.Mk(2);
        var b = Net.Mk(3);
        a.GetText("t").Insert(0, "abc");
        Net.Sync(a, b);
        a.GetText("t").Delete(1, 1);
        Net.Sync(a, b);
        Assert.Equal(1, StructsProbe.Of(b, 2).Single(s => s.Deleted).Ref);
    });

    [Fact]
    [Covers("GC-03")]
    public void OnlyDeletedItemsWithoutKeep_AndAcceptedByTheFilter_AreCollected() => Impl.Run(() =>
    {
        var seen = new List<(long Clock, bool Deleted)>();
        var d = new Doc(new DocOptions { ClientId = 1, GcFilter = view => { seen.Add((view.Id.Clock, view.IsDeleted)); return true; } });
        d.GetText("t").Insert(0, "abc");
        d.GetText("t").Delete(0, 1);

        Assert.All(seen, x => Assert.True(x.Deleted));        // the filter is only consulted for deleted items
        Assert.NotEmpty(seen);
        Assert.Equal(1, StructsProbe.Of(d, 1).First(s => s.Deleted).Ref);
    });

    [Fact]
    [Covers("GC-04")]
    public void CollectionForms_TopLevelDeletedItemsKeepAShell_ChildrenOfDeletedTypesBecomeGc() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        a.GetText("t").Insert(0, "abc");
        a.GetText("t").Delete(0, 3);
        var shell = Assert.Single(StructsProbe.Of(a, 1));
        Assert.True(shell.IsItem);
        Assert.Equal(1, shell.Ref);                         // Item shell with ContentDeleted

        var b = Net.Mk(2);
        var arr = b.GetArray("arr");
        arr.Insert(0, new YEntry[] { new YMap() });
        ((YMap)arr[0].Type!).Set("k", 1);
        arr.Delete(0, 1);
        var s = StructsProbe.Of(b, 2);
        Assert.True(s[0].IsItem);
        Assert.True(s[1].IsGc);                             // the type's content is bare GC structs
    });

    // ------------------------------------------------------------------ wire effects, orphans, merging

    [Fact]
    [Covers("GC-07")]
    public void ChildrenOfACollectedType_AreDiscardedWhenGcIsOn_AndKeptHiddenWhenItIsOff() => Impl.Run(() =>
    {
        var visible = new List<string>();
        var states = new List<string>();
        foreach (var gc in new[] { true, false })
        {
            var a = Net.Mk(1, gc);
            var b = Net.Mk(2);
            var arr = a.GetArray("arr");
            arr.Insert(0, new YEntry[] { new YMap() });
            Net.Sync(a, b);
            ((YMap)b.GetArray("arr")[0].Type!).Set("k", "late");
            arr.Delete(0, 1);
            Net.Both(a, b);

            visible.Add($"{a.GetArray("arr").Length}/{b.GetArray("arr").Length}");
            states.Add(Hex.Of(a.EncodeStateAsUpdate()));
            var late = Assert.Single(StructsProbe.Of(a, 2));
            Assert.Equal(gc, late.IsGc);                    // gc on: a GC of the full length; off: a deleted item
            Assert.True(late.Deleted);
        }
        Assert.Equal(new[] { "0/0", "0/0" }, visible);       // the visible state converges either way
        Assert.NotEqual(states[0], states[1]);               // the bytes differ
    });

    [Fact]
    [Covers("GC-08", "STR-28", "STR-60")]
    public void AdjacentDeletedStructs_MergeAfterEveryTransaction_WhateverTheGcFlag() => Impl.Run(() =>
    {
        foreach (var gc in new[] { false, true })
        {
            var d = Net.Mk(1, gc);
            var t = d.GetText("t");
            t.Insert(0, "abcd");
            t.Delete(1, 1);
            t.Delete(1, 1);                                  // two separate transactions, adjacent tombstones

            var s = StructsProbe.Of(d, 1);
            Assert.Equal(new[] { (0L, 1L), (1L, 2L), (3L, 1L) }, s.Select(x => (x.Clock, x.Len)));
            Assert.Equal(gc ? 1 : 4, s[1].Ref);
            if (!gc) Assert.Equal("bc", s[1].Text);
        }
    });

    [Fact]
    [Covers("STR-28")]
    public void MergeWith_AnyCondition_RequiresEveryConditionToHold() => Impl.Run(() =>
    {
        // same client, adjacent ids, origin chain, same deleted state, same content kind: merges
        var ok = Net.Mk(1);
        ok.GetText("t").Insert(0, "a");
        ok.GetText("t").Insert(1, "b");
        Assert.Equal(new[] { (0L, 2L) }, StructsProbe.Of(ok, 1).Select(x => (x.Clock, x.Len)));

        // deleted state differs
        var deleted = Net.Mk(1);
        deleted.GetText("t").Insert(0, "abc");
        deleted.GetText("t").Delete(1, 1);
        Assert.Equal(3, StructsProbe.Of(deleted, 1).Count);

        // different clients
        var a = Net.Mk(1);
        var b = Net.Mk(2);
        a.GetText("t").Insert(0, "a");
        Net.Sync(a, b);
        b.GetText("t").Insert(1, "b");
        Net.Sync(b, a);
        Assert.Equal(2, StructsProbe.Dump(a).Count);

        // the origin chain is broken: "b" was typed between "a" and "c"
        var chain = Net.Mk(1);
        chain.GetText("t").Insert(0, "a");
        chain.GetText("t").Insert(1, "c");
        chain.GetText("t").Insert(1, "b");
        Assert.Equal(new[] { (0L, 1L), (1L, 1L), (2L, 1L) }, StructsProbe.Of(chain, 1).Select(x => (x.Clock, x.Len)));

        // different content kinds never merge, however adjacent
        var kinds = Net.Mk(1);
        kinds.GetArray("arr").Push(new YEntry[] { 1 });
        kinds.GetArray("arr").Push(new YEntry[] { YValue.Bytes(new byte[] { 1 }) });
        Assert.Equal(2, StructsProbe.Of(kinds, 1).Count);
    });

    [Fact]
    [Covers("GC-09")]
    public void SubdocOptions_AreCarriedByTheItem_AndTheItemIsCollectedLikeAnyOther() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        a.GetMap("m").Set("s", new Doc(new DocOptions { Guid = "g1", Gc = false }));
        var b = Net.Mk(2);
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        Assert.True(b.GetMap("m").TryGet("s", out var entry));
        Assert.False(entry.Doc!.Gc);                       // gc:false persists through the wire in the item's options

        a.GetMap("m").Delete("s");                         // ContentDoc.gc itself does nothing; the item still becomes ContentDeleted
        Assert.Equal(1, StructsProbe.Of(a, 1).Single().Ref);
    });

    [Fact]
    [Covers("GC-10")]
    public void Snapshots_GcOn_NeedGcOffAndTurningItOffLaterDoesNotBringContentBack() => Impl.Run(() =>
    {
        var on = Net.Mk(1);
        on.GetText("t").Insert(0, "abc");
        var snapshot = YSnapshot.Create(on);
        Assert.ThrowsAny<Exception>(() => YSnapshot.CreateDoc(on, snapshot));        // origin.Gc must be false (SNAP-08)

        on.GetText("t").Delete(1, 1);
        on.Gc = false;
        Assert.Equal("ac", YSnapshot.CreateDoc(on, snapshot).GetText("t").ToString());   // "b" was already collected

        var off = Net.Mk(2, gc: false);
        off.GetText("t").Insert(0, "abc");
        var kept = YSnapshot.Create(off);
        off.GetText("t").Delete(1, 1);
        Assert.Equal("abc", YSnapshot.CreateDoc(off, kept).GetText("t").ToString());
    });

    [Fact]
    [Covers("GC-11")]
    public void ConvergenceHelper_ChecksStoresAndPending_WithGcOnByDefault_AndAGcOffRunByHand() => Impl.Run(() =>
    {
        var internals = new StructsSimInternals();
        for (uint seed = 1; seed <= 3; seed++)
        {
            SimHarness.ApplyRandomTests<object?>(Prng.Create(seed),
                new Action<TestYInstance, IPrng, object?>[]
                {
                    (u, g, _) => { var t = u.Doc.GetText("text"); t.Insert((int)Prng.Int32(g, 0, t.Length), Prng.Word(g)); },
                    (u, g, _) => { var t = u.Doc.GetText("text"); if (t.Length > 0) t.Delete((int)Prng.Int32(g, 0, t.Length - 1), 1); },
                }, iterations: 40, internals: internals);
        }

        // gc off, by hand: the same edits on two replicas that exchange everything
        var a = Net.Mk(1, gc: false);
        var b = Net.Mk(2, gc: false);
        var gen = Prng.Create(7);
        foreach (var d in new[] { a, b, a, b, a })
        {
            var t = d.GetText("t");
            t.Insert((int)Prng.Int32(gen, 0, t.Length), Prng.Word(gen));
            if (t.Length > 3) t.Delete((int)Prng.Int32(gen, 0, t.Length - 1), 1);
        }
        Net.Both(a, b);
        Net.Both(a, b);
        Assert.Equal(Text(a), Text(b));
        internals.AssertNoPending(a);
        internals.AssertNoPending(b);
        internals.AssertStoresEqual(a, b);
    });
}
