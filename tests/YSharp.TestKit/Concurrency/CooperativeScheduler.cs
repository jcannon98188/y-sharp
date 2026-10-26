using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Xunit;
using YSharp.TestKit.Rng;

namespace YSharp.TestKit.Concurrency;

/// <summary>Picks which runnable thread runs next. The only source of non-determinism in a schedule (ADR 0006 "Systematic exploration").</summary>
public interface IChoiceStrategy
{
    /// <summary>Returns an index in <c>[0, count)</c>; only called when <paramref name="count"/> is at least 2.</summary>
    int Choose(int count);
}

/// <summary>Seeded random choices: one seed is one schedule, on any machine.</summary>
public sealed class SeededChoice : IChoiceStrategy
{
    private readonly IPrng _gen;

    public SeededChoice(uint seed)
    {
        Seed = seed;
        _gen = Prng.Create(seed);
    }

    public uint Seed { get; }

    public int Choose(int count) => Math.Min(count - 1, (int)(_gen.Next() * count));
}

/// <summary>
/// Depth-first enumeration of every schedule of a small scenario: replay a prefix of choices, then always take the first option, and
/// record each decision so <see cref="NextPrefix"/> can backtrack to the last decision that still has an untried option.
/// </summary>
public sealed class DfsChoice : IChoiceStrategy
{
    private readonly int[] _prefix;
    private readonly List<(int Chosen, int Count)> _path = new();

    public DfsChoice(int[]? prefix = null) => _prefix = prefix ?? Array.Empty<int>();

    public IReadOnlyList<(int Chosen, int Count)> Path => _path;

    public int Choose(int count)
    {
        var i = _path.Count;
        var chosen = i < _prefix.Length ? Math.Min(_prefix[i], count - 1) : 0;
        _path.Add((chosen, count));
        return chosen;
    }

    /// <summary>The prefix of the next schedule in depth-first order, or null when this was the last one.</summary>
    public int[]? NextPrefix()
    {
        for (var i = _path.Count - 1; i >= 0; i--)
        {
            if (_path[i].Chosen + 1 >= _path[i].Count) continue;
            var next = new int[i + 1];
            for (var j = 0; j < i; j++) next[j] = _path[j].Chosen;
            next[i] = _path[i].Chosen + 1;
            return next;
        }
        return null;
    }
}

/// <summary>Raised inside a parked thread to unwind it when its schedule is abandoned (deadlock, watchdog). Test code must not catch it.</summary>
public sealed class ScheduleAbortException : Exception
{
    public ScheduleAbortException() : base("The schedule was abandoned.") { }
}

/// <summary>The outcome of one schedule.</summary>
public sealed class ScheduleResult
{
    public string Scenario { get; init; } = "";
    public string Strategy { get; init; } = "";
    public bool Completed { get; init; }
    public bool TimedOut { get; init; }
    public string? Deadlock { get; init; }
    public IReadOnlyList<string> Trace { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<(string Thread, Exception Error)> Errors { get; init; } = Array.Empty<(string, Exception)>();
    public IReadOnlyList<int> Choices { get; init; } = Array.Empty<int>();

    public bool Clean => Completed && !TimedOut && Deadlock == null && Errors.Count == 0;

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("scenario ").Append(Scenario).Append(", ").Append(Strategy).AppendLine();
        sb.Append("replay: set YSHARP_SEED to the seed above (or pass the choices ").Append(string.Join(",", Choices)).AppendLine(" to DfsChoice)");
        if (TimedOut) sb.AppendLine("WATCHDOG: the schedule did not finish. A thread blocked outside the seam (a raw Monitor.Wait, Thread.Sleep or BCL lock inside the library), or a test thread never returned.");
        if (Deadlock != null) sb.AppendLine("DEADLOCK: " + Deadlock);
        foreach (var (thread, error) in Errors) sb.Append("error in ").Append(thread).Append(": ").AppendLine(error.ToString());
        sb.AppendLine("trace (" + Trace.Count + " steps, last 80):");
        foreach (var line in Trace.Skip(Math.Max(0, Trace.Count - 80))) sb.Append("  ").AppendLine(line);
        return sb.ToString();
    }

    /// <summary>Fails the test with the seed and trace unless the schedule finished with no deadlock, timeout or unhandled thread error.</summary>
    public void AssertClean()
    {
        if (!Clean) Assert.Fail(Describe());
    }
}

/// <summary>
/// The hand-rolled, seeded, cooperative scheduler of ADR 0006 / docs/research/schedule-exploration.md.
/// <para>
/// Spawned threads are real <see cref="Thread"/>s but a baton passes between them so exactly one runs at a time. A thread gives the baton
/// up only at a <see cref="Point"/> (the scheduler may switch to any runnable thread, itself included) or a <see cref="Block"/> (the thread
/// cannot proceed until a predicate holds). Every choice goes through an <see cref="IChoiceStrategy"/>, so a seed, or a recorded choice
/// list, replays the schedule exactly. When no thread is runnable the schedule is a deadlock and is reported with the wait of every thread.
/// </para>
/// <para>
/// The library under test reports its yield and block points through <see cref="ScheduleSeam"/>; tests add their own with
/// <see cref="Point"/>, <see cref="Block"/> and <see cref="Note"/>. Calls from a thread this scheduler does not control are no-ops.
/// </para>
/// </summary>
public sealed class CooperativeScheduler
{
    private sealed class Ctl
    {
        public required string Name { get; init; }
        public required Action Body { get; init; }
        public SemaphoreSlim Gate { get; } = new(0);
        public Func<bool>? Wait { get; set; }
        public string LastId { get; set; } = "(not started)";
        public bool Finished { get; set; }
        public Thread? Thread { get; set; }
    }

    [ThreadStatic]
    private static Ctl? t_current;

    private readonly object _sync = new();
    private readonly List<Ctl> _threads = new();
    private readonly List<string> _trace = new();
    private readonly List<string> _notes = new();
    private readonly List<(string, Exception)> _errors = new();
    private readonly List<int> _choices = new();
    private readonly IChoiceStrategy _choice;
    private readonly ManualResetEventSlim _done = new(false);
    private bool _aborted;
    private bool _timedOut;
    private bool _started;
    private string? _deadlock;

    public CooperativeScheduler(IChoiceStrategy choice, string scenario)
    {
        _choice = choice;
        Scenario = scenario;
    }

    public string Scenario { get; }

    public string Strategy => _choice is SeededChoice s ? $"seed {s.Seed}" : _choice is DfsChoice ? "depth-first" : _choice.GetType().Name;

    /// <summary>True when the calling thread is one of this scheduler's.</summary>
    public bool Controls => t_current is { } c && _threads.Contains(c);

    public void Spawn(string name, Action body)
    {
        if (_started) throw new InvalidOperationException("Spawn before Run.");
        _threads.Add(new Ctl { Name = name, Body = body });
    }

    /// <summary>A yield point: the scheduler may hand the baton to another runnable thread.</summary>
    public void Point(string id)
    {
        if (t_current is not { } me || !_threads.Contains(me)) return;
        Yield(me, id, null);
    }

    /// <summary>The calling thread cannot proceed until <paramref name="ready"/> returns true (evaluated by the scheduler, never concurrently).</summary>
    public void Block(string id, Func<bool> ready)
    {
        if (t_current is not { } me || !_threads.Contains(me)) return;
        Yield(me, id, ready);
    }

    /// <summary>Records a test event in both the trace and <see cref="ScheduleResult.Notes"/>, in the order the schedule produced it.</summary>
    public void Note(string text)
    {
        lock (_sync)
        {
            var who = t_current?.Name ?? "main";
            _notes.Add(text);
            _trace.Add($"{who}: note {text}");
        }
    }

    /// <summary>True while the named thread is parked on a <see cref="Block"/> whose predicate does not hold.</summary>
    public bool IsBlocked(string name)
    {
        lock (_sync)
        {
            var c = _threads.FirstOrDefault(t => t.Name == name);
            return c is { Finished: false, Wait: { } w } && !w();
        }
    }

    /// <summary>True while the named thread is parked on the <see cref="Block"/> called <paramref name="id"/> and its predicate does not hold.</summary>
    public bool IsBlockedAt(string name, string id)
    {
        lock (_sync)
        {
            var c = _threads.FirstOrDefault(t => t.Name == name);
            return c is { Finished: false, Wait: { } w } && c.LastId == id && !w();
        }
    }

    /// <summary>Blocks the calling controlled thread until the named thread is blocked (inside the library or in a test Block).</summary>
    public void WaitUntilBlocked(string name) => Block("wait-until-blocked:" + name, () => IsBlocked(name));

    /// <summary>Blocks the calling controlled thread until the named thread is blocked at the block point <paramref name="id"/>.</summary>
    public void WaitUntilBlockedAt(string name, string id) => Block($"wait-until-blocked:{name}@{id}", () => IsBlockedAt(name, id));

    public ScheduleResult Run(TimeSpan watchdog)
    {
        _started = true;
        foreach (var c in _threads)
        {
            var ctl = c;
            ctl.Thread = new Thread(() => Entry(ctl)) { IsBackground = true, Name = "sched:" + ctl.Name };
            ctl.Thread.Start();
        }
        lock (_sync)
        {
            if (_threads.Count == 0) _done.Set();
            else Release(PickNext()!);
        }
        if (!_done.Wait(watchdog))
        {
            lock (_sync)
            {
                _timedOut = true;
                AbortAll();
            }
        }
        foreach (var c in _threads) c.Thread!.Join(TimeSpan.FromSeconds(5));
        lock (_sync)
        {
            return new ScheduleResult
            {
                Scenario = Scenario,
                Strategy = Strategy,
                Completed = _threads.All(t => t.Finished),
                TimedOut = _timedOut,
                Deadlock = _deadlock,
                Trace = _trace.ToArray(),
                Notes = _notes.ToArray(),
                Errors = _errors.ToArray(),
                Choices = _choices.ToArray(),
            };
        }
    }

    // ------------------------------------------------------------------ internals

    private void Entry(Ctl c)
    {
        c.Gate.Wait();
        if (_aborted) return;
        t_current = c;
        try { c.Body(); }
        catch (ScheduleAbortException) { }
        catch (Exception e)
        {
            lock (_sync)
            {
                _errors.Add((c.Name, e));
                _trace.Add($"{c.Name}: unhandled {e.GetType().Name}");
            }
        }
        lock (_sync)
        {
            c.Finished = true;
            _trace.Add($"{c.Name}: finished");
            if (_aborted) return;
            if (_threads.All(t => t.Finished)) { _done.Set(); return; }
            var next = PickNext();
            if (next == null) Deadlock();
            else Release(next);
        }
    }

    private void Yield(Ctl me, string id, Func<bool>? wait)
    {
        lock (_sync)
        {
            if (_aborted) throw new ScheduleAbortException();
            me.LastId = id;
            me.Wait = wait;
            _trace.Add($"{me.Name}: {(wait == null ? "point" : "block")} {id}");
            var next = PickNext();
            if (next == null)
            {
                Deadlock();
                throw new ScheduleAbortException();
            }
            if (next == me)
            {
                me.Wait = null;
                return;
            }
            Release(next);
        }
        me.Gate.Wait();
        if (_aborted) throw new ScheduleAbortException();
    }

    private void Release(Ctl next)
    {
        next.Wait = null;
        next.Gate.Release();
    }

    /// <summary>Called with the lock held. Returns the thread to run next, or null when none can.</summary>
    private Ctl? PickNext()
    {
        var runnable = _threads.Where(t => !t.Finished && (t.Wait == null || t.Wait())).ToList();
        if (runnable.Count == 0) return null;
        if (runnable.Count == 1) return runnable[0];
        var i = _choice.Choose(runnable.Count);
        _choices.Add(i);
        _trace.Add($"  -> choose {runnable[i].Name} of [{string.Join(", ", runnable.Select(t => t.Name))}]");
        return runnable[i];
    }

    private void Deadlock()
    {
        _deadlock = string.Join("; ", _threads.Where(t => !t.Finished).Select(t => $"{t.Name} waits at '{t.LastId}'"));
        _trace.Add("DEADLOCK: " + _deadlock);
        AbortAll();
    }

    private void AbortAll()
    {
        _aborted = true;
        foreach (var t in _threads.Where(t => !t.Finished)) t.Gate.Release();
        _done.Set();
    }
}
