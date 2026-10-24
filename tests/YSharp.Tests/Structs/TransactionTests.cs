using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Structs;

/// <summary>Transactions and their cleanup (docs/research/structs.md section J), observed through the public events.</summary>
public sealed class TransactionTests
{
    private static List<string> LogEvents(Doc d)
    {
        var log = new List<string>();
        d.BeforeAllTransactions += (_, _) => log.Add("beforeAllTransactions");
        d.BeforeTransaction += (_, _) => log.Add("beforeTransaction");
        d.BeforeObserverCalls += (_, _) => log.Add("beforeObserverCalls");
        d.AfterTransaction += (_, _) => log.Add("afterTransaction");
        d.AfterTransactionCleanup += (_, _) => log.Add("afterTransactionCleanup");
        d.Update += (_, _, _, _) => log.Add("update");
        d.UpdateV2 += (_, _, _, _) => log.Add("updateV2");
        d.AfterAllTransactions += (_, _) => log.Add("afterAllTransactions");
        return log;
    }

    [Fact]
    [Covers("STR-53", "STR-56")]
    public void ATransaction_SnapshotsTheStateBefore_FillsTheStateAfter_AndNewItemsContinueTheOwnClock() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var t = a.GetText("t");
        t.Insert(0, "abc");
        (long Before, long After)? seen = null;
        a.AfterTransaction += (tx, _) => seen = (tx.BeforeState[1], tx.AfterState[1]);
        t.Insert(3, "d");
        Assert.Equal((3L, 4L), seen);
        Assert.Equal((0L, 4L), (StructsProbe.Of(a, 1)[0].Clock, StructsProbe.Of(a, 1)[0].End));

        var b = Net.Mk(2);
        Net.Sync(a, b);
        b.GetText("t").Insert(1, "X");                       // nextID = (own client, current own state)
        b.GetText("t").Insert(0, "Y");
        Assert.Equal(new[] { (0L, 1L), (1L, 1L) }, StructsProbe.Of(b, 2).Select(s => (s.Clock, s.Len)));
        Assert.Equal(2L, b.GetStateVector()[2]);
    });

    [Fact]
    [Covers("STR-54", "STR-58")]
    public void EventOrder_ForOneTransaction_IsFixed() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var t = a.GetText("t");
        var log = LogEvents(a);
        t.Observe((_, _) => log.Add("observe"));

        t.Insert(0, "a");
        Assert.Equal(new[]
        {
            "beforeAllTransactions", "beforeTransaction", "beforeObserverCalls", "observe",
            "afterTransaction", "afterTransactionCleanup", "update", "updateV2", "afterAllTransactions",
        }, log);

        log.Clear();
        a.Transact(_ => { });                                 // nothing changed: no observers and no update
        Assert.Equal(new[]
        {
            "beforeAllTransactions", "beforeTransaction", "beforeObserverCalls",
            "afterTransaction", "afterTransactionCleanup", "afterAllTransactions",
        }, log);
    });

    [Fact]
    [Covers("STR-54")]
    public void ANestedTransact_JoinsTheOuterTransaction_AndKeepsItsOrigin() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var seen = new List<(object? Origin, bool Local)>();
        a.AfterTransaction += (tx, _) => seen.Add((tx.Origin, tx.IsLocal));
        var updates = Net.Capture(a);

        a.Transact(outer => a.Transact(inner => a.GetText("t").Insert(0, "x"), "inner"), "outer");

        Assert.Equal(new (object?, bool)[] { ("outer", true) }, seen);
        Assert.Single(updates);
    });

    [Fact]
    [Covers("STR-54")]
    public void ATransactionOpenedByAnObserver_IsQueued_AndRunsAfterTheFirstOnesCleanup() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var t = a.GetText("t");
        var log = new List<string>();
        a.BeforeAllTransactions += (_, _) => log.Add("beforeAll");
        a.AfterAllTransactions += (_, _) => log.Add("afterAll");
        a.AfterTransaction += (_, _) => log.Add("after");
        a.Update += (_, _, _, _) => log.Add("update");
        var first = true;
        t.Observe((_, _) =>
        {
            log.Add(first ? "observer 1" : "observer 2");
            if (first)
            {
                first = false;
                t.Insert(1, "B");
                log.Add("observer 1 done");
            }
        });

        t.Insert(0, "A");

        Assert.Equal(new[] { "beforeAll", "observer 1", "observer 1 done", "after", "update", "observer 2", "after", "update", "afterAll" }, log);
        Assert.Equal("AB", t.ToString());
    });

    [Fact]
    [Covers("STR-54", "STR-73")]
    public void ABodyThatThrows_StillCleansUp_CommitsAndEmitsTheUpdate_ThenPropagates() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var t = a.GetText("t");
        var log = new List<string>();
        a.AfterTransaction += (_, _) => log.Add("after");
        a.Update += (_, _, _, _) => log.Add("update");

        var ex = Assert.Throws<InvalidOperationException>(() => a.Transact(_ =>
        {
            t.Insert(0, "x");
            throw new InvalidOperationException("boom");
        }));

        Assert.Equal("boom", ex.Message);
        Assert.Equal(new[] { "after", "update" }, log);
        Assert.Equal("x", t.ToString());
    });

    [Fact]
    [Covers("STR-55")]
    public void ChangedTypes_NewTypesInTheTransaction_AreNotListed() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var arr = a.GetArray("arr");
        arr.Insert(0, new YEntry[] { new YMap() });
        var existing = (YMap)arr[0].Type!;

        List<YType>? changed = null;
        a.AfterTransaction += (tx, _) => changed = tx.ChangedTypes.ToList();
        a.Transact(_ =>
        {
            arr.Insert(0, new YEntry[] { new YMap() });          // created here: not "changed"
            ((YMap)arr[0].Type!).Set("k", 1);
            existing.Set("y", 2);
        });

        Assert.Equal(2, changed!.Count);
        Assert.Contains((YType)arr, changed);
        Assert.Contains((YType)existing, changed);
    });

    [Fact]
    [Covers("STR-57")]
    public void Update_EmptyOrDeleteOnlyTransaction_EmitsNothingOrOnlyTheDeleteSet() => Impl.Run(() =>
    {
        var a = Net.Mk(2);
        var t = a.GetText("t");
        t.Insert(0, "abc");
        var updates = Net.Capture(a);

        a.Transact(_ => { });
        t.Delete(0, 0);
        Assert.Empty(updates);

        t.Delete(1, 1);
        var update = Assert.Single(updates);
        Assert.Equal("000102010101", Hex.Of(update));            // no structs, one delete range (client 2, clock 1, length 1)
    });

    [Fact]
    [Covers("STR-58", "STR-60")]
    public void Cleanup_GcsAndMerges_BeforeAfterTransactionCleanupIsRaised() => Impl.Run(() =>
    {
        var a = Net.Mk(1);
        var t = a.GetText("t");
        t.Insert(0, "a");
        string? atCleanup = null;
        a.AfterTransactionCleanup += (_, _) => atCleanup = StructsProbe.DumpJson(a);
        t.Insert(1, "b");

        var merged = StructsProbe.Of(a, 1);
        Assert.Equal((0L, 2L, "ab"), (merged[0].Clock, merged[0].Len, merged[0].Text));       // merged during cleanup
        Assert.Null(StructuralDiff.Find(StructsProbe.DumpJson(a), atCleanup!));

        t.Delete(0, 2);
        Assert.Single(StructsProbe.Of(a, 1));
        Assert.Equal(1, StructsProbe.Of(a, 1)[0].Ref);                                         // collected to ContentDeleted by then
    });
}
