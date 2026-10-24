using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Xunit;
using YSharp.TestKit.Rng;

namespace YSharp.TestKit.Concurrency;

/// <summary>What one stress run does (ADR 0006 "Test design": N threads, random operations, one document, fixed or random seed).</summary>
public sealed record StressOptions
{
    public uint Seed { get; init; } = 1;
    public int Writers { get; init; } = 4;
    public int Readers { get; init; } = 2;
    public int OpsPerThread { get; init; } = 200;

    /// <summary>Operations run without an explicit transaction (CC-8). Mutations are then not grouped, so the paired-counter invariant is not asserted.</summary>
    public bool Implicit { get; init; }

    public TimeSpan Watchdog { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The thread count the oversubscription runs use: more threads than cores, capped so CI machines stay sane.</summary>
    public static int Oversubscribed => Math.Clamp(Environment.ProcessorCount * 2, 4, 16);

    public override string ToString() =>
        $"seed={Seed} writers={Writers} readers={Readers} ops={OpsPerThread} {(Implicit ? "implicit" : "explicit")} tx";
}

/// <summary>What a stress run found. Every failure message carries the seed, the options and each thread's operation log, so it replays.</summary>
public sealed class StressResult
{
    public StressOptions Options { get; init; } = new();
    public List<string> Violations { get; } = new();
    public List<string> ThreadErrors { get; } = new();
    public Dictionary<string, List<string>> OpLogs { get; } = new();
    public bool TimedOut { get; set; }
    public int Commits { get; set; }

    public bool Clean => Violations.Count == 0 && ThreadErrors.Count == 0 && !TimedOut;

    public void AssertClean()
    {
        if (Clean) return;
        var sb = new StringBuilder();
        sb.AppendLine("stress run failed: " + Options);
        sb.AppendLine("replay: set YSHARP_SEED=" + Options.Seed + " and rerun this test (per-thread operations are derived from the seed; the interleaving is real-time)");
        if (TimedOut) sb.AppendLine("WATCHDOG: no progress before the timeout; a deadlock or a lost wake-up. Last operation of each thread below.");
        foreach (var v in Violations.Take(20)) sb.AppendLine("violation: " + v);
        foreach (var e in ThreadErrors.Take(10)) sb.AppendLine("thread error: " + e);
        foreach (var (name, log) in OpLogs)
        {
            sb.AppendLine($"{name} (last {Math.Min(15, log.Count)} of {log.Count}):");
            lock (log) foreach (var op in log.Skip(Math.Max(0, log.Count - 15))) sb.Append("  ").AppendLine(op);
        }
        Assert.Fail(sb.ToString());
    }
}

/// <summary>
/// The stress and soak workloads. The same code is the gating stress tier (fixed seed, a second or two, in <c>YSharp.Tests</c>) and the
/// nightly soak (random seeds, much longer, in <c>YSharp.Fuzz.Tests</c>).
/// <para>
/// Invariants (ADR 0006): serializability (the <c>update</c> stream, which fires inside the write lock, replayed in order on a fresh
/// document reproduces the state byte for byte), no torn reads (a counter kept in the same write transaction as the data it counts), convergence
/// of documents synced across threads, no deadlock (watchdog), no leaks (the lock is free afterwards) and read purity (concurrent reads leave the
/// encoded state unchanged and return what a single-threaded read returns).
/// </para>
/// </summary>
public static class StressHarness
{
    private static string Mix(string what, uint seed, int thread) => $"{what}[{thread}] seed {seed}";

    private static IPrng Gen(uint seed, int thread) => Prng.Create(unchecked(seed * 2654435761u + (uint)thread * 40503u + 1));

    private static int Pick(IPrng g, int max) => (int)Prng.Int32(g, 0, max);

    /// <summary>Probe on the calling thread so the stub's NotImplementedException surfaces as a skip, not as a worker-thread error.</summary>
    private static void Probe() => _ = new Doc().GetText("probe");

    // ------------------------------------------------------------------ workload 1: one document, many threads

    public static StressResult RunSharedDoc(StressOptions o)
    {
        Probe();
        var result = new StressResult { Options = o };
        var doc = new Doc(new DocOptions { ClientId = 1 });
        var arr = doc.GetArray("a");
        var map = doc.GetMap("m");
        var text = doc.GetText("t");
        var updates = new List<byte[]>();
        doc.Update += (u, _, _, _) => updates.Add(u.ToArray());   // fires inside the write lock: commit order

        var stop = 0;
        var workers = new List<Thread>();
        var readerCount = 0;

        void Spawn(string name, Action<IPrng, List<string>> body, int index)
        {
            var log = new List<string>();
            lock (result.OpLogs) result.OpLogs[name] = log;
            var t = new Thread(() =>
            {
                try { body(Gen(o.Seed, index), log); }
                catch (Exception e)
                {
                    lock (result.ThreadErrors) result.ThreadErrors.Add($"{name}: {e}");
                    Interlocked.Exchange(ref stop, 1);
                }
            }) { IsBackground = true, Name = name };
            workers.Add(t);
        }

        for (var w = 0; w < o.Writers; w++)
        {
            var index = w;
            Spawn($"writer{w}", (g, log) =>
            {
                for (var i = 0; i < o.OpsPerThread && Volatile.Read(ref stop) == 0; i++)
                {
                    var kind = Pick(g, 5);
                    var at = Pick(g, 1000);
                    var key = "k" + Pick(g, 7);
                    var word = "w" + Pick(g, 99);
                    log.Add($"{i}: kind={kind} at={at} key={key}");
                    void Apply()
                    {
                        switch (kind)
                        {
                            case 0: arr.Push(new YEntry[] { index * 1000 + i }); break;
                            case 1: arr.Insert(at % (arr.Length + 1), new YEntry[] { index * 1000 + i }); break;
                            case 2: if (arr.Length > 0) arr.Delete(at % arr.Length); break;
                            case 3: text.Insert(at % (text.Length + 1), word); break;
                            case 4: if (text.Length > 0) text.Delete(at % text.Length, 1); break;
                            default: map.Set(key, (double)i); break;
                        }
                    }
                    if (o.Implicit) Apply();
                    else
                        doc.Transact(_ =>
                        {
                            Apply();
                            map.Set("len", (double)arr.Length);
                            map.Set("tlen", (double)text.Length);
                        });
                }
            }, index);
        }

        for (var r = 0; r < o.Readers; r++)
        {
            var index = 100 + r;
            Interlocked.Increment(ref readerCount);
            Spawn($"reader{r}", (g, log) =>
            {
                for (var i = 0; Volatile.Read(ref stop) == 0 && i < o.OpsPerThread * 10; i++)
                {
                    log.Add($"{i}: read");
                    if (o.Implicit)
                    {
                        var n = arr.Length;
                        if (n > 0) Sink(arr[Pick(g, 100000) % n]);   // may race a delete in implicit mode; the index is re-checked by the library
                        continue;
                    }
                    doc.ReadTransact(rtx =>
                    {
                        var n = arr.Length;
                        var tl = text.Length;
                        var hasLen = map.TryGet("len", out var lenEntry);
                        map.TryGet("tlen", out var tlenEntry);
                        var expectLen = hasLen ? (int)lenEntry.Value.AsNumber() : 0;
                        var expectTlen = hasLen ? (int)tlenEntry.Value.AsNumber() : 0;
                        if (n != expectLen) lock (result.Violations) result.Violations.Add($"torn read: array length {n}, counter {expectLen}");
                        if (tl != expectTlen) lock (result.Violations) result.Violations.Add($"torn read: text length {tl}, counter {expectTlen}");
                        if (n > 0) Sink(arr[Pick(g, 100000) % n]);
                        if (tl > 0) Sink(text.ToString());
                    });
                }
            }, index);
        }

        foreach (var t in workers) t.Start();
        var deadline = DateTime.UtcNow + o.Watchdog;
        foreach (var t in workers.Where(t => t.Name!.StartsWith("writer", StringComparison.Ordinal)))
            if (!t.Join(Remaining(deadline))) { result.TimedOut = true; break; }
        Interlocked.Exchange(ref stop, 1);
        foreach (var t in workers)
            if (!t.Join(result.TimedOut ? TimeSpan.FromSeconds(1) : Remaining(deadline))) result.TimedOut = true;
        if (result.TimedOut) return result;

        // no leaks (invariant 5): the lock is free and no transaction is open
        var free = false;
        var acquired = doc.TryTransact(_ => free = true);
        if (!acquired.Acquired || !free) result.Violations.Add("the write lock was not free after every thread finished");
        doc.ReadTransact(_ => { });

        // serializability (invariant 1)
        var expected = doc.EncodeStateAsUpdate();
        var replica = new Doc(new DocOptions { ClientId = 2 });
        foreach (var u in updates) replica.ApplyUpdate(u);
        var replayed = replica.EncodeStateAsUpdate();
        result.Commits = updates.Count;
        if (!expected.AsSpan().SequenceEqual(replayed))
            result.Violations.Add($"serializability: replaying the {updates.Count}-update commit stream gave different state bytes ({Convert.ToHexString(replayed).Length / 2} vs {expected.Length})");
        if (!o.Implicit)
        {
            var counter = replica.GetMap("m").TryGet("len", out var c) ? (int)c.Value.AsNumber() : 0;
            if (counter != replica.GetArray("a").Length) result.Violations.Add("the replayed document's counter does not match its array");
        }
        return result;
    }

    private static void Sink(object? value) => GC.KeepAlive(value);

    private static TimeSpan Remaining(DateTime deadline)
    {
        var left = deadline - DateTime.UtcNow;
        return left < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : left;
    }

    // ------------------------------------------------------------------ workload 2: read purity (CC-4)

    /// <summary>Fills a document, then reads it from many threads at random positions. Reads must return what a single-threaded read returns and must not change the encoded state.</summary>
    public static StressResult RunReadPurity(StressOptions o)
    {
        Probe();
        var result = new StressResult { Options = o };
        var doc = new Doc(new DocOptions { ClientId = 1 });
        var arr = doc.GetArray("a");
        var text = doc.GetText("t");
        var g0 = Gen(o.Seed, 0);
        doc.Transact(_ =>
        {
            for (var i = 0; i < 400; i++)
            {
                arr.Insert(Pick(g0, i), new YEntry[] { i });
                text.Insert(Pick(g0, text.Length), "x" + i);
            }
        });
        var before = doc.EncodeStateAsUpdate();
        var expectedArray = doc.ReadTransactResult(() => arr.ToArray().Select(e => e.Value.AsNumber()).ToArray());
        var expectedText = doc.ReadTransactResult(() => text.ToString());

        var threads = new List<Thread>();
        var n = Math.Max(2, o.Readers);
        for (var r = 0; r < n; r++)
        {
            var index = r;
            var log = new List<string>();
            result.OpLogs[$"reader{r}"] = log;
            threads.Add(new Thread(() =>
            {
                try
                {
                    var g = Gen(o.Seed, index + 1);
                    for (var i = 0; i < o.OpsPerThread * 4; i++)
                    {
                        var at = Pick(g, expectedArray.Length - 1);
                        log.Add($"{i}: arr[{at}]");
                        var got = doc.ReadTransactResult(() => arr[at].Value.AsNumber());
                        if (got != expectedArray[at]) lock (result.Violations) result.Violations.Add($"arr[{at}] read {got}, single-threaded read {expectedArray[at]}");
                        if (i % 50 == 0 && doc.ReadTransactResult(() => text.ToString()) != expectedText)
                            lock (result.Violations) result.Violations.Add("text changed under readers");
                    }
                }
                catch (Exception e) { lock (result.ThreadErrors) result.ThreadErrors.Add($"reader{index}: {e}"); }
            }) { IsBackground = true });
        }
        foreach (var t in threads) t.Start();
        var deadline = DateTime.UtcNow + o.Watchdog;
        foreach (var t in threads) if (!t.Join(Remaining(deadline))) { result.TimedOut = true; break; }
        if (result.TimedOut) return result;
        if (!before.AsSpan().SequenceEqual(doc.EncodeStateAsUpdate())) result.Violations.Add("read purity: concurrent reads changed the encoded state");
        return result;
    }

    // ------------------------------------------------------------------ workload 3: convergence

    /// <summary>Each thread owns a document and edits it; threads sync pairwise at random through <c>ApplyUpdate</c>. Afterwards all documents agree.</summary>
    public static StressResult RunConvergence(StressOptions o)
    {
        Probe();
        var result = new StressResult { Options = o };
        var count = Math.Max(2, o.Writers);
        var docs = Enumerable.Range(0, count).Select(i => new Doc(new DocOptions { ClientId = (uint)(10 + i) })).ToArray();
        var threads = new List<Thread>();
        for (var d = 0; d < count; d++)
        {
            var me = d;
            var log = new List<string>();
            result.OpLogs[$"peer{d}"] = log;
            threads.Add(new Thread(() =>
            {
                try
                {
                    var g = Gen(o.Seed, me);
                    var text = docs[me].GetText("t");
                    var arr = docs[me].GetArray("a");
                    for (var i = 0; i < o.OpsPerThread; i++)
                    {
                        var kind = Pick(g, 3);
                        log.Add($"{i}: kind={kind}");
                        switch (kind)
                        {
                            case 0: text.Insert(Pick(g, text.Length), "p" + me); break;
                            case 1: arr.Push(new YEntry[] { me * 1000 + i }); break;
                            default:
                                var other = (me + 1 + Pick(g, count - 2)) % count;
                                var svBytes = new System.Buffers.ArrayBufferWriter<byte>();
                                docs[me].EncodeStateVector(svBytes);
                                var diff = docs[other].EncodeStateAsUpdate(svBytes.WrittenSpan);   // one lock at a time: never nested across documents
                                docs[me].ApplyUpdate(diff, "peer" + other);
                                break;
                        }
                    }
                }
                catch (Exception e) { lock (result.ThreadErrors) result.ThreadErrors.Add($"peer{me}: {e}"); }
            }) { IsBackground = true });
        }
        foreach (var t in threads) t.Start();
        var deadline = DateTime.UtcNow + o.Watchdog;
        foreach (var t in threads) if (!t.Join(Remaining(deadline))) { result.TimedOut = true; break; }
        if (result.TimedOut) return result;

        for (var round = 0; round < 2; round++)
            for (var i = 0; i < count; i++)
                for (var j = 0; j < count; j++)
                    if (i != j) docs[j].ApplyUpdate(docs[i].EncodeStateAsUpdate(Sv(docs[j])));
        var reference = docs[0].EncodeStateAsUpdate();
        for (var i = 1; i < count; i++)
        {
            if (!docs[i].GetText("t").ToString().Equals(docs[0].GetText("t").ToString(), StringComparison.Ordinal))
                result.Violations.Add($"convergence: peer {i} text differs from peer 0");
            if (docs[i].GetArray("a").Length != docs[0].GetArray("a").Length)
                result.Violations.Add($"convergence: peer {i} array length differs from peer 0");
            if (!docs[i].EncodeStateAsUpdate().AsSpan().SequenceEqual(reference))
                result.Violations.Add($"convergence: peer {i} state bytes differ from peer 0");
        }
        return result;
    }

    private static byte[] Sv(Doc d)
    {
        var w = new System.Buffers.ArrayBufferWriter<byte>();
        d.EncodeStateVector(w);
        return w.WrittenSpan.ToArray();
    }

    private static T ReadTransactResult<T>(this Doc doc, Func<T> read)
    {
        T value = default!;
        doc.ReadTransact(_ => value = read());
        return value;
    }
}
