using System;
using System.Diagnostics;
using System.Threading;
using Xunit;
using YSharp.TestKit.Allocation;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;
using YSharp.Tests.Allocation;

namespace YSharp.Tests.Concurrency;

/// <summary>
/// CC-6 and CC-7: the blocking and non-blocking acquire forms and their allocation. Timeouts here are real time (50 to 100 ms): the contract
/// is about the wait, not a latency bound. Tier: gating. No seeds.
/// </summary>
[Requires(Feature.Transactions)]
[Collection(ConcurrencyCollection.Name)]
[Tier(Tier.Gating)]
public sealed class AcquireTests
{
    private static readonly Action<Transaction> Nop = static _ => { };

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(80);

    // ------------------------------------------------------------------ CC-6

    [Fact, Covers("CC-6")]
    public void ABlockingTransact_WithATimeout_ThrowsTimeoutException_WhileTheLockIsHeld()
    {
        var doc = Conc.NewDoc();
        using var holder = Holder.Write(doc);
        var ran = false;
        var clock = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => Conc.TransactFor(doc, _ => ran = true, Short));
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(40), $"gave up after {clock.Elapsed.TotalMilliseconds} ms, long before the {Short.TotalMilliseconds} ms timeout");
        Assert.False(ran);
    }

    [Fact, Covers("CC-6")]
    public void ABlockingTransact_WithATimeout_RunsTheBodyWhenTheLockIsFree()
    {
        var doc = Conc.NewDoc();
        var ran = false;
        doc.TransactWithTimeout(_ => ran = true, "origin", Short, TestContext.Current.CancellationToken);
        Assert.True(ran);
    }

    [Fact, Covers("CC-6")]
    public void ABlockingTransact_ThrowsOperationCanceledException_WhenTheTokenIsCancelledWhileWaiting()
    {
        var doc = Conc.NewDoc();
        using var holder = Holder.Write(doc);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(Short);
        var ran = false;
        Assert.ThrowsAny<OperationCanceledException>(() => Conc.TransactUntilCancelled(doc, _ => ran = true, cts.Token));
        Assert.False(ran);
    }

    [Fact, Covers("CC-6")]
    public void AGiveUp_LeavesTheLockIntact_NoStaleWaitingWriterBlocksLaterReaders()
    {
        var doc = Conc.NewDoc();
        using var holder = Holder.Read(doc);
        Assert.Throws<TimeoutException>(() => Conc.TransactFor(doc, Nop, Short));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(Short);
        Assert.ThrowsAny<OperationCanceledException>(() => Conc.TransactUntilCancelled(doc, Nop, cts.Token));
        // writers that gave up no longer count as waiting: a new reader shares the lock at once
        Conc.AssertDoesNotWait(() => doc.ReadTransact(Nop), "a reader after two writers gave up");
        holder.Release();
        doc.Transact(Nop);
    }

    [Fact, Covers("CC-6")]
    public void TryTransact_ReturnsAcquired_AndRunsTheBody_WhenTheLockIsFree()
    {
        var doc = Conc.NewDoc();
        var ran = false;
        var result = doc.TryTransact(_ => ran = true);
        Assert.True(result.Acquired);
        Assert.Equal(TryTransactStatus.Acquired, result.Status);
        Assert.True(ran);
    }

    [Fact, Covers("CC-6")]
    public void TryTransact_ReturnsContended_WithoutBlockingOrRunningTheBody_WhenAnotherThreadHoldsTheLock()
    {
        var doc = Conc.NewDoc();
        foreach (var holderIsWriter in new[] { true, false })
        {
            using var holder = holderIsWriter ? Holder.Write(doc) : Holder.Read(doc);
            var ran = false;
            var clock = Stopwatch.StartNew();
            var result = doc.TryTransact(_ => ran = true);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), "TryTransact waited");
            Assert.False(result.Acquired);
            Assert.Equal(TryTransactStatus.Contended, result.Status);
            Assert.False(ran);
        }
    }

    [Fact, Covers("CC-6", "CC-2")]
    public void TryTransact_OnTheOwningThread_JoinsTheTransaction()
    {
        var doc = Conc.NewDoc();
        doc.Transact(_ =>
        {
            var ran = false;
            Assert.True(doc.TryTransact(_ => ran = true).Acquired);
            Assert.True(ran);
        });
    }

    // ------------------------------------------------------------------ CC-7 (BUD-01, BUD-02)

    // Owner decision (TASK-4.12): an uncontended acquire allocates only the small Transaction its body receives (BUD-01, BUD-02 limits);
    // a contended TryTransact runs no body and allocates nothing.

    [Fact, Covers("CC-7")]
    public void Acquire_UncontendedAcquireAndRelease_StaysWithinItsBudget()
    {
        var doc = Conc.NewDoc();
        AllocationBudgets.Check("BUD-01", "Transact", () => doc.Transact(0, static (_, _) => { }));
        AllocationBudgets.Check("BUD-01", "ReadTransact", () => doc.ReadTransact(Nop));
    }

    [Fact, Covers("CC-7")]
    public void TryTransact_Uncontended_StaysWithinItsBudget()
    {
        var doc = Conc.NewDoc();
        AllocationBudgets.Check("BUD-02", "Uncontended", () => doc.TryTransact(Nop));
    }

    [Fact, Covers("CC-7")]
    public void TryTransact_AllocatesNothing_EvenWhenContended()
    {
        var doc = Conc.NewDoc();
        using var holder = Holder.Write(doc);
        AllocationAssert.AtMost("BUD-02", 0, () => doc.TryTransact(Nop));
    }
}
