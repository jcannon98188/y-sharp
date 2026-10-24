using System;
using System.Linq;
using Xunit;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Structs;

/// <summary>Item helper functions (docs/research/structs.md section B): followRedone, keepItem, splitItem, redoItem.</summary>
public sealed class ItemHelperTests
{
    private static readonly UndoManagerOptions Immediate = new() { CaptureTimeout = TimeSpan.Zero };

    private static double[] Numbers(YArray arr) => arr.ToArray().Select(e => e.Value.AsNumber()).ToArray();

    [Fact]
    [Covers("STR-05", "STR-10", "GC-03")]
    public void KeepItem_MarksTheItemAndEveryAncestor_AndStopsAtTheRoot() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var arr = a.GetArray("arr");
        arr.Insert(0, new YEntry[] { new YMap() });
        var map = (YMap)arr[0].Type!;
        map.Set("k", "v");
        map.Set("j", "w");
        using var um = new UndoManager(arr, Immediate);
        um.StopCapturing();

        map.Delete("k");

        var s = StructsProbe.Of(a, 1);
        Assert.Equal(new[] { true, true, false }, s.Select(x => x.Keep));       // the map's own item, deleted "k", live "j"
        Assert.Equal(new[] { false, true, false }, s.Select(x => x.Deleted));
        Assert.Equal("k", s[1].ParentSub);
        Assert.Equal(7, s[0].Ref);
    });

    [Fact]
    [Covers("STR-11")]
    public void SplitItem_KeepsOriginsAndRightOrigin_AndOnlyTheDeletedPartEntersTheDeleteSet() => Impl.Run(() =>
    {
        var a = Net.Mk(1, gc: false);
        var b = Net.Mk(2, gc: false);
        a.GetText("t").Insert(0, "abc");
        a.GetText("t").Insert(0, "XYZ");     // origin null, right origin = (1,0)
        Net.Sync(a, b);

        b.GetText("t").Delete(1, 1);         // splits "XYZ" into X | Y | Z

        var s = StructsProbe.Of(b, 1);
        Assert.Equal(new[] { (0L, 3L), (3L, 1L), (4L, 1L), (5L, 1L) }, s.Select(x => (x.Clock, x.Len)));
        Assert.Equal(new[] { "abc", "X", "Y", "Z" }, s.Select(x => x.Text));
        Assert.Equal(new (uint, long)?[] { null, null, (1u, 3L), (1u, 4L) }, s.Select(x => x.Origin));            // right half: origin = (client, clock + diff - 1)
        Assert.Equal(new (uint, long)?[] { null, (1u, 0L), (1u, 0L), (1u, 0L) }, s.Select(x => x.RightOrigin));    // copied to the right half, left half unchanged
        Assert.Equal(new[] { false, false, true, false }, s.Select(x => x.Deleted));
        Assert.Equal("[[1,[[4,1]]]]", StructsProbe.DeleteSetJson(StructsProbe.Dump(b)));
        Assert.Equal("abcXZ", b.GetText("t").ToString());
    });

    [Fact]
    [Covers("STR-12")]
    public void RedoItem_ReinsertsADeletedArrayItem_InTheSameNeighbourhood() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var arr = a.GetArray("arr");
        arr.Push(new YEntry[] { 1, 2, 3 });
        using var um = new UndoManager(arr, Immediate);

        arr.Delete(1, 1);
        um.Undo();
        Assert.Equal(new double[] { 1, 2, 3 }, Numbers(arr));
        um.Redo();
        Assert.Equal(new double[] { 1, 3 }, Numbers(arr));
        um.Undo();
        Assert.Equal(new double[] { 1, 2, 3 }, Numbers(arr));

        // The re-inserted items are new structs between the same neighbours and are kept.
        var last = StructsProbe.Of(a, 1).Last();
        Assert.Equal(((uint, long)?)(1u, 0L), last.Origin);
        Assert.False(last.Deleted);
        Assert.True(last.Keep);
    });

    [Fact]
    [Covers("STR-12")]
    public void RedoItem_OfAMapEntry_DoesNotOverrideAConcurrentRemoteChange() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var b = Net.Mk(2);
        var ma = a.GetMap("m");
        ma.Set("k", "base");
        Net.Sync(a, b);
        using var um = new UndoManager(ma, Immediate);

        ma.Set("k", "A");
        um.Undo();
        Assert.True(ma.TryGet("k", out var afterUndo));
        Assert.Equal("base", afterUndo.Value.AsString());

        b.GetMap("m").Set("k", "B");
        Net.Sync(b, a);
        um.Redo();                                   // the entry has a newer value from another client: redo returns null

        Assert.True(ma.TryGet("k", out var finalValue));
        Assert.Equal("B", finalValue.Value.AsString());
    });

    [Fact]
    [Covers("STR-12")]
    public void RedoItem_ChasesARedoneParent_WhenTheParentWasDeletedAndRestored() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var arr = a.GetArray("arr");
        using var um = new UndoManager(arr, Immediate);
        arr.Insert(0, new YEntry[] { new YMap() });
        um.StopCapturing();
        ((YMap)arr[0].Type!).Set("k", "v");
        um.StopCapturing();
        arr.Delete(0, 1);

        um.Undo();                                   // restores the map and, through the redone parent, its entry
        Assert.Equal(1, arr.Length);
        Assert.True(((YMap)arr[0].Type!).Has("k"));
        um.Undo();                                   // removes "k" again
        Assert.False(((YMap)arr[0].Type!).Has("k"));
        um.Redo();
        Assert.True(((YMap)arr[0].Type!).Has("k"));
        um.Redo();                                   // redoes the deletion of the map
        Assert.Equal(0, arr.Length);
    });

    [Fact]
    [Covers("STR-09")]
    public void FollowRedone_DeleteAndUndo_LetsARelativePositionSurvive() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var t = a.GetText("t");
        t.Insert(0, "abcd");
        var position = t.CreateRelativePosition(2);
        using var um = new UndoManager(t, Immediate);

        t.Delete(1, 2);
        Assert.Equal(1, position.Resolve(a)!.Value.Index);

        um.Undo();                                   // "bc" comes back as new structs; the old ones point at them through `redone`
        Assert.Equal("abcd", t.ToString());
        Assert.Equal(2, position.Resolve(a, followUndoneDeletions: true)!.Value.Index);
        Assert.Equal(3, position.Resolve(a, followUndoneDeletions: false)!.Value.Index);
    });

    [Fact]
    [Covers("GC-05", "GC-02")]
    public void UndoKeep_ProtectsDeletedItems_ButClearDoesNotCollectThemRetroactively() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var t = a.GetText("t");
        t.Insert(0, "abcdef");
        var um = new UndoManager(t, Immediate);

        t.Delete(1, 2);
        var kept = StructsProbe.Of(a, 1).Single(s => s.Deleted);
        Assert.True(kept.Keep);
        Assert.Equal("bc", kept.Text);               // GC ran at the end of the transaction but spared the kept item

        um.Clear();
        t.Insert(0, "q");                            // GC only looks at the current transaction's delete set (GC-02)
        var after = StructsProbe.Of(a, 1).Single(s => s.Deleted);
        Assert.False(after.Keep);
        Assert.Equal("bc", after.Text);
        um.Dispose();
    });
}
