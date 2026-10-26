using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using YSharp.TestKit.Time;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Concurrency;

/// <summary>CC-18..CC-20: subdoc locks, awareness, UndoManager and PermanentUserData. Tier: gating. No seeds (the schedule-driven versions are in <see cref="ScheduleScenarioTests"/>).</summary>
[Requires(Feature.Concurrency)]
[Collection(ConcurrencyCollection.Name)]
[Tier(Tier.Gating)]
public sealed class ComponentTests
{
    private static (Doc Parent, Doc Child) Pair()
    {
        var parent = Conc.NewDoc(1);
        var child = new Doc(new DocOptions { Guid = "sub" });
        parent.GetArray("subs").Push(new YEntry[] { child });
        return (parent, child);
    }

    private static YValue State(string key, double value) =>
        YValue.Object(new[] { new KeyValuePair<string, YValue>(key, value) });

    // ------------------------------------------------------------------ CC-18

    [Fact, Covers("CC-18")]
    [Requires(Feature.Array, Feature.Subdocs)]
    public void ASubdoc_HasItsOwnLock_NeitherDocumentBlocksTheOther()
    {
        var (parent, child) = Pair();
        using (var holder = Holder.Write(parent))
            Conc.AssertDoesNotWait(() => child.Transact(_ => child.GetArray("c").Push(Conc.One)), "the subdoc while the parent was write-locked");
        using (var holder = Holder.Write(child))
            Conc.AssertDoesNotWait(() => parent.Transact(_ => parent.GetArray("a").Push(Conc.One)), "the parent while the subdoc was write-locked");
    }

    [Fact, Covers("CC-18")]
    [Requires(Feature.Array)]
    public void TheHierarchy_ParentBeforeChild_IsAllowedOnOneThread()
    {
        var (parent, child) = Pair();
        parent.Transact(_ => child.Transact(_ => child.GetArray("c").Push(Conc.One)));
        Assert.Equal(1, child.GetArray("c").Length);
    }

    [Fact, Covers("CC-18")]
    public void TakingTheParentWhileHoldingTheChild_IsRejectedByTheDebugChecker_NotAHang()
    {
#if DEBUG
        var (parent, child) = Pair();
        using var holder = Holder.Write(parent);   // another thread owns the parent: the opposite order would deadlock for real
        var rejected = false;
        var bg = Bg.Start(() => child.Transact(_ =>
        {
            try { Conc.TransactFor(parent, static _ => { }, TimeSpan.FromSeconds(5)); }
            catch (YConcurrencyException) { rejected = true; }
        }));
        bg.Join();
        Assert.True(rejected, "the lock-order checker did not turn the child-then-parent acquire into a YConcurrencyException");
#else
        Assert.Skip("The lock-order checker is a debug-build feature (CC-18); the Debug run covers this test.");
#endif
    }

    // ------------------------------------------------------------------ CC-19

    [Fact, Covers("CC-19")]
    [Requires(Feature.Awareness)]
    public void Awareness_AnyOperation_NeverTakesTheDocLock()
    {
        var doc = Conc.NewDoc();
        var aware = new Awareness(doc, new VirtualTimeProvider());
        using var holder = Holder.Write(doc);
        Conc.AssertDoesNotWait(() =>
        {
            aware.SetLocalState(State("a", 1));
            aware.SetLocalStateField("b", 2);
            GC.KeepAlive(aware.GetStates());
            GC.KeepAlive(aware.LocalState);
            aware.EncodeUpdate(new ArrayBufferWriter<byte>());
        }, "awareness calls while a document writer held the doc lock");
    }

    [Fact, Covers("CC-19")]
    [Requires(Feature.Events, Feature.Awareness)]
    public void AwarenessEvents_FireSynchronously_OnTheCallingThread()
    {
        var aware = new Awareness(Conc.NewDoc(), new VirtualTimeProvider());
        var threads = new List<int>();
        aware.Change += (_, _) => threads.Add(Environment.CurrentManagedThreadId);
        aware.Update += (_, _) => threads.Add(Environment.CurrentManagedThreadId);
        var bg = Bg.Start(() => aware.SetLocalState(State("a", 1)));
        bg.Join();
        Assert.NotEmpty(threads);
        Assert.All(threads, t => Assert.Equal(bg.ThreadId, t));
    }

    [Fact, Covers("CC-19")]
    [Requires(Feature.Awareness)]
    public void TimeoutTimer_InjectedTimeProviderAdvanced_FiresOnTheAdvancingThread()
    {
        var time = new VirtualTimeProvider();
        var aware = new Awareness(Conc.NewDoc(1), time);
        var remote = new Awareness(Conc.NewDoc(2), time);
        remote.SetLocalState(State("r", 1));
        var bytes = new ArrayBufferWriter<byte>();
        remote.EncodeUpdate(bytes);
        Assert.True(aware.TryApplyUpdate(bytes.WrittenSpan, "net").Ok);
        Assert.Contains(remote.ClientId, aware.GetStates().Keys);
        var removed = new List<(object? Origin, int Thread)>();
        aware.Change += (c, origin) =>
        {
            if (c.Removed.Contains(remote.ClientId)) removed.Add((origin, Environment.CurrentManagedThreadId));
        };
        var bg = Bg.Start(() => time.Advance(TimeSpan.FromSeconds(40)));    // no real waiting: the clock is injected
        bg.Join();
        var (origin, thread) = Assert.Single(removed);
        Assert.Equal("timeout", origin);
        Assert.Equal(bg.ThreadId, thread);
    }

    [Fact, Covers("CC-19")]
    [Requires(Feature.Array, Feature.Undo, Feature.Awareness)]
    public void TheAwarenessLock_IsExclusive_AndReentrantOnTheOwner()
    {
        var aware = new Awareness(Conc.NewDoc(), new VirtualTimeProvider());
        using var inHandler = new ManualResetEventSlim(false);
        using var proceed = new ManualResetEventSlim(false);
        var nested = 0;
        aware.Change += (c, origin) =>
        {
            if (Equals(origin, "local") && c.Updated.Count + c.Added.Count > 0 && nested == 0)
            {
                nested++;
                aware.SetLocalStateField("again", 1);   // re-entrant: the owner takes its own lock again
                inHandler.Set();
                proceed.Wait(Bg.Generous);
            }
        };
        var first = Bg.Start(() => aware.SetLocalState(State("a", 1)));
        Assert.True(inHandler.Wait(Bg.Generous, TestContext.Current.CancellationToken));
        var second = Bg.Start(() => aware.SetLocalStateField("b", 2));
        second.AssertStillBlocked();                    // exclusive: another thread waits while an event is being delivered
        proceed.Set();
        first.Join();
        second.Join();
    }

    // ------------------------------------------------------------------ CC-20

    private static (Doc Doc, YArray Arr, UndoManager Um) Undoable()
    {
        var doc = Conc.NewDoc();
        var arr = doc.GetArray("a");
        var um = new UndoManager(arr, new UndoManagerOptions { CaptureTimeout = TimeSpan.Zero });
        arr.Push(Conc.One);
        arr.Push(Conc.One);
        return (doc, arr, um);
    }

    [Fact, Covers("CC-20")]
    [Requires(Feature.Undo)]
    public void UndoManagerMembers_RunUnderTheDocLock_AnImplicitTransactionEach()
    {
        var (doc, _, um) = Undoable();
        foreach (var member in new Action[] { () => GC.KeepAlive(um.CanUndo), () => GC.KeepAlive(um.CanRedo), () => GC.KeepAlive(um.UndoStack), () => GC.KeepAlive(um.RedoStack), um.StopCapturing })
        {
            using var holder = Holder.Write(doc);
            Conc.AssertWaitsFor(holder, member, "an UndoManager member while a writer held the doc lock");
        }
    }

    [Fact, Covers("CC-20")]
    [Requires(Feature.Undo)]
    public void UndoAndRedo_AnyCall_AreWriteTransactions()
    {
        var (doc, arr, um) = Undoable();
        using (var reader = Holder.Read(doc))
            Conc.AssertWaitsFor(reader, () => um.Undo(), "Undo while a reader held the doc lock");
        Assert.Equal(1, arr.Length);
        using (var reader = Holder.Read(doc))
        {
            Conc.AssertDoesNotWait(() => GC.KeepAlive(um.CanUndo), "a read-only UndoManager member while only a reader held the lock");
            Conc.AssertWaitsFor(reader, () => um.Redo(), "Redo while a reader held the doc lock");
        }
        Assert.Equal(2, arr.Length);
    }

    [Fact, Covers("CC-20")]
    [Requires(Feature.Events)]
    public void Undo_AnyCall_RunsOnTheCallingThreadAndItsEventsFireThere()
    {
        var (doc, _, um) = Undoable();
        var threads = new List<int>();
        doc.AfterTransaction += (_, _) => threads.Add(Environment.CurrentManagedThreadId);
        var bg = Bg.Start(() => um.Undo());
        bg.Join();
        Assert.NotEmpty(threads);
        Assert.All(threads, t => Assert.Equal(bg.ThreadId, t));
    }

    [Fact, Covers("CC-20")]
    [Requires(Feature.UserData)]
    public void PermanentUserDataMembers_AnyCall_RunUnderTheDocLock()
    {
        var doc = Conc.NewDoc();
        var pud = new PermanentUserData(doc, new PermanentUserDataOptions { Defer = static f => f() });
        pud.SetUserMapping(doc, doc.ClientId, "alice");
        using var holder = Holder.Write(doc);
        Conc.AssertWaitsFor(holder, () => Assert.Equal("alice", pud.GetUserByClientId(doc.ClientId)), "GetUserByClientId while a writer held the doc lock");
        using var writer = Holder.Write(doc);
        Conc.AssertWaitsFor(writer, () => pud.SetUserMapping(doc, 99, "bob"), "SetUserMapping while a writer held the doc lock");
    }

    [Fact, Covers("CC-20")]
    [LibraryIndependent]
    public void StackItemMeta_IsCallerOwned_AndNotProtected()
    {
        // ADR 0006 CC-20: the library neither locks nor copies StackItem.Meta. It is a plain dictionary the caller guards.
        var meta = typeof(StackItem).GetProperty(nameof(StackItem.Meta))!;
        Assert.Equal(typeof(Dictionary<object, object?>), meta.PropertyType);
        Assert.True(meta.GetMethod!.IsPublic);
    }
}
