using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Concurrency;

/// <summary>CC-11..CC-14: observers run synchronously on the committing thread inside the write lock, the handler list is safe to change from anywhere, and faults are contained. Tier: gating. No seeds.</summary>
[Collection(ConcurrencyCollection.Name)]
[Tier(Tier.Gating)]
public sealed class ObserverTests
{
    private sealed class Probe
    {
        public Doc Doc { get; } = Conc.NewDoc();
        public YArray Arr { get; }
        public List<string> Log { get; } = new();
        public List<int> Threads { get; } = new();
        public TryTransactStatus? ContendedFromElsewhere { get; private set; }
        public bool TimedOutFromElsewhere { get; private set; }

        public Probe()
        {
            Arr = Doc.GetArray("a");
            Doc.BeforeTransaction += (_, _) => Rec("before");
            Doc.BeforeObserverCalls += (_, _) => Rec("beforeObservers");
            Arr.Observe((_, _) =>
            {
                Rec("observe");
                // inside the write lock: no other thread can take it, so TryTransact elsewhere is Contended and a timed Transact times out
                var bg = Bg.Start(() =>
                {
                    ContendedFromElsewhere = Doc.TryTransact(static _ => { }).Status;
                    try { Conc.TransactFor(Doc, static _ => { }, TimeSpan.FromMilliseconds(30)); }
                    catch (TimeoutException) { TimedOutFromElsewhere = true; }
                });
                bg.Join();
            });
            Arr.ObserveDeep((_, _) => Rec("deep"));
            Doc.AfterTransaction += (_, _) => Rec("after");
            Doc.AfterTransactionCleanup += (_, _) => Rec("cleanup");
            Doc.AfterAllTransactions += (_, _) => Rec("all");
        }

        private void Rec(string what)
        {
            Log.Add(what);
            Threads.Add(Environment.CurrentManagedThreadId);
        }
    }

    // ------------------------------------------------------------------ CC-11

    [Fact, Covers("CC-11")]
    public void Observers_RunSynchronously_OnTheCommittingThread_InsideTheWriteLock() => Impl.Run(() =>
    {
        var p = new Probe();
        p.Arr.Push(Conc.One);
        Assert.All(p.Threads, t => Assert.Equal(Environment.CurrentManagedThreadId, t));
        Assert.Equal(TryTransactStatus.Contended, p.ContendedFromElsewhere);
        Assert.True(p.TimedOutFromElsewhere);
        // by the time the call returned every handler has run: no dispatcher, no deferred work
        Assert.Contains("all", p.Log);
    });

    [Fact, Covers("CC-11")]
    public void TheCommitOrder_IsTheSameOnEveryThread_AndHandlersNeverHopThreads() => Impl.Run(() =>
    {
        var onMain = new Probe();
        onMain.Arr.Push(Conc.One);
        var reference = onMain.Log.ToArray();
        // the oracle's order (Spec 7 EventTests owns the full sequence): observers before deep observers before afterTransaction
        Assert.True(reference.ToList().IndexOf("observe") < reference.ToList().IndexOf("deep"));
        Assert.True(reference.ToList().IndexOf("deep") < reference.ToList().IndexOf("after"));
        Assert.True(reference.ToList().IndexOf("after") < reference.ToList().IndexOf("cleanup"));

        for (var i = 0; i < 4; i++)
        {
            var p = new Probe();
            var bg = Bg.Start(() => p.Arr.Push(Conc.One));
            bg.Join();
            Assert.Equal(reference, p.Log.ToArray());
            Assert.All(p.Threads, t => Assert.Equal(bg.ThreadId, t));
        }
    });

    [Fact, Covers("CC-11")]
    public void RemoteUpdate_Applied_FiresOnTheThreadThatCalledApplyUpdate() => Impl.Run(() =>
    {
        var source = Conc.NewDoc(9);
        source.GetArray("a").Push(Conc.One);
        var update = source.EncodeStateAsUpdate();
        var doc = Conc.NewDoc(1);
        var seen = new List<(string Event, int Thread)>();
        doc.Update += (_, _, _, _) => seen.Add(("update", Environment.CurrentManagedThreadId));
        doc.GetArray("a").Observe((_, tx) => seen.Add((tx.IsLocal ? "local" : "remote", Environment.CurrentManagedThreadId)));
        var bg = Bg.Start(() => doc.ApplyUpdate(update, "net"));
        bg.Join();
        Assert.Contains(("remote", bg.ThreadId), seen);
        Assert.Contains(("update", bg.ThreadId), seen);
        Assert.All(seen, s => Assert.Equal(bg.ThreadId, s.Thread));
    });

    // ------------------------------------------------------------------ CC-12

    [Fact, Covers("CC-12")]
    public void ObserveAndUnobserve_NeverTakeTheDocumentLock_FromAnyThread() => Impl.Run(() =>
    {
        var doc = Conc.NewDoc();
        var arr = doc.GetArray("a");
        using var holder = Holder.Write(doc);
        YEventHandler<YEvent> h = static (_, _) => { };
        YDeepEventHandler dh = static (_, _) => { };
        TransactionHandler th = static (_, _) => { };
        Conc.AssertDoesNotWait(() =>
        {
            var sub = arr.Observe(h);
            sub.Dispose();
            arr.Observe(h);
            arr.Unobserve(h);
            var deep = arr.ObserveDeep(dh);
            deep.Dispose();
            arr.ObserveDeep(dh);
            arr.UnobserveDeep(dh);
            doc.AfterTransaction += th;
            doc.AfterTransaction -= th;
        }, "subscribing and unsubscribing");
    });

    [Fact, Covers("CC-12")]
    public void ObserveAndUnobserve_InsideACallback_AreSafe() => Impl.Run(() =>
    {
        var doc = Conc.NewDoc();
        var arr = doc.GetArray("a");
        var log = new List<string>();
        var late = 0;
        Subscription self = default;
        self = arr.Observe((_, _) =>
        {
            log.Add("first");
            self.Dispose();                                   // unsubscribing itself
            arr.Observe((_, _) => late++);                    // subscribing from inside
        });
        arr.Push(Conc.One);
        arr.Push(Conc.One);
        Assert.Equal(new[] { "first" }, log);                 // it removed itself after the first call
        Assert.Equal(1, late);                                // the handler added in the callback saw the second event only
    });

    [Fact, Covers("CC-12")]
    public void SubscribingFromAnotherThread_WhileACommitIsInFlight_DoesNotWaitForIt() => Impl.Run(() =>
    {
        var doc = Conc.NewDoc();
        var arr = doc.GetArray("a");
        using var inHandler = new ManualResetEventSlim(false);
        using var proceed = new ManualResetEventSlim(false);
        arr.Observe((_, _) =>
        {
            inHandler.Set();
            proceed.Wait(Bg.Generous);
        });
        var committer = Bg.Start(() => arr.Push(Conc.One));
        Assert.True(inHandler.Wait(Bg.Generous));
        var calls = 0;
        Conc.AssertDoesNotWait(() => arr.Observe((_, _) => calls++), "Observe during a commit");
        proceed.Set();
        committer.Join();
        Assert.Equal(0, calls);                                // not part of the dispatch that was already running
        arr.Push(Conc.One);
        Assert.Equal(1, calls);
    });

    // ------------------------------------------------------------------ CC-13

    [Fact, Covers("CC-13")]
    public void HandlersRun_InRegistrationOrder_AndTheSameHandlerRegisteredTwiceRunsTwice() => Impl.Run(() =>
    {
        var arr = Conc.NewDoc().GetArray("a");
        var log = new List<string>();
        YEventHandler<YEvent> a = (_, _) => log.Add("a");
        YEventHandler<YEvent> b = (_, _) => log.Add("b");
        arr.Observe(a);
        arr.Observe(b);
        arr.Observe(a);
        arr.Push(Conc.One);
        Assert.Equal(new[] { "a", "b", "a" }, log);
    });

    [Fact, Covers("CC-13")]
    public void ARemovedHandlerStillRuns_InTheDispatchThatWasInFlight_AnAddedOneFirstRunsNextTrigger() => Impl.Run(() =>
    {
        // Spec 7's TX-12 probe ran this against Yjs 13.6.33 and got the same rule, so CC-13 is a match, not a divergence.
        var arr = Conc.NewDoc().GetArray("a");
        var log = new List<string>();
        YEventHandler<YEvent> h3 = (_, _) => log.Add("h3");
        YEventHandler<YEvent> h2 = (_, _) => log.Add("h2");
        YEventHandler<YEvent> h1 = (_, _) =>
        {
            log.Add("h1");
            arr.Unobserve(h2);
            arr.Observe(h3);
        };
        arr.Observe(h1);
        arr.Observe(h2);
        arr.Push(Conc.One);
        log.Add("--");
        arr.Push(Conc.One);
        Assert.Equal(new[] { "h1", "h2", "--", "h1", "h3" }, log);
    });

    [Fact, Covers("CC-13")]
    public void TheSnapshot_IsTakenWhenTheTriggerStarts_EvenWhenAnotherThreadChangesTheListMeanwhile() => Impl.Run(() =>
    {
        var doc = Conc.NewDoc();
        var arr = doc.GetArray("a");
        var log = new List<string>();
        using var inFirst = new ManualResetEventSlim(false);
        using var proceed = new ManualResetEventSlim(false);
        YEventHandler<YEvent> second = (_, _) => log.Add("second");
        arr.Observe((_, _) =>
        {
            log.Add("first");
            inFirst.Set();
            proceed.Wait(Bg.Generous);
        });
        arr.Observe(second);
        var committer = Bg.Start(() => arr.Push(Conc.One));
        Assert.True(inFirst.Wait(Bg.Generous));
        arr.Unobserve(second);                                 // removed from another thread, mid-dispatch
        arr.Observe((_, _) => log.Add("late"));                // added from another thread, mid-dispatch
        proceed.Set();
        committer.Join();
        Assert.Equal(new[] { "first", "second" }, log);        // the removed one still ran, the added one did not
    });

    // ------------------------------------------------------------------ CC-14

    [Fact, Covers("CC-14")]
    public void AHandlerFault_IsContained_OnTheCommittingThread_WhicheverThreadThatIs() => Impl.Run(() =>
    {
        var doc = Conc.NewDoc();
        var arr = doc.GetArray("a");
        var faults = new List<(Exception Fault, int Thread)>();
        doc.ObserverFaulted += (fault, _) => faults.Add((fault, Environment.CurrentManagedThreadId));
        var boom = new InvalidOperationException("boom");
        var ranAfter = 0;
        var updates = 0;
        arr.Observe((_, _) => throw boom);
        arr.Observe((_, _) => ranAfter++);
        doc.Update += (_, _, _, _) => updates++;

        arr.Push(Conc.One);                                    // main thread
        var bg = Bg.Start(() => arr.Push(Conc.One));           // another thread
        bg.Join();                                             // no exception reached the committer

        Assert.Equal(2, ranAfter);                             // the next handler and the rest of the commit still ran
        Assert.Equal(2, updates);
        Assert.Equal(2, arr.Length);
        Assert.Equal(2, faults.Count);
        Assert.All(faults, f => Assert.Same(boom, f.Fault));
        Assert.Equal(new[] { Environment.CurrentManagedThreadId, bg.ThreadId }, faults.Select(f => f.Thread).ToArray());
        Conc.AssertDoesNotWait(() => doc.Transact(static _ => { }), "a writer after a faulting commit");   // the lock was released
    });

    [Fact, Covers("CC-14")]
    public void AFault_InARemoteUpdatesObserver_DoesNotStopTheApply_OrLeakTheLock() => Impl.Run(() =>
    {
        var source = Conc.NewDoc(9);
        source.GetArray("a").Push(Conc.One);
        var doc = Conc.NewDoc(1);
        var faults = 0;
        doc.ObserverFaulted += (_, _) => Interlocked.Increment(ref faults);
        doc.GetArray("a").Observe((_, _) => throw new InvalidOperationException("boom"));
        var bg = Bg.Start(() => Assert.True(doc.TryApplyUpdate(source.EncodeStateAsUpdate()).Ok));
        bg.Join();
        Assert.Equal(1, faults);
        Assert.Equal(1, doc.GetArray("a").Length);
        Assert.False(doc.IsFaulted);                           // a handler fault is not an internal invariant violation (ADR 0005)
        Conc.AssertDoesNotWait(() => doc.Transact(static _ => { }), "a writer after the remote apply");
    });
}
