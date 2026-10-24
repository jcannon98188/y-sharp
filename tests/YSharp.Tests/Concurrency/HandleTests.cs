using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Xunit;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Concurrency;

/// <summary>CC-8, CC-9 and CC-10: thread-safe handles, views that do not outlive their transaction, lock-free options. Tier: gating. No seeds.</summary>
[Collection(ConcurrencyCollection.Name)]
[Tier(Tier.Gating)]
public sealed class HandleTests
{
    private sealed class Fixture
    {
        public Doc Doc { get; } = Conc.NewDoc();
        public YArray Arr { get; }
        public YMap Map { get; }
        public YText Text { get; }
        public byte[] RemoteUpdate { get; }

        public Fixture()
        {
            Arr = Doc.GetArray("a");
            Map = Doc.GetMap("m");
            Text = Doc.GetText("t");
            Arr.Push(new YEntry[] { 1, 2, 3 });
            Map.Set("k", 1);
            Text.Insert(0, "hello");
            var remote = Conc.NewDoc(2);
            remote.GetArray("r").Push(Conc.One);
            RemoteUpdate = remote.EncodeStateAsUpdate();
        }

        public Action Member(string name) => name switch
        {
            "array.Length" => () => GC.KeepAlive(Arr.Length),
            "array.Get" => () => GC.KeepAlive(Arr[0]),
            "array.ToArray" => () => GC.KeepAlive(Arr.ToArray()),
            "array.ToJson" => () => GC.KeepAlive(Arr.ToJson()),
            "map.Count" => () => GC.KeepAlive(Map.Count),
            "map.TryGet" => () => GC.KeepAlive(Map.TryGet("k", out _)),
            "text.Length" => () => GC.KeepAlive(Text.Length),
            "text.ToString" => () => GC.KeepAlive(Text.ToString()),
            "doc.EncodeStateAsUpdate" => () => GC.KeepAlive(Doc.EncodeStateAsUpdate()),
            "doc.GetStateVector" => () => GC.KeepAlive(Doc.GetStateVector()),
            "array.Push" => () => Arr.Push(Conc.One),
            "array.Delete" => () => Arr.Delete(0),
            "map.Set" => () => Map.Set("k2", 2),
            "map.Delete" => () => Map.Delete("k"),
            "text.Insert" => () => Text.Insert(0, "x"),
            "doc.ApplyUpdate" => () => Doc.ApplyUpdate(RemoteUpdate),
            "doc.Transact" => () => Doc.Transact(static _ => { }),
            _ => throw new ArgumentException(name),
        };
    }

    public static IEnumerable<object[]> ReadMembers() =>
        new[] { "array.Length", "array.Get", "array.ToArray", "array.ToJson", "map.Count", "map.TryGet", "text.Length", "text.ToString", "doc.EncodeStateAsUpdate", "doc.GetStateVector" }
            .Select(n => new object[] { n });

    public static IEnumerable<object[]> WriteMembers() =>
        new[] { "array.Push", "array.Delete", "map.Set", "map.Delete", "text.Insert", "doc.ApplyUpdate", "doc.Transact" }.Select(n => new object[] { n });

    // ------------------------------------------------------------------ CC-8

    [Theory, MemberData(nameof(ReadMembers)), Covers("CC-8")]
    public void AReadMember_CalledOutsideATransaction_TakesAnImplicitReadTransaction(string member) => Impl.Run(() =>
    {
        var f = new Fixture();
        using (var writer = Holder.Write(f.Doc))
            Conc.AssertWaitsFor(writer, f.Member(member), member + " while a writer held the lock");
        using var reader = Holder.Read(f.Doc);
        Conc.AssertDoesNotWait(f.Member(member), member + " while only a reader held the lock");
    });

    [Theory, MemberData(nameof(WriteMembers)), Covers("CC-8")]
    public void AWriteMember_CalledOutsideATransaction_TakesAnImplicitWriteTransaction(string member) => Impl.Run(() =>
    {
        var f = new Fixture();
        using (var reader = Holder.Read(f.Doc))
            Conc.AssertWaitsFor(reader, f.Member(member), member + " while a reader held the lock");
        using var writer = Holder.Write(f.Doc);
        Conc.AssertWaitsFor(writer, f.Member(member), member + " while a writer held the lock");
    });

    [Fact, Covers("CC-8")]
    public void AnExplicitTransact_BatchesCalls_AnImplicitOneDoesNot() => Impl.Run(() =>
    {
        var f = new Fixture();
        var updates = 0;
        f.Doc.Update += (_, _, _, _) => updates++;
        f.Arr.Push(Conc.One);
        f.Arr.Push(Conc.One);
        Assert.Equal(2, updates);
        updates = 0;
        f.Doc.Transact(_ =>
        {
            f.Arr.Push(Conc.One);
            f.Arr.Push(Conc.One);
            f.Text.Insert(0, "y");
        });
        Assert.Equal(1, updates);
    });

    [Fact, Covers("CC-8")]
    public void EveryPublicMember_OfTheSharedTypes_IsCallableFromAnyThread_NoThreadAffinityIsEnforced() => Impl.Run(() =>
    {
        var f = new Fixture();
        // a document created on this thread is used, read and written from another, with no explicit transaction
        Conc.AssertDoesNotWait(() =>
        {
            f.Arr.Push(Conc.One);
            Assert.Equal(4, f.Arr.Length);
            f.Map.Set("z", 1);
            f.Text.Insert(0, "!");
            Assert.Equal("!hello", f.Text.ToString());
            f.Doc.ApplyUpdate(f.RemoteUpdate);
        }, "calls from a second thread");
    });

    // ------------------------------------------------------------------ CC-9

    [Fact, Covers("CC-9")]
    public void TransactionObject_AfterItsTransactionEnded_IsInvalid() => Impl.Run(() =>
    {
        var doc = Conc.NewDoc();
        Transaction? captured = null;
        doc.Transact(tx =>
        {
            captured = tx;
            Assert.True(tx.IsValid);
            Assert.True(tx.IsWrite);
        });
        Assert.False(captured!.IsValid);
        doc.ReadTransact(tx =>
        {
            captured = tx;
            Assert.True(tx.IsValid);
            Assert.False(tx.IsWrite);
        });
        Assert.False(captured!.IsValid);
    });

    [Fact, Covers("CC-9")]
    public void EventObject_AfterDispatchEnded_IsInvalid() => Impl.Run(() =>
    {
        var f = new Fixture();
        YEvent? captured = null;
        f.Arr.Observe((e, _) =>
        {
            captured = e;
            Assert.True(e.IsValid);
        });
        f.Arr.Push(Conc.One);
        Assert.NotNull(captured);
        Assert.False(captured!.IsValid);
    });

    [Fact, Covers("CC-9")]
    public void ALiveCollection_CapturedInsideATransaction_SnapshotsOrThrowsOnceItEnded() => Impl.Run(() =>
    {
        var doc = Conc.NewDoc();
        var arr = doc.GetArray("a");
        IReadOnlyCollection<YType>? changed = null;
        var expected = -1;
        doc.Transact(tx =>
        {
            arr.Push(Conc.One);
            changed = tx.ChangedTypes;
            expected = changed.Count;
        });
        doc.GetMap("other").Set("k", 1);   // a later transaction changes other types
        SnapshotOrThrow(() => changed!.Count, expected);
        SnapshotOrThrow(() => changed!.Count(), expected);
    });

    [Fact, Covers("CC-9")]
    public void AnEnumerator_OutsideATransaction_SnapshotsAndHoldsNoLock() => Impl.Run(() =>
    {
        var f = new Fixture();
        var enumerator = f.Arr.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        // the suspended enumerator must not hold the lock: a writer on another thread gets in and the enumerator keeps its snapshot
        Conc.AssertDoesNotWait(() => f.Arr.Push(new YEntry[] { 9, 9, 9 }), "a writer while an implicit enumerator was suspended");
        var seen = 1;
        while (enumerator.MoveNext()) seen++;
        Assert.Equal(3, seen);
        Assert.Equal(6, f.Arr.Length);
    });

    [Fact, Covers("CC-9")]
    public void AnEnumerator_TakenInsideATransaction_SnapshotsOrThrowsAfterItEnded() => Impl.Run(() =>
    {
        var f = new Fixture();
        ListEnumerator enumerator = default;
        f.Doc.Transact(_ => enumerator = f.Arr.GetEnumerator());
        f.Arr.Push(Conc.One);
        SnapshotOrThrow(() =>
        {
            var n = 0;
            while (enumerator.MoveNext()) n++;
            return n;
        }, 3);
    });

    /// <summary>ADR 0006 CC-9 allows either outcome: the old contents, or an exception (which type belongs to the exception design; see the API gaps in the Spec 12 notes).</summary>
    private static void SnapshotOrThrow(Func<int> read, int expected)
    {
        int got;
        try { got = read(); }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or YException) { return; }
        Assert.Equal(expected, got);
    }

    [Fact, Covers("CC-9")]
    public void RootCollectionSnapshot_AnotherThreadAddsRoots_DoesNotChange() => Impl.Run(() =>
    {
        var doc = Conc.NewDoc();
        doc.GetArray("a");
        var share = doc.Share;
        var before = share.Count;
        Conc.AssertDoesNotWait(() => doc.GetMap("later"), "adding a root type");
        Assert.Equal(before, share.Count);
        Assert.Equal(before + 1, doc.Share.Count);
    });

    // ------------------------------------------------------------------ CC-10

    [Fact, Covers("CC-10")]
    public void Options_AreReadWithoutTheDocumentLock_WhileAWriterHoldsIt() => Impl.Run(() =>
    {
        var doc = new Doc(new DocOptions { ClientId = 77, Guid = "guid-1", Gc = false, ShouldLoad = false });
        using var holder = Holder.Write(doc);
        ulong client = 0;
        string guid = "";
        var gc = true;
        Conc.AssertDoesNotWait(() =>
        {
            client = doc.ClientId;
            guid = doc.Guid;
            gc = doc.Gc;
            GC.KeepAlive(doc.Limits);
        }, "reading ClientId, Guid, Gc and Limits");
        Assert.Equal(77UL, client);
        Assert.Equal("guid-1", guid);
        Assert.False(gc);
        holder.Release();
        using var reader = Holder.Read(doc);
        Conc.AssertDoesNotWait(() => GC.KeepAlive(doc.ClientId), "reading ClientId while a reader held the lock");
    });

    [Fact, Covers("CC-10")]
    public void Options_AreReadLockFree_FromManyThreadsWhileWritersRun() => Impl.Run(() =>
    {
        var doc = new Doc(new DocOptions { ClientId = 5, Guid = "g" });
        using var stop = new ManualResetEventSlim(false);
        var writer = Bg.Start(() =>
        {
            var arr = doc.GetArray("a");
            while (!stop.IsSet) arr.Push(Conc.One);
        });
        var readers = Enumerable.Range(0, 3).Select(_ => Bg.Start(() =>
        {
            for (var i = 0; i < 20000; i++)
            {
                Assert.Equal(5UL, doc.ClientId);
                Assert.Equal("g", doc.Guid);
            }
        })).ToArray();
        foreach (var r in readers) r.Join();
        stop.Set();
        writer.Join();
    });
}
