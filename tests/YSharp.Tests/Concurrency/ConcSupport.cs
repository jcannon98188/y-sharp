using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;
using YSharp.TestKit.Stubs;

namespace YSharp.Tests.Concurrency;

/// <summary>
/// Concurrency tests share static state (the schedule seam, thread counts, allocation counters) and need real cores, so every class of
/// Spec 12 is in this one collection, which xUnit runs without parallelism (ADR 0006, 0015).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConcurrencyCollection
{
    public const string Name = "Concurrency";
}

/// <summary>
/// Spec 12 tests assert against y-sharp, which is still the API stub (ADR 0019). Every body goes through <see cref="Run"/>: while the stub
/// throws <see cref="NotImplementedException"/> the test is skipped visibly, and the moment the implementation exists the same assertions run.
/// </summary>
internal static class Impl
{
    public static void Run(Action body)
    {
        // Probe first: work on other threads would otherwise see the stub's exception only there.
        ApiStub.SkipIfStub();
        ApiStub.Run(body);
    }
}

/// <summary>A body running on its own thread, so a test can see whether it is blocked and what it ended with.</summary>
internal sealed class Bg
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _done = new(false);

    private Bg(Action body)
    {
        _thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { Error = e; }
            finally { _done.Set(); }
        }) { IsBackground = true, Name = "bg" };
        _thread.Start();
        ThreadId = _thread.ManagedThreadId;
    }

    public static Bg Start(Action body) => new(body);

    public Exception? Error { get; private set; }
    public int ThreadId { get; }

    /// <summary>How long a negative check waits before it decides "still blocked". Long enough to catch a missing lock, short enough for CI.</summary>
    public static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(150);

    public static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    public bool IsDone => _done.IsSet;

    public bool Finishes(TimeSpan within) => _done.Wait(within);

    public void AssertStillBlocked() => Assert.False(_done.Wait(Blocked), "the call returned although the lock should have kept it waiting");

    /// <summary>Waits for the body and rethrows what it threw.</summary>
    public void Join()
    {
        Assert.True(_done.Wait(Generous), "the call never returned (deadlock or lost wake-up)");
        if (Error is NotImplementedException) throw Error;
        if (Error != null) throw new AggregateException(Error);
    }

    public void JoinAllowing<T>() where T : Exception
    {
        Assert.True(_done.Wait(Generous), "the call never returned (deadlock or lost wake-up)");
        if (Error != null && Error is not T) throw new AggregateException(Error);
    }
}

/// <summary>Holds a document's lock on a background thread until released, so other threads can be checked against it.</summary>
internal sealed class Holder : IDisposable
{
    private readonly ManualResetEventSlim _entered = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private readonly Bg _thread;

    private Holder(Doc doc, bool write)
    {
        Action<Transaction> body = _ =>
        {
            _entered.Set();
            _release.Wait(Bg.Generous);
        };
        _thread = Bg.Start(() =>
        {
            if (write) doc.Transact(body);
            else doc.ReadTransact(body);
        });
        Assert.True(_entered.Wait(Bg.Generous), "the holder never got the lock");
        HolderThreadId = _thread.ThreadId;
    }

    public int HolderThreadId { get; }

    public static Holder Write(Doc doc) => new(doc, true);

    public static Holder Read(Doc doc) => new(doc, false);

    public void Release()
    {
        _release.Set();
        _thread.Join();
    }

    public void Dispose() => Release();
}

internal static class Conc
{
    public static Doc NewDoc(ulong client = 1) => new(new DocOptions { ClientId = client });

    public static YEntry[] One => new YEntry[] { 1 };   // a property: building a YEntry throws on the API stub

    /// <summary>The blocking timed form: <c>TransactWithTimeout</c> has a name of its own, so a null origin cannot bind to the state-passing <c>Transact</c>.</summary>
    public static void TransactFor(Doc doc, Action<Transaction> body, TimeSpan timeout) =>
        doc.TransactWithTimeout(body, null, timeout);

    public static void TransactUntilCancelled(Doc doc, Action<Transaction> body, CancellationToken ct) =>
        doc.TransactWithTimeout(body, null, Timeout.InfiniteTimeSpan, ct);

    /// <summary>The seeds the gating tier runs per scenario; <c>YSHARP_SEED</c> pins one to replay a failure.</summary>
    public static IReadOnlyList<uint> Seeds => YSharp.TestKit.Concurrency.Explorer.GatingSeeds;

    /// <summary>Runs <paramref name="body"/> on another thread that is expected to block until a holder lets go; checks both halves.</summary>
    public static void AssertWaitsFor(Holder holder, Action body, string what)
    {
        var bg = Bg.Start(body);
        Assert.False(bg.Finishes(Bg.Blocked), $"{what} ran while the lock was held");
        holder.Release();
        bg.Join();
    }

    /// <summary>Runs <paramref name="body"/> on another thread that is expected to finish while the lock is still held.</summary>
    public static void AssertDoesNotWait(Action body, string what)
    {
        var bg = Bg.Start(body);
        Assert.True(bg.Finishes(Bg.Generous), $"{what} waited for the lock");
        bg.Join();
    }
}
