using System.Linq;
using Xunit;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Structs;

/// <summary>
/// StructStore lookup and split behaviour (docs/research/structs.md section I). The store is internal, so lookups are
/// observed through what callers do with them: applying delete sets, integrating with offsets and re-exporting pending state.
/// </summary>
[Requires(Feature.Store)]
public sealed class StructStoreTests
{
    [Fact]
    [Covers("STR-46", "STR-47")]
    [Requires(Feature.Text)]
    public void LookupByClock_FindsFirstMiddleAndLastStructs_WhenDeleteRangesAreApplied()
    {
        var a = Net.Mk(1);
        var b = Net.Mk(2);
        var t = a.GetText("t");
        t.Insert(0, "abcdefghij");
        Net.Sync(a, b);
        foreach (var i in new[] { 9, 7, 5, 3, 1 }) t.Delete(i, 1);       // ten structs: live and deleted alternate, so none can merge
        Net.Sync(a, b);                                                  // b looks up clock 1, 3, 5, 7 and 9 by binary search

        var s = StructsProbe.Of(b, 1);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (long)i), s.Select(x => x.Clock));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i % 2 == 1), s.Select(x => x.Deleted));
        Assert.Equal("acegi", b.GetText("t").ToString());
        Assert.Equal("[[1,[[1,1],[3,1],[5,1],[7,1],[9,1]]]]", StructsProbe.DeleteSetJson(StructsProbe.Dump(b)));
    }

    [Fact]
    [Covers("STR-47")]
    [Requires(Feature.V1)]
    public void LookupOfAnUnknownClient_IsHeldAsPending_InsteadOfFailing()
    {
        var d = Net.Mk(1);
        // No structs; a delete set for client 7, range [0, 1): JS would fail a lookup in the store, y-sharp keeps it pending.
        var result = d.TryApplyUpdate(Hex.Bytes("000107010001"));
        Assert.True(result.Ok);
        Assert.Empty(StructsProbe.Dump(d));
        Assert.Equal(0L, d.GetStateVector()[7]);
        Assert.Equal("000107010001", Hex.Of(d.EncodeStateAsUpdate()));    // re-exported as pending delete set
    }

    [Fact]
    [Covers("STR-48", "STR-49", "STR-51")]
    [Requires(Feature.Text)]
    public void ADeleteRangeInTheMiddleOfAnItem_SplitsItThreeWays_OnlyAtTheRangeEdges()
    {
        var a = Net.Mk(1, gc: false);
        var b = Net.Mk(2, gc: false);
        a.GetText("t").Insert(0, "hello");
        Net.Sync(a, b);
        a.GetText("t").Delete(1, 2);
        Net.Sync(a, b);

        var s = StructsProbe.Of(b, 1);
        Assert.Equal(new[] { (0L, 1L), (1L, 2L), (3L, 2L) }, s.Select(x => (x.Clock, x.Len)));
        Assert.Equal(new[] { "h", "el", "lo" }, s.Select(x => x.Text));
        Assert.Equal(new[] { false, true, false }, s.Select(x => x.Deleted));
        Assert.Equal(((ulong, long)?)(1UL, 0L), s[1].Origin);
        Assert.Equal(((ulong, long)?)(1UL, 2L), s[2].Origin);

        // A range that covers a whole struct splits nothing; the two adjacent tombstones then merge.
        a.GetText("t").Delete(0, 1);
        Net.Sync(a, b);
        var merged = StructsProbe.Of(b, 1);
        Assert.Equal(new[] { (0L, 3L), (3L, 2L) }, merged.Select(x => (x.Clock, x.Len)));
        Assert.Equal(new[] { "hel", "lo" }, merged.Select(x => x.Text));
    }

    [Fact]
    [Covers("STR-50", "GC-04")]
    [Requires(Feature.Array, Feature.Map)]
    public void ReplacingAStruct_KeepsItsIdAndLength_WhenItBecomesGc()
    {
        var a = Net.Mk(1);
        var arr = a.GetArray("arr");
        arr.Insert(0, new YEntry[] { new YMap() });
        ((YMap)arr[0].Type!).Set("k", 1);
        Assert.Equal(new[] { (0L, 1L), (1L, 1L) }, StructsProbe.Of(a, 1).Select(s => (s.Clock, s.Len)));

        arr.Delete(0, 1);

        var after = StructsProbe.Of(a, 1);
        Assert.Equal(new[] { (0L, 1L), (1L, 1L) }, after.Select(s => (s.Clock, s.Len)));   // same ids and lengths, nothing lost or invented
        Assert.False(after[0].IsGc);                                                       // the type's own item stays an Item (ContentDeleted)
        Assert.True(after[1].IsGc);                                                        // its child slot was replaced by a GC
    }

    [Fact]
    [Covers("STR-52")]
    [Requires(Feature.Text, Feature.Events)]
    public void PendingStructsAndPendingDeleteSet_AreStoreState_ReExportedByEncodeStateAsUpdate()
    {
        // pendingStructs
        var a = Net.Mk(1);
        var ups = Net.Capture(a);
        a.GetText("t").Insert(0, "a");
        a.GetText("t").Insert(1, "b");
        var d = Net.Mk(9);
        d.ApplyUpdate(ups[1]);
        Assert.Equal(0L, d.GetStateVector()[1]);
        Assert.Equal("01010101840100016200", Hex.Of(d.EncodeStateAsUpdate()));   // the waiting struct is part of the exported state
        var downstream = Net.Mk(10);
        downstream.ApplyUpdate(d.EncodeStateAsUpdate());
        downstream.ApplyUpdate(ups[0]);
        Assert.Equal("ab", downstream.GetText("t").ToString());

        // pendingDs
        var b = Net.Mk(2);
        var ups2 = Net.Capture(b);
        b.GetText("t").Insert(0, "abc");
        b.GetText("t").Delete(1, 1);
        var e = Net.Mk(11);
        e.ApplyUpdate(ups2[1]);
        Assert.Equal("000102010101", Hex.Of(e.EncodeStateAsUpdate()));
        var f = Net.Mk(12);
        f.ApplyUpdate(e.EncodeStateAsUpdate());
        f.ApplyUpdate(ups2[0]);
        Assert.Equal("ac", f.GetText("t").ToString());
    }
}
