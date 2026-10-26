using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Xunit;
using YSharp.TestKit.Concurrency;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Concurrency;

/// <summary>
/// CC-1..CC-5: the lock model of ADR 0006. Real threads and gates; the schedule-driven versions of the same guarantees are in
/// <see cref="ScheduleScenarioTests"/> and the load versions in <see cref="StressTests"/>. Tier: gating. No seeds (gates, not timing).
/// </summary>
[Requires(Feature.Concurrency)]
[Collection(ConcurrencyCollection.Name)]
[Tier(Tier.Gating)]
public sealed class LockModelTests
{
    // ------------------------------------------------------------------ CC-1

    [Fact, Covers("CC-1")]
    public void Writers_AreExclusive_ASecondWriterWaitsForTheFirst()
    {
        var doc = Conc.NewDoc();
        using var holder = Holder.Write(doc);
        Conc.AssertWaitsFor(holder, () => doc.Transact(_ => { }), "a second writer");
    }

    [Fact, Covers("CC-1")]
    public void Reader_WriterHoldsTheLock_WaitsForTheWriter()
    {
        var doc = Conc.NewDoc();
        using var holder = Holder.Write(doc);
        Conc.AssertWaitsFor(holder, () => doc.ReadTransact(_ => { }), "a reader");
    }

    [Fact, Covers("CC-1")]
    public void Writer_ReaderHoldsTheLock_WaitsForTheReader()
    {
        var doc = Conc.NewDoc();
        using var holder = Holder.Read(doc);
        Conc.AssertWaitsFor(holder, () => doc.Transact(_ => { }), "a writer");
    }

    [Fact, Covers("CC-1")]
    public void Readers_Share_TwoThreadsAreInsideAtTheSameTime()
    {
        var doc = Conc.NewDoc();
        using var both = new Barrier(2);
        void Read() => doc.ReadTransact(_ => Assert.True(both.SignalAndWait(Bg.Generous, TestContext.Current.CancellationToken), "the second reader never got in while the first held the lock"));
        var a = Bg.Start(Read);
        var b = Bg.Start(Read);
        a.Join();
        b.Join();
    }

    [Fact, Covers("CC-1")]
    [Requires(Feature.Array)]
    public void DocumentLock_AnotherDocumentLocked_IsNotBlocked()
    {
        var doc = Conc.NewDoc(1);
        var other = Conc.NewDoc(2);
        using var holder = Holder.Write(doc);
        Conc.AssertDoesNotWait(() => other.Transact(_ => other.GetArray("a").Push(Conc.One)), "an unrelated document");
    }

    // ------------------------------------------------------------------ CC-2

    [Fact, Covers("CC-2")]
    [Requires(Feature.Array, Feature.Events)]
    public void ANestedWrite_OnTheOwningThread_JoinsTheCurrentTransaction()
    {
        var doc = Conc.NewDoc();
        var arr = doc.GetArray("a");
        var afterTransaction = 0;
        var updates = 0;
        doc.AfterTransaction += (_, _) => afterTransaction++;
        doc.Update += (_, _, _, _) => updates++;
        Transaction? outer = null, inner = null;
        doc.Transact(tx =>
        {
            outer = tx;
            arr.Push(Conc.One);
            doc.Transact(t2 =>
            {
                inner = t2;
                arr.Push(Conc.One);
            });
            Assert.Equal(0, afterTransaction);   // the inner call did not commit anything
            Assert.Equal(0, updates);
        });
        Assert.Same(outer, inner);
        Assert.Equal(1, afterTransaction);
        Assert.Equal(1, updates);
        Assert.Equal(2, arr.Length);
    }

    [Fact, Covers("CC-2")]
    [Requires(Feature.Array)]
    public void Nested_ReadInsideRead_And_ReadInsideWrite_AreAllowedOnTheOwner()
    {
        var doc = Conc.NewDoc();
        var arr = doc.GetArray("a");
        doc.ReadTransact(_ => doc.ReadTransact(_ => Assert.Equal(0, arr.Length)));
        doc.Transact(_ =>
        {
            arr.Push(Conc.One);
            doc.ReadTransact(_ => Assert.Equal(1, arr.Length));
        });
    }

    [Fact, Covers("CC-2")]
    [Requires(Feature.Array, Feature.Events)]
    public void WritesFromObservers_RunInAFollowUpTransaction_AfterCleanup()
    {
        var doc = Conc.NewDoc();
        var arr = doc.GetArray("a");
        var log = new List<string>();
        doc.BeforeTransaction += (_, _) => log.Add("before");
        doc.AfterTransaction += (_, _) => log.Add("after");
        doc.AfterTransactionCleanup += (_, _) => log.Add("cleanup");
        arr.Observe((_, _) =>
        {
            if (arr.Length == 1) arr.Push(Conc.One);   // a write made from inside an observer
        });
        arr.Push(Conc.One);
        Assert.Equal(2, arr.Length);
        Assert.Equal(2, log.Count(l => l == "after"));
        Assert.Equal(2, log.Count(l => l == "cleanup"));
        Assert.Equal("cleanup", log[^1]);
        // the first transaction is cleaned up before the follow-up one ends
        Assert.True(log.IndexOf("cleanup") < log.LastIndexOf("after"));
    }

    [Fact, Covers("CC-2")]
    public void Transaction_AnotherThread_NeverJoins()
    {
        var doc = Conc.NewDoc();
        using var holder = Holder.Write(doc);
        TryTransactResult contended = default;
        var ran = false;
        Conc.AssertDoesNotWait(() => contended = doc.TryTransact(_ => ran = true), "TryTransact");
        Assert.Equal(TryTransactStatus.Contended, contended.Status);
        Assert.False(ran);
        Assert.Throws<TimeoutException>(() => Conc.TransactFor(doc, _ => ran = true, TimeSpan.FromMilliseconds(60)));
        Assert.False(ran);
    }

    [Fact, Covers("CC-2")]
    public void LockOwner_ReleasedLock_CanBeTakenByAnotherThread()
    {
        var doc = Conc.NewDoc();
        doc.Transact(_ => { });
        var onOther = false;
        Conc.AssertDoesNotWait(() => doc.Transact(_ => onOther = true), "a writer after the owner released");
        Assert.True(onOther);
    }

    // ------------------------------------------------------------------ CC-3

    [Fact, Covers("CC-3")]
    public void UpgradingAReadToAWrite_OnTheSameThread_Throws_AndLeavesTheLockIntact()
    {
        var doc = Conc.NewDoc();
        var wrote = false;
        doc.ReadTransact(_ => Assert.Throws<YConcurrencyException>(() => doc.Transact(_ => wrote = true)));
        Assert.False(wrote);
        // no leaked read count and no stale waiting writer: a writer elsewhere gets in at once
        Conc.AssertDoesNotWait(() => doc.Transact(_ => wrote = true), "a writer after the rejected upgrade");
        Assert.True(wrote);
    }

    private static void UpgradeViaScopes(Doc doc)
    {
        using var read = doc.BeginReadTransaction();
        using var write = doc.BeginTransaction();
    }

    [Fact, Covers("CC-3")]
    public void ScopedForms_ReadToWriteUpgrade_IsRejected()
    {
        var doc = Conc.NewDoc();
        Assert.Throws<YConcurrencyException>(() => UpgradeViaScopes(doc));
        Conc.AssertDoesNotWait(() => doc.Transact(_ => { }), "a writer after the rejected scoped upgrade");
    }

    [Fact, Covers("CC-3", "CC-6")]
    public void TryTransact_WhileTheCallingThreadHoldsAReadTransaction_ReturnsUpgradeRefusedAtOnce()
    {
        var doc = Conc.NewDoc();
        var ran = false;
        doc.ReadTransact(_ =>
        {
            // on the calling thread itself: another thread would see the read lock as contention, not as an upgrade
            var result = doc.TryTransact(_ => ran = true);
            Assert.False(result.Acquired);
            Assert.Equal(TryTransactStatus.UpgradeRefused, result.Status);
        });
        using (doc.BeginReadTransaction())
        {
            Assert.Equal(TryTransactStatus.UpgradeRefused, doc.TryTransact(_ => ran = true).Status);
        }
        Assert.False(ran);
        // the read lock was not disturbed: a writer elsewhere gets in, and TryTransact works again
        Conc.AssertDoesNotWait(() => doc.Transact(_ => { }), "a writer after the refused upgrades");
        Assert.True(doc.TryTransact(_ => ran = true).Acquired);
        Assert.True(ran);
    }

    // ------------------------------------------------------------------ CC-4

    [Fact, Covers("CC-4")]
    [Requires(Feature.Text, Feature.Array, Feature.Map)]
    public void AReadTransaction_NeverMutatesDocumentState_CachesIncluded()
    {
        var doc = Conc.NewDoc();
        var arr = doc.GetArray("a");
        var text = doc.GetText("t");
        var map = doc.GetMap("m");
        doc.Transact(_ =>
        {
            for (var i = 0; i < 300; i++)
            {
                arr.Insert(i / 2, new YEntry[] { i });
                text.Insert(text.Length / 2, "ab");
            }
            map.Set("k", 1);
        });
        var stateBefore = doc.EncodeStateAsUpdate();
        var structsBefore = Structs.StructsProbe.DumpJson(doc);

        var order = Enumerable.Range(0, arr.Length).OrderBy(i => (i * 7919) % 301).ToArray();   // jumps around: the access pattern that moves search markers
        doc.ReadTransact(_ =>
        {
            foreach (var i in order) GC.KeepAlive(arr[i]);
            GC.KeepAlive(text.ToString());
            GC.KeepAlive(arr.ToJson());
            GC.KeepAlive(map.TryGet("k", out var entry));
            var buffer = new YEntry[arr.Length];
            arr.CopyTo(0, buffer);
        });

        Assert.Equal(stateBefore, doc.EncodeStateAsUpdate());
        Assert.Equal(structsBefore, Structs.StructsProbe.DumpJson(doc));
    }

    [Fact, Covers("CC-4")]
    public void Reads_RunningAtTheSameTime_ReturnWhatASingleThreadedReadReturns()
    {
        var result = StressHarness.RunReadPurity(new StressOptions { Seed = YSharpEnv.Seed ?? 4242, Readers = 4, OpsPerThread = 120 });
        result.AssertClean();
    }

    // ------------------------------------------------------------------ CC-5

    private static readonly string[] ForbiddenTypes =
    {
        "ReaderWriterLockSlim", "ReaderWriterLock", "SemaphoreSlim", "Semaphore", "Mutex", "SpinLock", "SpinWait",
        "ManualResetEventSlim", "ManualResetEvent", "AutoResetEvent", "CountdownEvent", "Barrier",
    };

    [Fact, Covers("CC-5")]
    [LibraryIndependent]
    public void LibrarySource_AnyFile_UsesNoLockPrimitiveAScheduleTesterCannotControl()
    {
        // ADR 0006 CC-5 and docs/research/schedule-exploration.md: only Monitor, Interlocked and explicit state; no spin-waiting on internals.
        var root = RepoRoot.Find();
        var offenders = new List<string>();
        var pattern = new Regex(@"\b(" + string.Join("|", ForbiddenTypes) + @")\b|\bSystem\.Threading\.Lock\b|\bnew\s+Lock\s*\(|\bThread\.(Sleep|Yield)\b|\bSpinUntil\b");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "YSharp"), "*.cs", SearchOption.AllDirectories))
        {
            var n = 0;
            foreach (var raw in File.ReadLines(file))
            {
                n++;
                var line = raw;
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0) line = line[..comment];
                var m = pattern.Match(line);
                if (m.Success) offenders.Add($"{Path.GetRelativePath(root, file)}:{n}: {m.Value}");
            }
        }
        Assert.True(offenders.Count == 0, "Lock code must be built from Monitor, Interlocked and explicit state (CC-5):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact, Covers("CC-5")]
    [LibraryIndependent]
    public void CompiledLibrary_AnyType_HoldsNoFieldOfAnUncontrollableLockType()
    {
        var bad = new List<string>();
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var type in typeof(Doc).Assembly.GetTypes())
            foreach (var field in type.GetFields(all))
            {
                var t = field.FieldType;
                var name = t.IsGenericType ? t.GetGenericTypeDefinition().Name : t.Name;
                if (ForbiddenTypes.Contains(name) || t.FullName == "System.Threading.Lock")
                    bad.Add($"{type.FullName}.{field.Name}: {t.FullName}");
            }
        Assert.True(bad.Count == 0, "fields the schedule tester cannot control: " + string.Join("; ", bad));
    }

    [Fact, Covers("CC-5")]
    [Requires(Feature.Text, Feature.Array)]
    public void Lock_AnyTransition_IsReportedToTheScheduleSeam()
    {
        _ = new Doc().GetText("probe");
        var sched = new CooperativeScheduler(new SeededChoice(1), "seam-is-hooked");
        var doc = Conc.NewDoc();
        using (new SeamInstall(sched))
        {
            sched.Spawn("A", () => doc.Transact(_ => doc.GetArray("a").Push(Conc.One)));
            sched.Spawn("B", () => doc.ReadTransact(tx => GC.KeepAlive(doc.GetArray("a").Length)));
            var result = sched.Run(Explorer.Watchdog);
            result.AssertClean();
            // the test itself added no points, so every one of these lines came from the lock
            Assert.Contains(result.Trace, l => l.Contains(": point ", StringComparison.Ordinal) || l.Contains(": block ", StringComparison.Ordinal));
        }
    }
}
