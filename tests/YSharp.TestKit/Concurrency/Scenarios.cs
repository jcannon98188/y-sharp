using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Time;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Stubs;

namespace YSharp.TestKit.Concurrency;

/// <summary>
/// A small concurrency scenario for the scheduler: two to four threads, a few operations (ADR 0006 "Systematic exploration").
/// <see cref="Build"/> creates fresh state, spawns the threads and returns the check to run once the schedule has finished cleanly.
/// </summary>
public sealed class Scenario
{
    public required string Name { get; init; }
    public required string[] Covers { get; init; }
    public required Func<CooperativeScheduler, Action<ScheduleResult>?> Build { get; init; }

    /// <summary>The scenario relies on the debug-build lock-order checker (CC-18), so it only runs in Debug.</summary>
    public bool DebugOnly { get; init; }

    public override string ToString() => Name;
}

/// <summary>Runs scenarios under the scheduler: seeded random schedules for the gating and nightly tiers, depth-first enumeration for the tiny ones.</summary>
public static class Explorer
{
    /// <summary>Seeds the gating tier runs for every scenario. <c>YSHARP_SEED</c> pins one seed locally to replay a failure.</summary>
    public const int GatingSeedCount = 40;

    public static IReadOnlyList<uint> GatingSeeds =>
        YSharpEnv.Seed is { } pinned ? new[] { pinned } : Enumerable.Range(1, GatingSeedCount).Select(i => (uint)i).ToArray();

    public static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    public static void RunSeeds(Scenario scenario, IEnumerable<uint> seeds)
    {
        if (scenario.DebugOnly)
        {
#if !DEBUG
            Assert.Skip($"{scenario.Name} relies on the debug-build lock-order checker (CC-18).");
#endif
        }
        foreach (var seed in seeds) RunOne(scenario, new SeededChoice(seed));
    }

    /// <summary>Enumerates schedules depth first until all are explored or <paramref name="max"/> have run; returns how many ran.</summary>
    public static int RunDfs(Scenario scenario, int max, out bool exhausted)
    {
        if (scenario.DebugOnly)
        {
#if !DEBUG
            Assert.Skip($"{scenario.Name} relies on the debug-build lock-order checker (CC-18).");
#endif
        }
        var prefix = (int[]?)null;
        var count = 0;
        exhausted = false;
        while (count < max)
        {
            var dfs = new DfsChoice(prefix);
            RunOne(scenario, dfs);
            count++;
            prefix = dfs.NextPrefix();
            if (prefix == null)
            {
                exhausted = true;
                break;
            }
        }
        return count;
    }

    public static void RunOne(Scenario scenario, IChoiceStrategy choice)
    {
        // While the library is the stub, skip visibly.
        ApiStub.SkipIfStub();

        var sched = new CooperativeScheduler(choice, scenario.Name);
        Action<ScheduleResult>? check;
        ScheduleResult result;
        using (ScheduleSeam.Install(sched))
        {
            check = scenario.Build(sched);
            result = sched.Run(Watchdog);
        }
        result.AssertClean();
        try { check?.Invoke(result); }
        catch (Exception e) when (e is not NotImplementedException || !ApiStub.IsStub)
        {
            Assert.Fail(e.Message + Environment.NewLine + result.Describe());
        }
    }
}

/// <summary>The scenarios ADR 0006 names: re-entrancy, writer against reader, subscribe against commit, the subdoc ABBA case, awareness timer against <c>SetLocalState</c>, plus upgrade, destroy and undo.</summary>
public static class Scenarios
{
    private static Doc NewDoc(ulong client = 1) => new(new DocOptions { ClientId = client });

    // a property: building a YEntry throws on the API stub, and a static initializer must not

    private static YEntry[] One => new YEntry[] { 1 };

    private static int Index(ScheduleResult r, string note) => r.Notes.ToList().IndexOf(note);

    /// <summary>CC-1, CC-2: a reader holds, a writer queues behind it, a later reader must queue behind the writer (writer preference), and the writer is exclusive.</summary>
    public static readonly Scenario WriterPreference = new()
    {
        Name = "writer-preference",
        Covers = new[] { "CC-1", "CC-2" },
        Build = s =>
        {
            var doc = NewDoc();
            var (r1In, release) = (false, false);
            s.Spawn("R1", () => doc.ReadTransact(_ =>
            {
                r1In = true;
                s.Note("R1-in");
                s.Block("r1-hold", () => release);
                s.Note("R1-out");
            }));
            s.Spawn("W", () =>
            {
                s.Block("w-after-r1", () => r1In);
                doc.Transact(_ =>
                {
                    s.Note("W-in");
                    doc.GetArray("a").Push(One);
                    s.Point("w-inside");
                    s.Note("W-out");
                });
            });
            s.Spawn("R2", () =>
            {
                s.WaitUntilBlocked("W");
                s.Note("R2-try");
                doc.ReadTransact(_ => s.Note("R2-in"));
            });
            s.Spawn("Release", () =>
            {
                s.WaitUntilBlocked("R2");
                release = true;
            });
            return r =>
            {
                Assert.True(Index(r, "R1-in") < Index(r, "R2-try"));
                Assert.True(Index(r, "R2-try") < Index(r, "W-in"), "the writer got in before the second reader even asked");
                Assert.True(Index(r, "R1-out") < Index(r, "W-in"), "a writer entered while a reader held the lock");
                Assert.True(Index(r, "W-out") < Index(r, "R2-in"), "a reader that arrived after a waiting writer got in first");
            };
        },
    };

    /// <summary>CC-2: a nested write joins the owner's transaction; another thread never joins and is excluded for the whole of it.</summary>
    public static readonly Scenario NestedJoinAndExclusion = new()
    {
        Name = "nested-join-and-exclusion",
        Covers = new[] { "CC-1", "CC-2" },
        Build = s =>
        {
            var doc = NewDoc();
            Transaction? outer = null, inner = null, other = null;
            s.Spawn("Owner", () => doc.Transact(tx =>
            {
                outer = tx;
                s.Note("O-in");
                s.Point("o-1");
                doc.Transact(t2 => inner = t2);
                s.Point("o-2");
                doc.GetArray("a").Push(One);
                s.Note("O-out");
            }));
            s.Spawn("Other", () => doc.Transact(tx =>
            {
                other = tx;
                s.Note("X-in");
                s.Note("X-out");
            }));
            return r =>
            {
                Assert.Same(outer, inner);
                Assert.NotSame(outer, other);
                var (oi, oo, xi, xo) = (Index(r, "O-in"), Index(r, "O-out"), Index(r, "X-in"), Index(r, "X-out"));
                Assert.True(xo < oi || xi > oo, "the other thread's transaction overlapped the owner's");
            };
        },
    };

    /// <summary>CC-3: upgrading a read to a write on the same thread throws, in every schedule, and never waits for itself.</summary>
    public static readonly Scenario UpgradeIsRejected = new()
    {
        Name = "upgrade-is-rejected",
        Covers = new[] { "CC-3", "CC-1" },
        Build = s =>
        {
            var doc = NewDoc();
            s.Spawn("Upgrader", () => doc.ReadTransact(_ =>
            {
                s.Point("u-in-read");
                try
                {
                    doc.Transact(_ => s.Note("upgraded"));
                    s.Note("no-throw");
                }
                catch (YConcurrencyException) { s.Note("upgrade-threw"); }
            }));
            s.Spawn("Writer", () => doc.Transact(_ => doc.GetArray("a").Push(One)));
            s.Spawn("Reader", () => doc.ReadTransact(_ => s.Point("r-in-read")));
            return r =>
            {
                Assert.Contains("upgrade-threw", r.Notes);
                Assert.DoesNotContain("upgraded", r.Notes);
                Assert.DoesNotContain("no-throw", r.Notes);
            };
        },
    };

    /// <summary>CC-12, CC-13: Observe and Dispose race a commit; a handler sees whole events only, and once disposed it is never called again.</summary>
    public static readonly Scenario SubscribeVsCommit = new()
    {
        Name = "subscribe-vs-commit",
        Covers = new[] { "CC-12", "CC-13" },
        Build = s =>
        {
            var doc = NewDoc();
            var arr = doc.GetArray("a");
            var calls = 0;
            var disposed = false;
            var lateCall = false;
            var sawEmptyDelta = false;
            s.Spawn("Commit1", () => arr.Push(One));
            s.Spawn("Commit2", () =>
            {
                s.Point("c2-start");
                arr.Push(One);
            });
            s.Spawn("Sub", () =>
            {
                var sub = arr.Observe((e, tx) =>
                {
                    calls++;
                    if (disposed) lateCall = true;
                    if (!tx.IsWrite || !ReferenceEquals(e.Target, arr)) sawEmptyDelta = true;
                });
                s.Point("sub-registered");
                sub.Dispose();
                disposed = true;
                s.Point("sub-disposed");
            });
            return r =>
            {
                Assert.InRange(calls, 0, 2);
                Assert.False(sawEmptyDelta);
                Assert.False(lateCall, "a handler ran after Dispose returned and no dispatch was in flight");
                arr.Push(One);
                Assert.InRange(calls, 0, 2);   // unsubscribed: the third commit does not reach it
            };
        },
    };

    /// <summary>CC-17: Destroy is a write transaction that waits its turn; a destroyed document still accepts acquires.</summary>
    public static readonly Scenario DestroyVsWrite = new()
    {
        Name = "destroy-vs-write",
        Covers = new[] { "CC-17", "CC-1" },
        Build = s =>
        {
            var doc = NewDoc();
            s.Spawn("Destroyer", () =>
            {
                doc.Destroy();
                s.Note("destroyed");
            });
            s.Spawn("Writer", () => doc.Transact(_ =>
            {
                s.Note("W-in");
                doc.GetArray("a").Push(One);
                s.Point("w-inside");
                s.Note("W-out");
            }));
            s.Spawn("Late", () =>
            {
                s.Block("late-after-destroy", () => doc.IsDestroyed);
                doc.Transact(_ => s.Note("late-acquired"));
            });
            return r =>
            {
                Assert.True(doc.IsDestroyed);
                Assert.Contains("late-acquired", r.Notes);
                Assert.Contains("W-out", r.Notes);
            };
        },
    };

    private static Doc NewSubdoc(Doc parent)
    {
        var child = new Doc(new DocOptions { Guid = "sub" });
        parent.GetArray("subs").Push(new YEntry[] { child });
        return child;
    }

    /// <summary>CC-18: every thread takes parent before child, or only one of them: no schedule deadlocks.</summary>
    public static readonly Scenario SubdocHierarchy = new()
    {
        Name = "subdoc-hierarchy",
        Covers = new[] { "CC-18" },
        Build = s =>
        {
            var parent = NewDoc(1);
            var child = NewSubdoc(parent);
            s.Spawn("ParentThenChild", () => parent.Transact(_ =>
            {
                s.Point("p-held");
                child.Transact(_ => child.GetArray("c").Push(One));
            }));
            s.Spawn("ParentOnly", () => parent.Transact(_ =>
            {
                s.Point("p2-held");
                parent.GetArray("a").Push(One);
            }));
            s.Spawn("ChildOnly", () => child.Transact(_ => child.GetArray("c").Push(One)));
            return r =>
            {
                Assert.Equal(2, child.GetArray("c").Length);
                Assert.Equal(1, parent.GetArray("a").Length);
            };
        },
    };

    /// <summary>CC-18: the ABBA case. One thread locks the child and then asks for the parent; the debug checker turns that into an exception instead of a hang, in every schedule.</summary>
    public static readonly Scenario SubdocAbba = new()
    {
        Name = "subdoc-abba",
        Covers = new[] { "CC-18" },
        DebugOnly = true,
        Build = s =>
        {
            var parent = NewDoc(1);
            var child = NewSubdoc(parent);
            s.Spawn("Good", () => parent.Transact(_ =>
            {
                s.Point("good-holds-parent");
                child.Transact(_ => s.Note("good-in-child"));
            }));
            s.Spawn("Bad", () => child.Transact(_ =>
            {
                s.Point("bad-holds-child");
                try
                {
                    parent.Transact(_ => s.Note("bad-in-parent"));
                }
                catch (YConcurrencyException) { s.Note("bad-order-rejected"); }
            }));
            return r =>
            {
                Assert.Contains("bad-order-rejected", r.Notes);
                Assert.DoesNotContain("bad-in-parent", r.Notes);
                Assert.Contains("good-in-child", r.Notes);
            };
        },
    };

    /// <summary>CC-19: the awareness timer (virtual time, advanced on a controlled thread) races <c>SetLocalState</c> and a document write.</summary>
    public static readonly Scenario AwarenessTimerVsSetState = new()
    {
        Name = "awareness-timer-vs-set-state",
        Covers = new[] { "CC-19", "CC-11" },
        Build = s =>
        {
            var time = new VirtualTimeProvider();
            var doc = NewDoc(1);
            var aware = new Awareness(doc, time);
            var remote = new Awareness(NewDoc(2), time);
            remote.SetLocalState(State("remote", 1));
            var bytes = new System.Buffers.ArrayBufferWriter<byte>();
            remote.EncodeUpdate(bytes);
            Assert.True(aware.TryApplyUpdate(bytes.WrittenSpan, "remote").Ok);
            var remoteId = remote.ClientId;
            var removedBy = new List<object?>();
            aware.Change += (c, origin) =>
            {
                if (c.Removed.Contains(remoteId)) removedBy.Add(origin);
                s.Note("change:" + origin);
            };
            s.Spawn("SetState", () =>
            {
                aware.SetLocalState(State("n", 1));
                s.Point("after-set");
                aware.SetLocalStateField("m", 2);
            });
            s.Spawn("Timer", () => time.Advance(TimeSpan.FromSeconds(40)));
            s.Spawn("DocWrite", () => doc.Transact(_ => doc.GetArray("a").Push(One)));
            return r =>
            {
                var states = aware.GetStates();
                Assert.DoesNotContain(remoteId, states.Keys);
                Assert.Equal(new object?[] { "timeout" }, removedBy.ToArray());
                Assert.Contains(aware.ClientId, states.Keys);
                foreach (var id in states.Keys) Assert.Contains(id, aware.Meta.Keys);
                Assert.Equal(1, doc.GetArray("a").Length);
            };
        },
    };

    private static YValue State(string key, double value) =>
        YValue.Object(new[] { new KeyValuePair<string, YValue>(key, value) });

    /// <summary>CC-20: <c>UndoManager.Undo</c> (a write transaction) races an ordinary write on the same document.</summary>
    public static readonly Scenario UndoVsWrite = new()
    {
        Name = "undo-vs-write",
        Covers = new[] { "CC-20", "CC-1" },
        Build = s =>
        {
            var doc = NewDoc();
            var arr = doc.GetArray("a");
            var um = new UndoManager(arr, new UndoManagerOptions { CaptureTimeout = TimeSpan.Zero });
            arr.Push(One);
            s.Spawn("Undo", () => um.Undo());
            s.Spawn("Write", () => doc.Transact(_ => arr.Push(One)));
            return r =>
            {
                // either order ends with exactly one element: the undo removes one of the two pushes
                Assert.Equal(1, arr.Length);
            };
        },
    };

    public static readonly IReadOnlyList<Scenario> All = new[]
    {
        WriterPreference, NestedJoinAndExclusion, UpgradeIsRejected, SubscribeVsCommit, DestroyVsWrite,
        SubdocHierarchy, SubdocAbba, AwarenessTimerVsSetState, UndoVsWrite,
    };

    /// <summary>Small enough for depth-first enumeration of every schedule.</summary>
    public static readonly IReadOnlyList<Scenario> Exhaustive = new[] { UpgradeIsRejected, DestroyVsWrite, NestedJoinAndExclusion };
}
