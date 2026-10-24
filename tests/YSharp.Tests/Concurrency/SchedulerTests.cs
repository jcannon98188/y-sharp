using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using YSharp.TestKit.Concurrency;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Concurrency;

/// <summary>
/// The hand-rolled scheduler tested on its own, with no y-sharp involved (so these run on the API stub). CC-5 says the lock must be
/// controllable by a systematic schedule tester; ADR 0006 settles on this scheduler instead of Coyote, so the scheduler's own contract is
/// part of that guarantee: a seed replays a schedule exactly, DFS enumerates all of them, and a deadlock is reported rather than hung.
/// Tier: gating. Seeds: fixed per test.
/// </summary>
[Collection(ConcurrencyCollection.Name)]
[Tier(Tier.Gating)]
public sealed class SchedulerTests
{
    private static readonly TimeSpan Wd = TimeSpan.FromSeconds(10);

    private static ScheduleResult Run(IChoiceStrategy choice, Action<CooperativeScheduler> build, TimeSpan? watchdog = null)
    {
        var s = new CooperativeScheduler(choice, "scheduler-test");
        build(s);
        return s.Run(watchdog ?? Wd);
    }

    private static void ThreeThreads(CooperativeScheduler s)
    {
        foreach (var name in new[] { "A", "B", "C" })
        {
            var n = name;
            s.Spawn(n, () =>
            {
                s.Note(n + "1");
                s.Point(n + ".p1");
                s.Note(n + "2");
                s.Point(n + ".p2");
                s.Note(n + "3");
            });
        }
    }

    [Fact, Covers("CC-5")]
    public void ASeed_ReplaysTheSameSchedule_EveryTime()
    {
        var first = Run(new SeededChoice(7), ThreeThreads);
        var second = Run(new SeededChoice(7), ThreeThreads);
        first.AssertClean();
        Assert.Equal(first.Trace, second.Trace);
        Assert.Equal(first.Choices, second.Choices);
    }

    [Fact, Covers("CC-5")]
    public void DifferentSeeds_ExploreDifferentSchedules()
    {
        var orders = new HashSet<string>();
        for (uint seed = 1; seed <= 30; seed++)
        {
            var r = Run(new SeededChoice(seed), ThreeThreads);
            r.AssertClean();
            Assert.Equal(9, r.Notes.Count);
            orders.Add(string.Join(",", r.Notes));
        }
        Assert.True(orders.Count >= 10, $"30 seeds produced only {orders.Count} distinct schedules");
    }

    [Fact, Covers("CC-5")]
    public void Dfs_EnumeratesEveryInterleavingOfTwoThreadsExactlyOnce()
    {
        var seen = new HashSet<string>();
        var runs = 0;
        int[]? prefix = null;
        while (true)
        {
            var dfs = new DfsChoice(prefix);
            var r = Run(dfs, s =>
            {
                foreach (var n in new[] { "a", "b" })
                {
                    var name = n;
                    s.Spawn(name, () =>
                    {
                        s.Note(name + "1");
                        s.Point(name);
                        s.Note(name + "2");
                    });
                }
            });
            r.AssertClean();
            runs++;
            Assert.True(seen.Add(string.Join(" ", r.Notes)), "DFS visited the same schedule twice");
            prefix = dfs.NextPrefix();
            if (prefix == null) break;
            Assert.True(runs < 100, "DFS did not terminate");
        }
        Assert.Equal(6, runs);   // C(4,2): two threads of two steps each
        Assert.Equal(6, seen.Count);
        Assert.Contains("a1 a2 b1 b2", seen);
        Assert.Contains("b1 a1 b2 a2", seen);
    }

    [Fact, Covers("CC-5")]
    public void ABlockedThread_ResumesOnlyOnceItsPredicateHolds()
    {
        for (uint seed = 1; seed <= 20; seed++)
        {
            var ready = false;
            var r = Run(new SeededChoice(seed), s =>
            {
                s.Spawn("Waiter", () =>
                {
                    s.Block("wait-ready", () => ready);
                    s.Note(ready ? "woke-ready" : "woke-early");
                });
                s.Spawn("Setter", () =>
                {
                    s.Point("before-set");
                    ready = true;
                    s.Note("set");
                });
            });
            r.AssertClean();
            Assert.Equal(new[] { "set", "woke-ready" }, r.Notes.ToArray());
        }
    }

    [Fact, Covers("CC-5")]
    public void ADeadlock_IsReportedWithEveryWait_NotHung()
    {
        var r = Run(new SeededChoice(3), s =>
        {
            var (a, b) = (false, false);
            s.Spawn("T1", () => { s.Block("T1-needs-b", () => b); a = true; });
            s.Spawn("T2", () => { s.Block("T2-needs-a", () => a); b = true; });
        });
        Assert.False(r.Clean);
        Assert.NotNull(r.Deadlock);
        Assert.Contains("T1 waits at 'T1-needs-b'", r.Deadlock);
        Assert.Contains("T2 waits at 'T2-needs-a'", r.Deadlock);
        Assert.False(r.TimedOut);
        Assert.Contains("DEADLOCK", r.Describe());
    }

    [Fact, Covers("CC-5")]
    public void AThreadBlockedOutsideTheSeam_TripsTheWatchdog_WithTheSeedInTheReport()
    {
        using var never = new ManualResetEventSlim(false);
        var r = Run(new SeededChoice(11), s =>
        {
            s.Spawn("Stuck", () => never.Wait(TimeSpan.FromSeconds(30)));
            s.Spawn("Other", () => s.Point("other"));
        }, TimeSpan.FromMilliseconds(300));
        Assert.True(r.TimedOut);
        Assert.False(r.Clean);
        Assert.Contains("seed 11", r.Describe());
        Assert.Contains("WATCHDOG", r.Describe());
        never.Set();
    }

    [Fact, Covers("CC-5")]
    public void AnUnhandledException_IsReportedAgainstItsThread_AndTheOtherThreadsFinish()
    {
        var r = Run(new SeededChoice(2), s =>
        {
            s.Spawn("Bad", () => throw new InvalidOperationException("boom"));
            s.Spawn("Good", () => s.Note("good-ran"));
        });
        Assert.Contains("good-ran", r.Notes);
        var (thread, error) = Assert.Single(r.Errors);
        Assert.Equal("Bad", thread);
        Assert.IsType<InvalidOperationException>(error);
        Assert.False(r.Clean);
    }

    [Fact, Covers("CC-5")]
    public void CallsFromThreadsTheSchedulerDoesNotControl_AreNoOps()
    {
        var s = new CooperativeScheduler(new SeededChoice(1), "uncontrolled");
        s.Point("main");
        s.Block("main", () => false);
        Assert.False(s.Controls);
        Assert.False(s.IsBlocked("nobody"));
    }
}
