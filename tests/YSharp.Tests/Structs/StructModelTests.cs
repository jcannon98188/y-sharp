using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Structs;

/// <summary>
/// Struct model and invariants (docs/research/structs.md sections A, F, G, H0): ids and lengths, per-client contiguity,
/// the item flags, GC and Skip structs. The struct store is read through the ADR 0016 <c>structs</c> dump
/// (<see cref="StructsProbe"/>); expected values were taken from the pinned Yjs.
/// </summary>
public sealed class StructModelTests
{
    internal static YValue Attr(string key, YValue value) =>
        YValue.Object(new[] { new KeyValuePair<string, YValue>(key, value) });

    [Fact]
    [Covers("STR-01", "STR-08")]
    public void Item_RecordsItsId_AndItsLengthComesFromItsContent() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        a.GetText("t").Insert(0, "abc");
        a.GetArray("arr").Push(new YEntry[] { 1, 2, 3 });
        a.GetArray("arr").Push(new YEntry[] { new YMap() });
        a.GetMap("m").Set("k", "v");

        var structs = StructsProbe.Of(a, 1);
        Assert.Equal(new[] { (0L, 3L), (3L, 3L), (6L, 1L), (7L, 1L) }, structs.Select(s => (s.Clock, s.Len)));
        Assert.Equal(new[] { 4, 8, 7, 8 }, structs.Select(s => s.Ref));
        Assert.All(structs, s => Assert.Equal(1u, s.Client));
    });

    [Fact]
    [Covers("STR-02", "STR-04")]
    public void PerClientStructs_AreGapFree_AndTheStateIsTheEndOfTheLastStruct() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var b = Net.Mk(2);
        a.GetText("t").Insert(0, "hello");
        Net.Sync(a, b);
        b.GetText("t").Insert(2, "XY");
        a.GetText("t").Delete(0, 2);
        a.GetMap("m").Set("k", 1);
        a.GetMap("m").Set("k", 2);
        Net.Both(a, b);

        foreach (var doc in new[] { a, b })
        {
            var dump = StructsProbe.Dump(doc);
            Assert.Equal(new uint[] { 2, 1 }, dump.Select(c => c.Client));   // descending client order
            foreach (var client in dump)
            {
                Assert.Equal(0L, client.Structs[0].Clock);
                for (var i = 0; i + 1 < client.Structs.Count; i++)
                    Assert.Equal(client.Structs[i].End, client.Structs[i + 1].Clock);
                Assert.Equal(client.Structs[^1].End, doc.GetStateVector()[client.Client]);
            }
            Assert.Equal(0L, doc.GetStateVector()[999]);
        }
    });

    [Fact]
    [Covers("STR-03")]
    public void AStructWithAClockGap_NeverEntersTheStore_ItWaitsAsPending() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var ups = Net.Capture(a);
        var t = a.GetText("t");
        t.Insert(0, "a");
        t.Insert(1, "b");
        t.Insert(2, "c");

        var d = Net.Mk(9);
        d.ApplyUpdate(ups[2]);
        Assert.Empty(StructsProbe.Of(d, 1));
        Assert.Equal(0L, d.GetStateVector()[1]);
        d.ApplyUpdate(ups[1]);
        Assert.Empty(StructsProbe.Of(d, 1));

        d.ApplyUpdate(ups[0]);
        var s = Assert.Single(StructsProbe.Of(d, 1));
        Assert.Equal((0L, 3L, "abc"), (s.Clock, s.Len, s.Text));
        Assert.Equal("abc", d.GetText("t").ToString());
    });

    [Fact]
    [Covers("STR-05", "STR-40")]
    public void FormatMarks_AreNotCountable_AndDeletionOnlySetsTheDeletedFlag() => Impl.Run(() =>
    {
        var a = Net.Mk(1, gc: false);
        var t = a.GetText("t");
        t.Insert(0, "ab", Attr("bold", true));
        Assert.Equal(2, t.Length);                                    // two format marks exist but do not count
        Assert.Equal(new[] { 6, 4, 6 }, StructsProbe.Of(a, 1).Select(s => s.Ref).Take(3));

        t.Delete(0, 1);
        Assert.Equal(1, t.Length);
        var deleted = StructsProbe.Of(a, 1).Single(s => s.Deleted);
        Assert.Equal("a", deleted.Text);                              // gc off: deleted content is kept
        Assert.False(deleted.Keep);

        var arr = a.GetArray("arr");
        arr.Push(new YEntry[] { 1, 2, 3 });
        arr.Delete(1, 1);
        Assert.Equal(2, arr.Length);                                  // deleted content never counts
    });

    [Fact]
    [Covers("STR-06")]
    public void OriginOfALaterInsert_IsTheLastIdOfTheItemItFollows() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var b = Net.Mk(2);
        a.GetText("t").Insert(0, "abc");
        Net.Sync(a, b);
        b.GetText("t").Insert(3, "X");
        Assert.Equal((1u, 2L), StructsProbe.Of(b, 2).Single().Origin);        // clock + length - 1 of a three-long item

        var c = Net.Mk(1);
        var d = Net.Mk(2);
        c.GetText("t").Insert(0, "a");
        Net.Sync(c, d);
        d.GetText("t").Insert(1, "Y");
        Assert.Equal((1u, 0L), StructsProbe.Of(d, 2).Single().Origin);        // a one-long item: its own id
    });

    [Fact]
    [Covers("STR-07")]
    public void Siblings_SkipDeletedNeighbours() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var frag = a.GetXmlFragment("x");
        frag.Push(new YType[] { new YXmlElement("a"), new YXmlElement("b"), new YXmlElement("c") });
        frag.Delete(1);

        var first = (YXmlElement)frag[0];
        var last = (YXmlElement)frag[1];
        Assert.Equal("a", first.NodeName);
        Assert.Equal("c", ((YXmlElement)first.NextSibling!).NodeName);
        Assert.Equal("a", ((YXmlElement)last.PrevSibling!).NodeName);
    });

    // ------------------------------------------------------------------ GC structs (F) and Skip structs (G)

    private static (Doc Source, List<byte[]> Updates) CollectedMap()
    {
        var a = Net.Mk(1);
        var ups = Net.Capture(a);
        var arr = a.GetArray("arr");
        arr.Insert(0, new YEntry[] { new YMap() });
        var map = (YMap)arr[0].Type!;
        map.Set("k1", 1);
        map.Set("k2", 2);
        map.Set("k3", 3);
        arr.Delete(0, 1);
        return (a, ups);
    }

    [Fact]
    [Covers("STR-31", "STR-32")]
    public void ChildrenOfACollectedType_BecomeOneMergedGcStruct() => Impl.Run(() =>
    {
        var (a, _) = CollectedMap();
        var s = StructsProbe.Of(a, 1);
        Assert.Equal(2, s.Count);
        Assert.Equal((0L, 1L, true, 1), (s[0].Clock, s[0].Len, s[0].Deleted, s[0].Ref));   // the type's own item keeps a ContentDeleted shell
        Assert.True(s[1].IsGc);                                                            // GC: ref 0, always deleted
        Assert.Equal((1L, 3L, 0, true), (s[1].Clock, s[1].Len, s[1].Ref, s[1].Deleted));  // three adjacent GCs merged into one of length 3
    });

    [Fact]
    [Covers("STR-16", "STR-33", "STR-34", "STR-35")]
    public void GcStruct_IsWrittenFromAnOffset_AndIntegratedWithOne_AndNeverWaitsForAnything() => Impl.Run(() =>
    {
        var (a, ups) = CollectedMap();
        var d = Net.Mk(9);
        d.ApplyUpdate(ups[0]);      // the map
        d.ApplyUpdate(ups[1]);      // k1 (clock 1): d now knows clocks 0..1 of the 4-clock GC range
        Assert.Equal(2L, d.GetStateVector()[1]);

        Net.Sync(a, d);             // a writes the GC range from offset 1; d integrates it with offset 1

        Assert.Equal(a.GetStateVector()[1], d.GetStateVector()[1]);
        Assert.Null(StructuralDiff.Find(StructsProbe.DumpJson(a), StructsProbe.DumpJson(d)));
        Assert.Equal(0, d.GetArray("arr").Length);
        Assert.Equal(Net.Hex(a.EncodeStateAsUpdate()), Net.Hex(d.EncodeStateAsUpdate()));   // nothing is held back as pending
    });

    private static readonly string MergedWithSkipV1 = "010301000401017401610a01840101016300";
    private static readonly string MergedWithSkipV2 = "00000241000102000504000a008406037461634101010100000103000100";

    [Fact]
    [Covers("STR-16", "STR-36", "STR-37", "STR-38", "STR-39")]
    public void MergingAcrossAMissingRange_WritesASkip_ThatIsReadButNeverStored() => Impl.Run(() =>
    {
        foreach (var format in new[] { UpdateFormat.V1, UpdateFormat.V2 })
        {
            var a = Net.Mk(1);
            var ups = new List<byte[]>();
            a.Update += (u, _, _, _) => ups.Add(u.ToArray());
            var v2 = new List<byte[]>();
            a.UpdateV2 += (u, _, _, _) => v2.Add(u.ToArray());
            var t = a.GetText("t");
            t.Insert(0, "a");
            t.Insert(1, "b");
            t.Insert(2, "c");
            var source = format == UpdateFormat.V1 ? ups : v2;

            var w = new System.Buffers.ArrayBufferWriter<byte>();
            Assert.True(YUpdate.TryMerge(new System.ReadOnlyMemory<byte>[] { source[0], source[2] }, w, format).Ok);
            // Skip is info 0x0a followed by a raw VarUint length (1): the same bytes in V1 and V2, unlike GC whose V2 length is RLE.
            HexAssert.Equal(Net.FromHex(format == UpdateFormat.V1 ? MergedWithSkipV1 : MergedWithSkipV2), w.WrittenSpan, format.ToString());

            var d = Net.Mk(9);
            d.ApplyUpdate(w.WrittenSpan, null, format);
            Assert.Equal("a", d.GetText("t").ToString());                      // the struct after the Skip waits as pending
            Assert.DoesNotContain(StructsProbe.All(d), s => s.Kind == "skip"); // Skip never enters the store
            Assert.Equal(1L, d.GetStateVector()[1]);

            d.ApplyUpdate(source[1], null, format);
            Assert.Equal("abc", d.GetText("t").ToString());
            Assert.DoesNotContain(StructsProbe.All(d), s => s.Kind == "skip");
            Assert.DoesNotContain(StructsProbe.All(a), s => s.Kind == "skip"); // a state update never contains Skips
        }
    });
}
