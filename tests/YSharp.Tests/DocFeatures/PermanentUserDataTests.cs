using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using YSharp.TestKit.Traceability;
using static YSharp.Tests.DocFeatures.Ds;
using YSharp.TestKit.Assertions;

namespace YSharp.Tests.DocFeatures;

/// <summary>
/// PermanentUserData (PUD-01..PUD-09, docs/research/doc-features.md section 6). The oracle defers its delete-set bookkeeping with
/// <c>setTimeout</c>; here the <see cref="PermanentUserDataOptions.Defer"/> seam collects the deferred actions in a queue that each test flushes
/// at the point JS would have let the timers run. Expected stores and lookups come from running the same operations on the pinned Yjs; the oracle
/// fixtures under <c>fixtures/pud</c> replay the same behaviour end to end.
/// </summary>
public sealed class PermanentUserDataTests
{
    /// <summary>A manual stand-in for the timer queue: actions run in scheduling order when <see cref="Flush"/> is called.</summary>
    private sealed class Timers
    {
        private readonly Queue<Action> _queue = new();
        public int Pending => _queue.Count;
        public PermanentUserDataOptions Options() => new() { Defer = _queue.Enqueue };
        public PermanentUserDataOptions Options(YMap store) => new() { Defer = _queue.Enqueue, Store = store };

        public void Flush()
        {
            while (_queue.Count > 0) _queue.Dequeue()();
        }
    }

    /// <summary>The users map as <c>{"user":{"ids":[...],"ds":["hex",...]}}</c>, in the map's iteration order.</summary>
    private static string Dump(Doc doc, string store = "users")
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var kv in doc.GetMap(store))
        {
            if (!first) sb.Append(',');
            first = false;
            var user = (YMap)kv.Value.Type!;
            var ids = ((YArray)Get(user, "ids").Type!).ToArray().Select(e => ((long)e.Value.AsNumber()).ToString());
            var ds = ((YArray)Get(user, "ds").Type!).ToArray().Select(e =>
            {
                Assert.True(e.Value.TryGetBytes(out var bytes));
                return "\"" + Hex.Of(bytes) + "\"";
            });
            sb.Append('"').Append(kv.Key).Append("\":{\"ids\":[").Append(string.Join(",", ids)).Append("],\"ds\":[").Append(string.Join(",", ds)).Append("]}");
        }
        return sb.Append('}').ToString();
    }

    private static Doc Mk(uint client) => Net.Mk(client);

    // ------------------------------------------------------------------ PUD-01, PUD-03, PUD-06, PUD-08

    [Fact]
    [Covers("PUD-01", "PUD-03", "PUD-06")]
    public void StorageLayout_AnyUser_IsAUsersMapOfIdsAndV1EncodedDeleteSets() => Impl.Run(() =>
    {
        var timers = new Timers();
        var a = Mk(1);
        var pud = new PermanentUserData(a, timers.Options());
        pud.SetUserMapping(a, 1, "user a");
        var t = a.GetText("t");
        t.Insert(0, "xhi");
        t.Delete(0, 1);
        Assert.Equal("{\"user a\":{\"ids\":[1],\"ds\":[]}}", Dump(a));       // the delete set is recorded later, not inside the transaction
        timers.Flush();
        Assert.Equal("{\"user a\":{\"ids\":[1],\"ds\":[\"0101010401\"]}}", Dump(a));   // client 1, one range: clock 4, length 1

        Assert.Equal("user a", pud.GetUserByClientId(1));
        Assert.Null(pud.GetUserByClientId(3));
        Assert.Equal("user a", pud.GetUserByDeletedId(new ItemId(1, 4)));
        Assert.Null(pud.GetUserByDeletedId(new ItemId(1, 3)));
        Assert.Null(pud.GetUserByDeletedId(new ItemId(1, 5)));
        Assert.Null(pud.GetUserByDeletedId(new ItemId(9, 0)));
    });

    [Fact]
    [Covers("PUD-01", "PUD-03", "PUD-06", "PUD-08", "PUD-09")]
    public void TwoUsers_AfterSync_LookUpEachOthersDeletions() => Impl.Run(() =>
    {
        var timers = new Timers();
        var a = Mk(1);
        var b = Mk(2);
        var pa = new PermanentUserData(a, timers.Options());
        var pb = new PermanentUserData(b, timers.Options());
        pa.SetUserMapping(a, 1, "user a");
        pb.SetUserMapping(b, 2, "user b");
        a.GetText("t").Insert(0, "xhi");
        a.GetText("t").Delete(0, 1);
        b.GetText("t").Insert(0, "hxxi");
        b.GetText("t").Delete(1, 2);
        timers.Flush();
        Net.Both(a, b);
        timers.Flush();
        Net.Both(a, b);

        Assert.Equal("{\"user a\":{\"ids\":[1],\"ds\":[\"0101010401\"]},\"user b\":{\"ids\":[2],\"ds\":[\"0102010502\"]}}", Dump(a));
        Assert.Equal("{\"user b\":{\"ids\":[2],\"ds\":[\"0102010502\"]},\"user a\":{\"ids\":[1],\"ds\":[\"0101010401\"]}}", Dump(b));
        Assert.Equal("user a", pa.GetUserByClientId(1));
        Assert.Equal("user b", pa.GetUserByClientId(2));
        Assert.Equal("user a", pa.GetUserByDeletedId(new ItemId(1, 4)));
        Assert.Equal("user b", pa.GetUserByDeletedId(new ItemId(2, 5)));
        Assert.Equal("user b", pa.GetUserByDeletedId(new ItemId(2, 6)));
        Assert.Null(pa.GetUserByDeletedId(new ItemId(2, 7)));
        Assert.Equal("user a", pb.GetUserByDeletedId(new ItemId(1, 4)));    // remote delete sets merge into the lookup (PUD-08)
        Assert.Equal("user b", pb.GetUserByClientId(2));
    });

    [Fact]
    [Covers("PUD-03")]
    public void DeleteSets_LocalDeletingTransactionsPassingTheFilter_AreRecordedOnlyThen() => Impl.Run(() =>
    {
        var timers = new Timers();
        var a = Mk(1);
        var pa = new PermanentUserData(a, timers.Options());
        pa.SetUserMapping(a, 1, "u", (tx, ds) => false);
        a.GetText("t").Insert(0, "abc");
        a.GetText("t").Delete(0, 1);
        timers.Flush();
        Assert.Equal("{\"u\":{\"ids\":[1],\"ds\":[]}}", Dump(a));             // the filter rejected it

        var c = Mk(3);
        var pc = new PermanentUserData(c, timers.Options());
        pc.SetUserMapping(c, 3, "w");
        c.GetText("t").Insert(0, "zz");
        timers.Flush();
        Assert.Equal("{\"w\":{\"ids\":[3],\"ds\":[]}}", Dump(c));             // inserting only: no delete set

        var b = Mk(2);
        var pb = new PermanentUserData(b, timers.Options());
        pb.SetUserMapping(b, 2, "v");
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        b.GetText("t").Delete(0, 1);
        timers.Flush();
        Assert.Equal("v", pb.GetUserByDeletedId(new ItemId(1, 4)) == null ? "v" : "unexpected");
        c.ApplyUpdate(b.EncodeStateAsUpdate());                                // a remote transaction on c
        timers.Flush();
        Assert.Equal("{\"w\":{\"ids\":[3],\"ds\":[]},\"v\":{\"ids\":[2],\"ds\":[\"0101010501\"]},\"u\":{\"ids\":[1],\"ds\":[]}}", Dump(c));
    });

    // ------------------------------------------------------------------ PUD-02

    [Fact]
    [Covers("PUD-02", "PUD-01", "PUD-06", "PUD-08")]
    public void Constructing_OverExistingData_RebuildsTheLookups() => Impl.Run(() =>
    {
        var timers = new Timers();
        var a = Mk(3);
        var pa = new PermanentUserData(a, timers.Options());
        pa.SetUserMapping(a, 3, "user a");
        a.GetText("t").Insert(0, "hello");
        a.GetText("t").Delete(1, 2);
        timers.Flush();

        var c = Mk(5);
        c.ApplyUpdate(a.EncodeStateAsUpdate());
        var pc = new PermanentUserData(c, timers.Options());
        Assert.Equal("{\"user a\":{\"ids\":[3],\"ds\":[\"0103010502\"]}}", Dump(c));
        Assert.Equal("user a", pc.GetUserByClientId(3));
        Assert.Null(pc.GetUserByClientId(5));
        Assert.Null(pc.GetUserByDeletedId(new ItemId(3, 4)));
        Assert.Equal("user a", pc.GetUserByDeletedId(new ItemId(3, 5)));
        Assert.Equal("user a", pc.GetUserByDeletedId(new ItemId(3, 6)));
        Assert.Null(pc.GetUserByDeletedId(new ItemId(3, 7)));
    });

    [Fact]
    [Covers("PUD-02")]
    public void UsersMap_WithAnotherStore_IsUsedInPlaceOfTheDefault() => Impl.Run(() =>
    {
        var timers = new Timers();
        var a = Mk(1);
        var store = a.GetMap("people");
        var pud = new PermanentUserData(a, timers.Options(store));
        pud.SetUserMapping(a, 1, "p");
        Assert.Equal("{\"p\":{\"ids\":[1],\"ds\":[]}}", Dump(a, "people"));
        Assert.DoesNotContain("users", a.Share.Keys);
    });

    // ------------------------------------------------------------------ PUD-04

    [Fact]
    [Covers("PUD-04", "PUD-09", "PUD-03")]
    public void ConcurrentlyCreatedUsers_OnMerge_AreMergedIntoTheWinningEntry() => Impl.Run(() =>
    {
        var timers = new Timers();
        var a = Mk(1);
        var b = Mk(2);
        var pa = new PermanentUserData(a, timers.Options());
        var pb = new PermanentUserData(b, timers.Options());
        pa.SetUserMapping(a, 1, "user a");
        pb.SetUserMapping(b, 2, "user a");                                      // same description, created concurrently
        a.GetText("t").Insert(0, "abc");
        a.GetText("t").Delete(0, 1);
        b.GetText("t").Insert(0, "xyz");
        b.GetText("t").Delete(0, 1);
        timers.Flush();
        for (var round = 0; round < 3; round++)
        {
            Net.Both(a, b);
            timers.Flush();
        }
        Net.Both(a, b);
        const string expected = "{\"user a\":{\"ids\":[2,1,2],\"ds\":[\"0102010401\",\"0102010401\"]}}";
        Assert.Equal(expected, Dump(a));
        Assert.Equal(expected, Dump(b));
        Assert.Equal("user a", pa.GetUserByClientId(1));
        Assert.Equal("user a", pa.GetUserByClientId(2));
        Assert.Equal("user a", pa.GetUserByDeletedId(new ItemId(2, 4)));
        Assert.Equal("user a", pb.GetUserByDeletedId(new ItemId(2, 4)));
        Assert.Null(pb.GetUserByDeletedId(new ItemId(1, 4)));                   // client 1's own record was lost with its overwritten entry, as upstream
    });

    // ------------------------------------------------------------------ PUD-05, PUD-06, PUD-09

    [Fact]
    [Covers("PUD-05")]
    public void SetUserMapping_CalledTwice_RecordsEveryDeletionTwice() => Impl.Run(() =>
    {
        var timers = new Timers();
        var a = Mk(1);
        var pud = new PermanentUserData(a, timers.Options());
        pud.SetUserMapping(a, 1, "u");
        pud.SetUserMapping(a, 1, "u");
        a.GetText("t").Insert(0, "abc");
        a.GetText("t").Delete(0, 1);
        timers.Flush();
        Assert.Equal("{\"u\":{\"ids\":[1,1],\"ds\":[\"0101010501\",\"0101010501\"]}}", Dump(a));
    });

    [Fact]
    [Covers("PUD-06", "PUD-09")]
    public void GetUserByDeletedId_ReturnsTheFirstUserInInsertionOrder_AndTheLastWriterWinsForAClientId() => Impl.Run(() =>
    {
        var timers = new Timers();
        var a = Mk(1);
        var pud = new PermanentUserData(a, timers.Options());
        pud.SetUserMapping(a, 1, "x");
        pud.SetUserMapping(a, 1, "y");                                          // the same client under two descriptions
        a.GetText("t").Insert(0, "abc");
        a.GetText("t").Delete(0, 1);
        timers.Flush();
        Assert.Equal("{\"x\":{\"ids\":[1],\"ds\":[\"0101010801\"]},\"y\":{\"ids\":[1],\"ds\":[\"0101010801\"]}}", Dump(a));
        Assert.Equal("x", pud.GetUserByDeletedId(new ItemId(1, 8)));
        Assert.Equal("y", pud.GetUserByClientId(1));
    });

    [Fact]
    [PortOf("encoding.tests.js:testPermanentUserData", "PUD-04", "PUD-06")]
    public void Port_PermanentUserData_ThirdDocWithTheSameDescriptionAfterASync() => Impl.Run(() =>
    {
        var timers = new Timers();
        var ydoc1 = new Doc();
        var ydoc2 = new Doc();
        var pd1 = new PermanentUserData(ydoc1, timers.Options());
        var pd2 = new PermanentUserData(ydoc2, timers.Options());
        pd1.SetUserMapping(ydoc1, ydoc1.ClientId, "user a");
        pd2.SetUserMapping(ydoc2, ydoc2.ClientId, "user b");
        ydoc1.GetText().Insert(0, "xhi");
        ydoc1.GetText().Delete(0, 1);
        ydoc2.GetText().Insert(0, "hxxi");
        ydoc2.GetText().Delete(1, 2);
        timers.Flush();
        ydoc2.ApplyUpdate(ydoc1.EncodeStateAsUpdate());
        ydoc1.ApplyUpdate(ydoc2.EncodeStateAsUpdate());
        Assert.Equal("user b", pd1.GetUserByClientId(ydoc2.ClientId));

        // now sync a third doc with the same description as doc1 and then create PermanentUserData
        var ydoc3 = new Doc();
        ydoc3.ApplyUpdate(ydoc1.EncodeStateAsUpdate());
        var pd3 = new PermanentUserData(ydoc3, timers.Options());
        pd3.SetUserMapping(ydoc3, ydoc3.ClientId, "user a");
        timers.Flush();
        Assert.Equal("user a", pd3.GetUserByClientId(ydoc3.ClientId));
        Assert.Equal("user a", pd3.GetUserByClientId(ydoc1.ClientId));
    });

    // ------------------------------------------------------------------ PUD-07

    [Fact]
    [Diverges("PUD-07", adr: "0003")]
    public void DeletingAUserKey_DoesNotCrash_WhereTheOracleThrowsInsideTheObserver() => Impl.Run(() =>
    {
        // JS: users.delete('u') makes the store observer call initUser(undefined, 'u'), which throws "Cannot read properties of undefined (reading 'get')"
        // out of the transaction (and the pending setTimeout callbacks of setUserMapping throw later). y-sharp must stay usable (ADR 0003, ADR 0005).
        var timers = new Timers();
        var a = Mk(1);
        var pud = new PermanentUserData(a, timers.Options());
        pud.SetUserMapping(a, 1, "u");
        timers.Flush();
        var ex = Record.Exception(() =>
        {
            a.GetMap("users").Delete("u");
            timers.Flush();
        });
        Assert.Null(ex);
        Assert.Null(Record.Exception(() => pud.GetUserByClientId(1)));
        a.GetText("t").Insert(0, "still usable");
        Assert.Equal("still usable", a.GetText("t").ToString());
    });
}
