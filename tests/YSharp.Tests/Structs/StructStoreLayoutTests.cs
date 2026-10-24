using System;
using System.Linq;
using Xunit;
using YSharp.Store;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Structs;

/// <summary>
/// The struct store's layout operations (TASK-4.11, ADR 0023) through its internal interface: the seam that integration, codecs and types
/// build on. Expected values are worked by hand from Yjs v13.6.33 (<c>StructStore.js</c>, <c>Item.js</c> <c>splitItem</c> and
/// <c>mergeWith</c>, the <c>Content*.splice</c> and <c>mergeWith</c> methods), not recomputed by the store.
/// </summary>
[Requires(Feature.Store)]
public sealed class StructStoreLayoutTests
{
    private const ulong A = 1;
    private const ulong B = 2;

    [Fact]
    [Covers("STR-03")]
    public void AddStruct_AtTheClientsState_Appends_AndOutOfOrder_IsAnInternalError()
    {
        var store = new StructStore();
        var parent = new Branch();
        Assert.Equal(0, store.GetState(A));

        var first = Text(store, parent, A, 0, "abc");
        store.Link(first, ItemRef.Null);
        Assert.Equal(3, store.GetState(A));

        var gap = Text(store, parent, A, 5, "x");
        Assert.Throws<YInternalException>(() => store.AddStruct(gap));
        Assert.Equal(3, store.GetState(A));
        Assert.Equal(1, store.StructCount(A));
    }

    [Fact]
    [Covers("STR-46", "STR-47")]
    public void Find_ReturnsTheStructHoldingTheClock_AtTheFirstMiddleAndLastStruct()
    {
        var store = new StructStore();
        var parent = new Branch();
        var left = ItemRef.Null;
        foreach (var (clock, text) in new[] { (0L, "ab"), (2L, "cde"), (5L, "f"), (6L, "ghij") })
        {
            var item = Text(store, parent, A, clock, text);
            store.Link(item, left);
            left = item;
        }

        Assert.Equal(new[] { 0L, 0L, 2L, 2L, 2L, 5L, 6L, 6L, 6L, 6L }, Enumerable.Range(0, 10).Select(c => store.Find(A, c).Clock));
        Assert.Throws<YInternalException>(() => store.Find(A, 10));
        Assert.Throws<YInternalException>(() => store.Find(B, 0));
    }

    [Fact]
    [Covers("STR-48", "STR-49")]
    public void GetItemCleanStart_SplitsInsideAnItem_AndLinksTheRightPartAfterTheLeft()
    {
        var store = new StructStore();
        var parent = new Branch();
        var item = Text(store, parent, A, 0, "hello");
        store.Link(item, ItemRef.Null);

        var right = store.GetItemCleanStart(A, 2);

        Assert.Equal(2L, right.Clock);
        Assert.Equal(3L, right.Length);
        Assert.Equal("he", store.GetString(item));
        Assert.Equal("llo", store.GetString(right));
        Assert.Equal(right, item.Right);
        Assert.Equal(item, right.Left);
        Assert.Equal(new ItemId(A, 1), store.OriginOf(right));     // splitItem: origin is the left part's last id
        Assert.Equal(2, store.StructCount(A));
        Assert.Equal(new[] { right.Item }, Enumerable.Range(0, store.MergeStructs.Count).Select(i => store.MergeStructs[i]));
        Assert.Equal(right, store.GetItemCleanStart(A, 2));           // already a clean start: no second split
        Assert.Equal(2, store.StructCount(A));
    }

    [Fact]
    [Covers("STR-49")]
    public void GetItemCleanEnd_SplitsAfterTheClock_AndReturnsTheLeftPart()
    {
        var store = new StructStore();
        var parent = new Branch();
        var item = Text(store, parent, A, 0, "hello");
        store.Link(item, ItemRef.Null);

        var left = store.GetItemCleanEnd(A, 1);

        Assert.Equal(item, left);
        Assert.Equal("he", store.GetString(left));
        Assert.Equal("llo", store.GetString(left.Right));
        Assert.Equal(left, store.GetItemCleanEnd(A, 1));
    }

    [Fact]
    public void Split_BetweenTheHalvesOfASurrogatePair_ReplacesBothHalvesWithTheReplacementCharacter()
    {
        // ContentString.splice (yjs#248): "a😀b" split at 2 cuts the pair; both halves become U+FFFD.
        var store = new StructStore();
        var parent = new Branch();
        var item = Text(store, parent, A, 0, "a😀b");
        store.Link(item, ItemRef.Null);

        var right = store.GetItemCleanStart(A, 2);

        Assert.Equal("a�", store.GetString(item));
        Assert.Equal("�b", store.GetString(right));
    }

    [Fact]
    public void MergeWith_TextNotAdjacentInTheBuffer_CopiesItIntoOneSlice()
    {
        // Two items of one client, clock-adjacent and linked, whose text was appended to the buffer out of order (as when another client's
        // text arrives in between). Yjs ContentString.mergeWith always concatenates, so the store must merge them too.
        var store = new StructStore();
        var parent = new Branch();
        var cdAddress = store.AppendText("cd");
        store.AppendText("zz");
        var ab = Text(store, parent, A, 0, "ab");
        store.Link(ab, ItemRef.Null);
        var cd = store.CreateItem(A, 2, new ItemId(A, 1), null, parent, default, false, ContentKind.String, cdAddress, 2);
        store.Link(cd, ab);

        Assert.Equal(1, store.TryToMergeWithLefts(A, 1));

        Assert.Equal(1, store.StructCount(A));
        Assert.Equal("abcd", store.GetString(ab));
        Assert.Equal(4L, ab.Length);
        Assert.True(ab.Right.IsNull);
        Assert.Equal(ab.Item, parent.End);
    }

    [Fact]
    public void MergeWith_DifferentRightOrigins_DoesNotMerge()
    {
        var store = new StructStore();
        var parent = new Branch();
        var ab = Text(store, parent, A, 0, "ab");
        store.Link(ab, ItemRef.Null);
        var c = store.CreateItem(A, 2, new ItemId(A, 1), new ItemId(B, 0), parent, default, false, ContentKind.String, store.AppendText("c"), 1);
        store.Link(c, ab);

        Assert.Equal(0, store.TryToMergeWithLefts(A, 1));
        Assert.Equal(2, store.StructCount(A));
    }

    [Fact]
    public void AnyValues_OfMixedKinds_RoundTrip_Split_AndMergeWhenNotAdjacent()
    {
        var store = new StructStore();
        var parent = new Branch();
        YValue[] values = { 1.5, "two", true, YValue.Null, YValue.BigInt(-7), YValue.Undefined, YValue.Array(new YValue[] { 1 }) };
        var start = store.AppendValues(values);
        store.AppendValue("noise");
        var item = store.CreateItem(A, 0, null, null, parent, default, false, ContentKind.Any, start, values.Length);
        store.Link(item, ItemRef.Null);

        Assert.Equal(values, Enumerable.Range(0, values.Length).Select(i => store.GetValue(item, i)));

        var right = store.GetItemCleanStart(A, 3);
        Assert.Equal(YValue.Null, store.GetValue(right, 0));
        Assert.Equal(3L, item.Length);

        // Put a non-adjacent run after the whole item and merge it: the store copies both runs to the end of the column.
        var tail = store.CreateItem(A, 7, new ItemId(A, 6), null, parent, default, false, ContentKind.Any, store.AppendValue("tail"), 1);
        store.Link(tail, right);
        // tryToMergeWithLefts keeps going left: the tail merges into the right part, which then merges back into the item.
        Assert.Equal(2, store.TryToMergeWithLefts(A, 2));
        Assert.Equal(1, store.StructCount(A));
        Assert.Equal(values.Append((YValue)"tail"), Enumerable.Range(0, 8).Select(i => store.GetValue(item, i)));
    }

    [Fact]
    [Covers("STR-50")]
    public void ReplaceWithGc_KeepsTheIdLengthAndSlot_AndReleasesTheContent()
    {
        var store = new StructStore();
        var parent = new Branch();
        var deletes = new DeleteRanges();
        var a = Text(store, parent, A, 0, "ab");
        store.Link(a, ItemRef.Null);
        var b = Text(store, parent, A, 2, "cd");
        store.Link(b, a);

        Assert.True(store.Delete(b, deletes));
        Assert.False(store.Delete(b, deletes));
        store.ReplaceWithGc(b);

        Assert.Equal(b, store.StructAt(A, 1));
        Assert.True(b.IsGc);
        Assert.Equal((2L, 2L), (b.Clock, b.Length));
        Assert.Null(b.Parent);
        Assert.Equal(4, store.GetState(A));
        Assert.Equal(new DeleteRange(A, 2, 2), deletes[0]);
        Assert.Equal(2, parent.Length);
    }

    [Fact]
    public void GcStructs_LongerThan2To31_KeepTheirLength_MergeWithEachOther_AndNeverSplit()
    {
        var store = new StructStore();
        const long huge = 1L << 40;
        var first = store.AddGc(A, 0, huge);
        store.AddGc(A, huge, 5);

        Assert.Equal(huge + 5, store.GetState(A));
        Assert.Equal(first, store.GetItemCleanStart(A, 10));      // a GC struct is not split (findIndexCleanStart splits items only)
        Assert.Equal(1, store.TryToMergeWithLefts(A, 1));
        Assert.Equal(huge + 5, first.Length);
        Assert.Equal(1, store.StructCount(A));
    }

    [Fact]
    public void GcContent_OfADeletedItem_KeepsALongLengthInTheDeletedForm()
    {
        var store = new StructStore();
        var parent = new Branch();
        var deletes = new DeleteRanges();
        var deleted = store.CreateItem(A, 0, null, null, parent, default, false, ContentKind.Deleted, 0, 3L << 32);
        store.Link(deleted, ItemRef.Null);

        Assert.Equal(3L << 32, deleted.Length);
        Assert.False(deleted.IsCountable);
        Assert.Equal(0, parent.Length);
        var right = store.GetItemCleanStart(A, 1L << 32);
        Assert.Equal(1L << 32, deleted.Length);
        Assert.Equal(2L << 32, right.Length);
        Assert.Equal(1, store.TryToMergeWithLefts(A, 1));
        Assert.Equal(3L << 32, deleted.Length);
    }

    [Fact]
    public void KeyedItems_ShareTheKeySlot_AndARemoteItemWithNoLeftGoesBeforeTheKeysItems()
    {
        var store = new StructStore();
        var map = new Branch();
        var deletes = new DeleteRanges();
        var v1 = store.CreateLocalItem(A, map, ItemRef.Null, ItemRef.Null, "k", true, ContentKind.Any, store.AppendValue(1), 1);
        store.Link(v1, ItemRef.Null);
        var v2 = store.CreateLocalItem(A, map, v1, ItemRef.Null, "k", true, ContentKind.Any, store.AppendValue(2), 1);
        store.Link(v2, v1);
        store.Delete(v1, deletes);
        Assert.Equal(1, store.Keys.Count);
        Assert.Equal(v2.Item, map.Map!.Get("k"));

        // A concurrent set from client B with no origin: Item.integrate starts at the key's earliest item, so it goes before v1 and is not current.
        var remote = store.CreateItem(B, 0, null, null, map, "k", true, ContentKind.Any, store.AppendValue(3), 1);
        store.Link(remote, ItemRef.Null);

        Assert.Equal(v1, remote.Right);
        Assert.Equal(remote, v1.Left);
        Assert.Equal(v2.Item, map.Map!.Get("k"));
        Assert.Equal("k", store.ParentSubOf(remote));
        Assert.Equal(1, store.Keys.Count);
        Assert.Equal(0, map.Length);
    }

    [Fact]
    public void ItemViewAndDeleteSet_ReadThroughTheStore()
    {
        var store = new StructStore();
        var parent = new Branch();
        var ranges = new DeleteRanges();
        var item = Text(store, parent, B, 0, "abc");
        store.Link(item, ItemRef.Null);
        store.Delete(item, ranges);
        ranges.Add(B, 5, 2);

        var view = new ItemView(item, store);
        var ds = new DeleteSet(ranges);

        Assert.Equal(new ItemId(B, 0), view.Id);
        Assert.True(view.IsDeleted);
        Assert.True(ds.Contains(new ItemId(B, 2)));
        Assert.False(ds.Contains(new ItemId(B, 3)));
        Assert.True(ds.Contains(new ItemId(B, 6)));
        Assert.False(ds.Contains(new ItemId(A, 0)));
        Assert.False(new DeleteSet().Contains(new ItemId(B, 0)));
    }

    [Fact]
    public void DeleteRanges_AddOverlappingAndTouchingRanges_MergeIntoSortedRanges()
    {
        var ranges = new DeleteRanges();
        ranges.Add(B, 10, 2);
        ranges.Add(A, 4, 1);
        ranges.Add(B, 0, 3);
        ranges.Add(B, 3, 7);      // touches [0,3) and [10,12)
        ranges.Add(A, 0, 2);

        Assert.Equal(
            new[] { new DeleteRange(A, 0, 2), new DeleteRange(A, 4, 1), new DeleteRange(B, 0, 12) },
            Enumerable.Range(0, ranges.Count).Select(i => ranges[i]));
    }

    private static ItemRef Text(StructStore store, Branch parent, ulong client, long clock, string text) =>
        store.CreateItem(client, clock, clock == 0 ? null : new ItemId(client, clock - 1), null, parent, default, false, ContentKind.String,
            store.AppendText(text), text.Length);
}
