using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Structs;

/// <summary>Content types (docs/research/structs.md section H): the content table, string splitting, ContentType and ContentDoc.</summary>
[Requires(Feature.Gc)]
public sealed class ContentTests
{
    private static YValue Obj(string key, YValue value) => StructModelTests.Attr(key, value);

    [Fact]
    [Covers("STR-30", "STR-44")]
    [Requires(Feature.TextFormat, Feature.Text, Feature.Array, Feature.Map)]
    public void EveryProducibleContentKind_HasItsRef_AndSurvivesAWireRoundTrip()
    {
        var a = Net.Mk(1, gc: false);
        var t = a.GetText("t");
        t.Insert(0, "hi", StructModelTests.Attr("b", true));        // Format (6), String (4), Format (6)
        t.InsertEmbed(2, Obj("img", 1));                              // Embed (5)
        var arr = a.GetArray("arr");
        arr.Push(new YEntry[] { 1, YValue.Bytes(new byte[] { 1 }), new YMap() });   // Any (8), Binary (3), Type (7)
        arr.Delete(0, 1);                                             // gc off: the deleted item keeps its Any content

        var refs = StructsProbe.Of(a, 1).Select(s => s.Ref).ToHashSet();
        Assert.Equal(new[] { 3, 4, 5, 6, 7, 8 }, refs.OrderBy(r => r));

        var b = Net.Mk(2, gc: false);
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        Assert.Null(StructuralDiff.Find(StructsProbe.DumpJson(a), StructsProbe.DumpJson(b)));
        Assert.Equal(Hex.Of(a.EncodeStateAsUpdate()), Hex.Of(b.EncodeStateAsUpdate()));

        // Deleted content with gc on is ContentDeleted (1).
        var c = Net.Mk(3);
        c.GetArray("arr").Push(new YEntry[] { 1, 2 });
        c.GetArray("arr").Delete(0, 2);
        Assert.Equal(new[] { 1 }, StructsProbe.Of(c, 3).Select(s => s.Ref));
    }

    [Fact]
    [Covers("STR-41")]
    [Requires(Feature.Text)]
    public void SplittingStringContent_InsideASurrogatePair_ReplacesBothHalves_AndOnlyThere()
    {
        var inside = Net.Mk(1, gc: false);
        inside.GetText("t").Insert(0, "a\U0001F600b");
        inside.GetText("t").Delete(2, 1);                              // between the high and the low surrogate
        var s = StructsProbe.Of(inside, 1);
        Assert.Equal(new[] { "a�", "�", "b" }, s.Select(x => x.Text));
        Assert.Equal(new[] { 2L, 1L, 1L }, s.Select(x => x.Len));      // lengths stay in UTF-16 code units
        Assert.Equal(new[] { false, true, false }, s.Select(x => x.Deleted));
        Assert.Equal("a�b", inside.GetText("t").ToString());
        Assert.Equal(3, inside.GetText("t").Length);

        var boundary = Net.Mk(2, gc: false);
        boundary.GetText("t").Insert(0, "a\U0001F600b");
        boundary.GetText("t").Delete(1, 2);                            // the whole pair
        var s2 = StructsProbe.Of(boundary, 2);
        Assert.Equal(new[] { "a", "\U0001F600", "b" }, s2.Select(x => x.Text));
        Assert.Equal("ab", boundary.GetText("t").ToString());

        var edge = Net.Mk(3, gc: false);
        edge.GetText("t").Insert(0, "\U0001F600b");
        edge.GetText("t").Delete(0, 1);                                // a split right after the high surrogate
        Assert.Equal(new[] { "�", "�b" }, StructsProbe.Of(edge, 3).Select(x => x.Text));
    }

    [Fact]
    [Covers("STR-42", "GC-04")]
    [Requires(Feature.Text, Feature.Array, Feature.Events)]
    public void DeletingAType_DeletesEveryDescendant_WithoutEventsForTheDeletedType()
    {
        var a = Net.Mk(1, gc: false);
        var arr = a.GetArray("arr");
        arr.Insert(0, new YEntry[] { new YText("abc") });
        var nested = (YText)arr[0].Type!;
        nested.Delete(0, 1);                                           // an old tombstone inside the type

        var events = 0;
        nested.Observe((_, _) => events++);
        var changedCount = -1;
        var inDeleteSet = new Dictionary<long, bool>();
        a.AfterTransaction += (tx, _) =>
        {
            changedCount = tx.ChangedTypes.Count;                      // the transaction object is pooled: read it inside the handler
            for (var clock = 0L; clock <= 3; clock++) inDeleteSet[clock] = tx.DeleteSet.Contains(new ItemId(1, clock));
        };
        arr.Delete(0, 1);

        Assert.Equal(0, events);                                       // no change events for a type that is itself deleted
        Assert.Equal(1, changedCount);                                 // only the array changed
        Assert.True(inDeleteSet[0]);                                   // the type's own item
        Assert.False(inDeleteSet[1]);                                  // the old tombstone was deleted by an earlier transaction
        Assert.True(inDeleteSet[2] && inDeleteSet[3]);                 // the live children
        Assert.All(StructsProbe.Of(a, 1), s => Assert.True(s.Deleted));
    }

    [Fact]
    [Covers("STR-42", "GC-04")]
    [Requires(Feature.Text, Feature.Array)]
    public void CollectingADeletedType_TurnsItsChildrenIntoGcStructs_AndLeavesAContentDeletedShell()
    {
        var a = Net.Mk(1);
        var arr = a.GetArray("arr");
        arr.Insert(0, new YEntry[] { new YText("abc") });
        arr.Delete(0, 1);

        var s = StructsProbe.Of(a, 1);
        Assert.Equal(2, s.Count);
        Assert.Equal((0L, 1L, 1, true), (s[0].Clock, s[0].Len, s[0].Ref, s[0].Deleted));   // the Item shell with ContentDeleted
        Assert.Equal((1L, 3L, true), (s[1].Clock, s[1].Len, s[1].IsGc));                   // children: one merged GC
    }

    [Fact]
    [Covers("STR-43")]
    [Requires(Feature.Map, Feature.Subdocs)]
    public void ContentDoc_CarriesGuidAndOptions_ThroughTheWire_AndRaisesSubdocEventsOnTheReceiver()
    {
        var a = Net.Mk(1);
        var sub = new Doc(new DocOptions { Guid = "g", AutoLoad = true, Gc = false });
        a.GetMap("m").Set("s", sub);

        var item = Assert.Single(StructsProbe.Of(a, 1));
        Assert.Equal(9, item.Ref);
        Assert.Equal("g", item.Value!.Value[0].GetString());
        Assert.Null(StructuralDiff.Find("{\"$\":\"obj\",\"pairs\":[[\"gc\",false],[\"autoLoad\",true]]}", item.Value!.Value[1].GetRawText()));

        var b = Net.Mk(2);
        var added = new List<string>();
        var loaded = new List<string>();
        b.SubdocsChanged += (change, _, _) =>
        {
            added.AddRange(change.Added.Select(d => d.Guid));
            loaded.AddRange(change.Loaded.Select(d => d.Guid));
        };
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        Assert.Equal(new[] { "g" }, added);
        Assert.Equal(new[] { "g" }, loaded);                           // autoLoad implies shouldLoad
        Assert.Null(StructuralDiff.Find(StructsProbe.DumpJson(a), StructsProbe.DumpJson(b)));
    }

    [Fact]
    [Covers("STR-43", "GC-09")]
    [Requires(Feature.Map, Feature.Subdocs)]
    public void ContentDoc_AddedAndDeletedInOneTransaction_ReportsNeitherAddedNorRemoved()
    {
        var a = Net.Mk(1);
        var m = a.GetMap("m");
        var changes = new List<(int Added, int Removed)>();
        a.SubdocsChanged += (c, _, _) => changes.Add((c.Added.Count, c.Removed.Count));

        a.Transact(_ =>
        {
            m.Set("s", new Doc(new DocOptions { Guid = "s1" }));
            m.Delete("s");
        });
        Assert.DoesNotContain(changes, c => c.Added > 0 || c.Removed > 0);

        changes.Clear();
        m.Set("s2", new Doc(new DocOptions { Guid = "s2" }));
        m.Delete("s2");                                                // a later transaction: reported as removed
        Assert.Contains(changes, c => c.Added == 1);
        Assert.Contains(changes, c => c.Removed == 1);
    }

    [Fact]
    [Covers("STR-44")]
    [Requires(Feature.Array)]
    public void NonSplittableContent_IsDeletedAsWholeItems_NeverSplit()
    {
        var a = Net.Mk(1, gc: false);
        var b = Net.Mk(2, gc: false);
        var arr = a.GetArray("arr");
        arr.Push(new YEntry[] { YValue.Bytes(new byte[] { 1 }), YValue.Bytes(new byte[] { 2 }), YValue.Bytes(new byte[] { 3 }) });
        Net.Sync(a, b);
        arr.Delete(1, 1);
        Net.Sync(a, b);

        var s = StructsProbe.Of(b, 1);
        Assert.Equal(new[] { (0L, 1L), (1L, 1L), (2L, 1L) }, s.Select(x => (x.Clock, x.Len)));
        Assert.Equal(new[] { false, true, false }, s.Select(x => x.Deleted));
        Assert.All(s, x => Assert.Equal(3, x.Ref));
    }

    [Fact]
    [Covers("STR-45")]
    [Requires(Feature.Array)]
    public void AdjacentItems_DifferentContentKinds_AreNotMerged()
    {
        var a = Net.Mk(1);
        var arr = a.GetArray("arr");
        arr.Push(new YEntry[] { 1 });
        arr.Push(new YEntry[] { "s" });
        arr.Push(new YEntry[] { YValue.Bytes(new byte[] { 1 }) });
        arr.Push(new YEntry[] { 2 });

        var s = StructsProbe.Of(a, 1);
        Assert.Equal(new[] { (0L, 2L, 8), (2L, 1L, 3), (3L, 1L, 8) }, s.Select(x => (x.Clock, x.Len, x.Ref)));   // [1,"s"] merged, bytes and 2 stay apart
    }
}
