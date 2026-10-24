using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Structs;

/// <summary>
/// Struct encoding, update application, pending structs and delete sets (docs/research/structs.md sections E4, F4, G3, K).
/// Hex goldens are the bytes the pinned Yjs produces for the same document history.
/// </summary>
public sealed class UpdateApplicationTests
{
    private static string Text(Doc d) => d.GetText("t").ToString();

    private static (byte[] U1, byte[] U2, byte[] U3) Chain()
    {
        var a = Net.Mk(1);
        var b = Net.Mk(2);
        var c = Net.Mk(3);
        var ua = Net.Capture(a);
        var ub = Net.Capture(b);
        var uc = Net.Capture(c);
        a.GetText("t").Insert(0, "A");
        Net.Sync(a, b);
        b.GetText("t").Insert(1, "B");
        Net.Sync(b, c);
        c.GetText("t").Insert(2, "C");
        return (ua[0], ub[^1], uc[^1]);
    }

    // ------------------------------------------------------------------ writing

    [Fact]
    [Covers("STR-29")]
    public void ItemWrite_BothOriginsAbsent_EmitsParentInfo() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        a.GetText("t").Insert(0, "abc");
        Assert.Equal("01010100040101740361626300", Hex.Of(a.EncodeStateAsUpdate()));
        Assert.Equal("000001010000010407047461626301030101000001010000", Hex.Of(a.EncodeStateAsUpdate(default, UpdateFormat.V2)));

        var c = Net.Mk(1);
        var b = Net.Mk(2);
        c.GetText("t").Insert(0, "abc");
        Net.Sync(c, b);
        var ups = Net.Capture(b);
        b.GetText("t").Insert(3, "X");     // origin only
        b.GetText("t").Insert(0, "Y");     // right origin only
        Assert.Equal("01010200840102015800", Hex.Of(ups[0]));
        Assert.Equal("01010201440100015900", Hex.Of(ups[1]));
    });

    [Fact]
    [Covers("STR-29")]
    public void ItemWrite_AMapEntryWritesItsKeyOnlyWithNullOrigins_AnOverwriteInheritsIt() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var ups = Net.Capture(a);
        a.GetMap("m").Set("k", 1);
        a.GetMap("m").Set("k", 2);
        Assert.Equal("010101002801016d016b017d0100", Hex.Of(ups[0]));       // info 0x28: parentSub bit, parent root "m", key "k"
        Assert.Equal("01010101a80100017d020101010001", Hex.Of(ups[1]));     // info 0xa8: origin present, so neither parent nor key is written
    });

    [Fact]
    [Covers("STR-29", "STR-34", "STR-17")]
    public void WritingFromAStateVectorInsideAnItem_StartsMidItem_WithASyntheticOrigin() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        a.GetText("t").Insert(0, "he");
        a.GetText("t").Insert(2, "llo");           // one struct "hello" after the merge
        var sv = Hex.Bytes("010102");            // the peer knows two characters of client 1
        Assert.Equal("01010102840101036c6c6f00", Hex.Of(a.EncodeStateAsUpdate(sv)));
        Assert.Equal("0000024100010200018405036c6c6f0300000001010200", Hex.Of(a.EncodeStateAsUpdate(sv, UpdateFormat.V2)));
    });

    [Fact]
    [Covers("STR-34", "STR-31", "GC-06")]
    public void GcStruct_AnyWrite_IsWrittenAsInfoZeroAndALength() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var arr = a.GetArray("arr");
        arr.Insert(0, new YEntry[] { new YMap() });
        var map = (YMap)arr[0].Type!;
        map.Set("k1", 1);
        map.Set("k2", 2);
        arr.Delete(0, 1);
        // struct 1: ContentDeleted shell; struct 2: GC (info 0x00, length 2); then the delete set.
        Assert.Equal("010201000101036172720100020101010003", Hex.Of(a.EncodeStateAsUpdate()));
        // V2 writes the GC length through the RLE-encoded length column.
        Assert.Equal("000001010000030100000503617272030101000201020102000101010002", Hex.Of(a.EncodeStateAsUpdate(default, UpdateFormat.V2)));
    });

    [Fact]
    [Covers("STR-66")]
    public void WritingAState_OmitsClientsThePeerHasInFull_AndWritesTheRestInDescendingClientOrder() => Impl.Run(() =>
    {
        var x = Net.Mk(9);
        foreach (var id in new ulong[] { 1, 2, 3 })
        {
            var d = Net.Mk(id);
            d.GetText("t").Insert(0, id == 1 ? "a" : id == 2 ? "b" : "c");
            Net.Sync(d, x);
        }
        var y = Net.Mk(8);
        y.ApplyUpdate(Hex.Bytes("0101010004010174016100"));       // client 1's "a" only
        Assert.Equal("010101", Hex.Of(Net.StateVector(y)));

        Assert.Equal("0201030004010174016301020004010174016200", Hex.Of(x.EncodeStateAsUpdate(Net.StateVector(y))));   // clients 3 and 2; client 1 omitted
        Assert.Equal("0301030004010174016301020004010174016201010004010174016100", Hex.Of(x.EncodeStateAsUpdate()));
        Assert.Equal("0000", Hex.Of(x.EncodeStateAsUpdate(Net.StateVector(x))));                                          // nothing to send: zero clients, empty delete set
    });

    [Fact]
    [Covers("STR-67")]
    public void StateAsUpdate_AnyDoc_IsTheStructsPlusTheDeleteSetDerivedFromTheStore() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        a.GetText("t").Insert(0, "hello");
        a.GetText("t").Delete(1, 3);
        Assert.Equal("0103010004010174016881010003840103016f0101010103", Hex.Of(a.EncodeStateAsUpdate()));
        Assert.Equal("000002410102000600050400810084060374686f410101010001030103000101010102", Hex.Of(a.EncodeStateAsUpdate(default, UpdateFormat.V2)));
    });

    [Fact]
    [Covers("GC-06")]
    public void SameHistory_GcOnAndOff_EncodesDifferently() => Impl.Run(() =>
    {
        var on = Net.Mk(1, gc: true);
        var off = Net.Mk(1, gc: false);
        foreach (var d in new[] { on, off })
        {
            d.GetText("t").Insert(0, "hello");
            d.GetText("t").Delete(1, 3);
        }
        Assert.Equal("0103010004010174016881010003840103016f0101010103", Hex.Of(on.EncodeStateAsUpdate()));       // ContentDeleted: ref 1 + length
        Assert.Equal("0103010004010174016884010003656c6c840103016f0101010103", Hex.Of(off.EncodeStateAsUpdate())); // the deleted text is still written
        Assert.Equal(Text(on), Text(off));
    });

    // ------------------------------------------------------------------ reading

    [Theory]
    [InlineData("01 01 05 00 04 01 01 74 01 79 00", "y", "")]                    // item, no origins: parent info (root "t")
    [InlineData("01 01 05 00 a4 01 00 01 79 00", "xy", "x")]                     // origin present: the parentSub flag (0x20) is ignored, parent inherited
    [InlineData("01 01 05 00 44 01 00 01 79 00", "yx", "x")]                     // right origin only
    [Covers("STR-61")]
    public void ReadingStructRefs_EveryInfoBitCombination_IsHonoured(string hex, string expected, string existing) => Impl.Run(() =>
    {
        var d = Net.Mk(1);
        if (existing != "") d.GetText("t").Insert(0, existing);
        var result = d.TryApplyUpdate(Hex.Bytes(hex.Replace(" ", "")));
        Assert.True(result.Ok);
        Assert.Equal(expected, Text(d));
        Assert.Equal(1L, d.GetStateVector()[5]);
    });

    [Fact]
    [Covers("STR-61")]
    public void ReadingStructRefs_GcSkipAndMapEntries_AreRead() => Impl.Run(() =>
    {
        var gc = Net.Mk(1);
        Assert.True(gc.TryApplyUpdate(Hex.Bytes("01010500000300")).Ok);            // GC of length 3 (info 0, length)
        Assert.Equal(3L, gc.GetStateVector()[5]);
        var s = Assert.Single(StructsProbe.Of(gc, 5));
        Assert.True(s.IsGc);
        Assert.Equal(3L, s.Len);

        var map = Net.Mk(1);
        Assert.True(map.TryApplyUpdate(Hex.Bytes("010105002801016d016b017d0100")).Ok);     // map entry: parentSub read because both origins are absent
        Assert.True(map.GetMap("m").TryGet("k", out var v));
        Assert.Equal(1d, v.Value.AsNumber());

        var skip = Net.Mk(1);
        Assert.True(skip.TryApplyUpdate(Hex.Bytes("010205000a0204010174017900")).Ok);   // Skip(2) then an item at clock 2
        Assert.Equal(0L, skip.GetStateVector()[5]);                                          // the item waits behind the skipped range
        Assert.Empty(StructsProbe.Of(skip, 5));
        Assert.Equal("0101050204010174017900", Hex.Of(skip.EncodeStateAsUpdate()));         // the Skip itself is not kept
    });

    [Fact]
    [Covers("STR-62")]
    public void ReapplyingAnUpdate_IsANoOp_NoEventsNoStateChange() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        a.GetText("t").Insert(0, "ab");
        var update = a.EncodeStateAsUpdate();

        var d = Net.Mk(2);
        var events = Net.Capture(d);
        d.ApplyUpdate(update);
        var state = Hex.Of(d.EncodeStateAsUpdate());
        d.ApplyUpdate(update);
        d.ApplyUpdate(a.EncodeStateAsUpdate());

        Assert.Single(events);
        Assert.Equal(state, Hex.Of(d.EncodeStateAsUpdate()));
        Assert.Equal("ab", Text(d));
    });

    [Fact]
    [Covers("STR-62", "STR-64")]
    public void CausallyDependentUpdates_ConvergeUnderEveryDeliveryOrder_AndLeaveNothingPending() => Impl.Run(() =>
    {
        var (u1, u2, u3) = Chain();
        var orders = new[] { new[] { u1, u2, u3 }, new[] { u1, u3, u2 }, new[] { u2, u1, u3 }, new[] { u2, u3, u1 }, new[] { u3, u1, u2 }, new[] { u3, u2, u1 } };
        var states = new HashSet<string>();
        foreach (var order in orders)
        {
            var d = Net.Mk(10);
            foreach (var u in order) d.ApplyUpdate(u);
            Assert.Equal("ABC", Text(d));
            Assert.False(StructsProbe.HasPending(d));
            states.Add(Hex.Of(d.EncodeStateAsUpdate()));
        }
        Assert.Single(states);
    });

    [Fact]
    [Covers("STR-63", "STR-64", "STR-52")]
    public void WhenOneStructWaits_EarlierStructsOfTheUpdateStillIntegrate_AndTheRestIsKeptAsPending() => Impl.Run(() =>
    {
        var x = Net.Mk(1);
        var y = Net.Mk(2);
        var z = Net.Mk(3);
        var w = Net.Mk(4);
        var ua = Net.Capture(x);
        var ub = Net.Capture(y);
        var uc = Net.Capture(z);
        var uw = Net.Capture(w);
        x.GetText("t").Insert(0, "A");
        Net.Sync(x, y);
        y.GetText("t").Insert(1, "B");
        w.GetText("t").Insert(0, "W");
        Net.Sync(w, z);
        z.GetText("t").Insert(1, "C");           // depends on client 4, whose update never reaches d directly

        var merged = new ArrayBufferWriter<byte>();
        Assert.True(YUpdate.TryMerge(new ReadOnlyMemory<byte>[] { ua[0], ub[^1], uc[^1] }, merged).Ok);
        var d = Net.Mk(9);
        d.ApplyUpdate(merged.WrittenSpan);

        Assert.Equal("AB", Text(d));
        Assert.Equal(new long[] { 1, 1, 0, 0 }, new ulong[] { 1, 2, 3, 4 }.Select(c => d.GetStateVector()[c]));
        Assert.True(StructsProbe.HasPending(d));

        var downstream = Net.Mk(10);
        downstream.ApplyUpdate(d.EncodeStateAsUpdate());          // the waiting struct travels with the exported state
        Assert.Equal("AB", Text(downstream));
        downstream.ApplyUpdate(uw[0]);
        d.ApplyUpdate(uw[0]);
        Assert.Equal("ABWC", Text(downstream));
        Assert.Equal("ABWC", Text(d));
        Assert.False(StructsProbe.HasPending(d));
    });

    [Fact]
    [Covers("STR-64")]
    public void IndependentPendingUpdates_AreMergedIntoOnePendingState_AndEachRetriesWhenItsDependencyArrives() => Impl.Run(() =>
    {
        var x = Net.Mk(1);
        var y = Net.Mk(2);
        var c = Net.Mk(5);
        var e = Net.Mk(6);
        var ux = Net.Capture(x);
        var uy = Net.Capture(y);
        var uc = Net.Capture(c);
        var ue = Net.Capture(e);
        x.GetText("t").Insert(0, "A");
        Net.Sync(x, y);
        y.GetText("t").Insert(1, "B");
        c.GetText("t").Insert(0, "c");
        Net.Sync(c, e);
        e.GetText("t").Insert(1, "e");

        var g = Net.Mk(11);
        g.ApplyUpdate(uy[^1]);                                     // waits for client 1
        g.ApplyUpdate(ue[^1]);                                     // waits for client 5
        Assert.Equal("", Text(g));
        Assert.Equal(0L, g.GetStateVector()[2]);

        var h = Net.Mk(12);
        h.ApplyUpdate(g.EncodeStateAsUpdate());                    // both waiting structs are in the one exported state
        h.ApplyUpdate(ux[0]);
        Assert.Equal("AB", Text(h));                               // only the first retries
        h.ApplyUpdate(uc[0]);
        Assert.Equal("ABce", Text(h));

        g.ApplyUpdate(ux[0]);
        g.ApplyUpdate(uc[0]);
        Assert.Equal("ABce", Text(g));
        Assert.False(StructsProbe.HasPending(g));
    });

    [Fact]
    [Covers("STR-65")]
    public void DeleteSet_ArrivingBeforeTheStructsItRefersTo_IsHeldAndAppliedLater() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var ups = Net.Capture(a);
        a.GetText("t").Insert(0, "ab");
        a.GetText("t").Insert(2, "cd");
        a.GetText("t").Delete(1, 2);            // removes "bc": clocks 1 and 2

        var d = Net.Mk(9);
        d.ApplyUpdate(ups[2]);                  // only a delete set, for a client d has never seen
        d.ApplyUpdate(ups[1]);
        Assert.Equal("", Text(d));
        d.ApplyUpdate(ups[0]);
        Assert.Equal("ad", Text(d));
        Assert.False(StructsProbe.HasPending(d));

        // The same delete set against a partially known range: the known part applies now, the tail waits.
        var e = Net.Mk(10);
        e.ApplyUpdate(ups[0]);
        e.ApplyUpdate(ups[2]);
        Assert.Equal("a", Text(e));
        Assert.Equal(new[] { (0L, 1L, false), (1L, 1L, true) }, StructsProbe.Of(e, 1).Select(s => (s.Clock, s.Len, s.Deleted)));
        e.ApplyUpdate(ups[1]);
        Assert.Equal("ad", Text(e));
        Assert.Equal(new[] { (0L, 1L, false), (1L, 2L, true), (3L, 1L, false) }, StructsProbe.Of(e, 1).Select(s => (s.Clock, s.Len, s.Deleted)));
    });

    [Fact]
    [Covers("STR-65")]
    public void ReapplyingADeleteSet_ForAlreadyDeletedRanges_ChangesNothing() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var ups = Net.Capture(a);
        a.GetText("t").Insert(0, "abc");
        a.GetText("t").Delete(1, 1);

        var d = Net.Mk(2);
        d.ApplyUpdate(ups[0]);
        var events = Net.Capture(d);
        d.ApplyUpdate(ups[1]);
        d.ApplyUpdate(ups[1]);
        Assert.Single(events);
        Assert.Equal("ac", Text(d));
    });

    [Fact]
    [Covers("STR-68")]
    public void AStructFromAPeerUsingMyClientId_ChangesMyClientId_AfterTheTransaction() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var b = Net.Mk(1);                       // the same client id on purpose
        a.GetText("t").Insert(0, "abc");
        Net.Sync(a, b);

        Assert.NotEqual(1UL, b.ClientId);
        Assert.Equal("abc", Text(b));
        b.GetText("t").Insert(3, "Z");           // written under the new id, so it cannot collide with a's clocks
        Net.Sync(b, a);
        Assert.Equal("abcZ", Text(a));
        Assert.Equal(new[] { b.ClientId, 1UL }, StructsProbe.Dump(a).Select(c => c.Client));
    });
}
