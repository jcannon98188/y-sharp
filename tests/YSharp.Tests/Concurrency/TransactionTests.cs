using System;
using System.Threading;
using Xunit;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Concurrency;

/// <summary>
/// Single-threaded checks of the transaction lifecycle (ADR 0006, 0008) that need no shared type: nesting and joining, the upgrade rule on
/// one thread, the returned value, stale transaction objects, and the internal entry points the mutators use for implicit transactions.
/// Tier: gating. No seeds.
/// </summary>
[Requires(Feature.Transactions)]
[Collection(ConcurrencyCollection.Name)]
[Tier(Tier.Gating)]
public sealed class TransactionTests
{
    // ------------------------------------------------------------------ CC-2

    [Fact, Covers("CC-2")]
    public void ANestedTransact_OnTheOwningThread_GetsTheSameTransaction_AndItsOriginStaysTheOuterOne()
    {
        var doc = Conc.NewDoc();
        Transaction? inner = null;
        doc.Transact(outer =>
        {
            doc.Transact(t => inner = t, "inner");
            Assert.Same(outer, inner);
            Assert.Equal("outer", outer.Origin);
            Assert.True(outer.IsWrite);
            Assert.True(outer.IsLocal);
        }, "outer");
    }

    [Fact, Covers("CC-2")]
    public void AReadInsideAWrite_ReadsThroughTheWriteTransaction()
    {
        var doc = Conc.NewDoc();
        doc.Transact(outer => doc.ReadTransact(read => Assert.Same(outer, read)));
    }

    [Fact, Covers("CC-2")]
    public void AReadInsideARead_JoinsIt_AndTheTransactionIsAReadOne()
    {
        var doc = Conc.NewDoc();
        doc.ReadTransact(outer =>
        {
            Assert.False(outer.IsWrite);
            doc.ReadTransact(inner => Assert.Same(outer, inner));
            Assert.True(outer.IsValid);   // the inner read did not end it
        });
    }

    [Fact, Covers("CC-2")]
    public void ScopesNest_LikeCallbacks()
    {
        var doc = Conc.NewDoc();
        using var outer = doc.BeginTransaction("o");
        using (var inner = doc.BeginTransaction())
            Assert.Same(outer.Transaction, inner.Transaction);
        Assert.True(outer.Transaction.IsValid);
    }

    // ------------------------------------------------------------------ CC-3

    [Fact, Covers("CC-3")]
    public void TryTransact_UnderTheCallersOwnRead_ReturnsUpgradeRefused_WithoutRunningTheBody()
    {
        var doc = Conc.NewDoc();
        var ran = false;
        doc.ReadTransact(_ => Assert.Equal(TryTransactStatus.UpgradeRefused, doc.TryTransact(_ => ran = true).Status));
        Assert.False(ran);
        Assert.True(doc.TryTransact(_ => ran = true).Acquired);
        Assert.True(ran);
    }

    [Fact, Covers("CC-3")]
    public void Transact_UnderTheCallersOwnRead_Throws_AndTheReadLockStaysUsable()
    {
        var doc = Conc.NewDoc();
        doc.ReadTransact(read =>
        {
            Assert.Throws<YConcurrencyException>(() => doc.Transact(static _ => { }));
            Assert.True(read.IsValid);
        });
        Conc.AssertDoesNotWait(() => doc.Transact(static _ => { }), "a writer after the rejected upgrade");
    }

    // ------------------------------------------------------------------ ADR 0008: the value forms

    [Fact, Covers("CC-6")]
    public void Transact_ReturnsTheBodysValue_InBothForms()
    {
        var doc = Conc.NewDoc();
        Assert.Equal(42, doc.Transact(_ => 42));
        Assert.Equal("ab", doc.Transact("a", static (s, _) => s + "b"));
    }

    [Fact, Covers("CC-6")]
    public void TransactWithTimeout_RejectsANegativeTimeout_OtherThanInfinite()
    {
        var doc = Conc.NewDoc();
        var ct = TestContext.Current.CancellationToken;
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.TransactWithTimeout(static _ => { }, null, TimeSpan.FromMilliseconds(-5), ct));
        var ran = false;
        doc.TransactWithTimeout(_ => ran = true, null, Timeout.InfiniteTimeSpan, ct);
        Assert.True(ran);
    }

    // ------------------------------------------------------------------ CC-9

    [Fact, Covers("CC-9")]
    public void ATransactionKeptPastItsBody_ThrowsYLifetimeException_FromEveryMemberButIsValid()
    {
        var doc = Conc.NewDoc();
        Transaction? kept = null;
        doc.Transact(tx => kept = tx);
        Assert.False(kept!.IsValid);
        Assert.Throws<YLifetimeException>(() => kept.Doc);
        Assert.Throws<YLifetimeException>(() => kept.Origin);
        Assert.Throws<YLifetimeException>(() => kept.IsLocal);
        Assert.Throws<YLifetimeException>(() => kept.IsWrite);
        Assert.Throws<YLifetimeException>(() => kept.BeforeState);
        Assert.Throws<YLifetimeException>(() => kept.AfterState);
        Assert.Throws<YLifetimeException>(() => kept.DeleteSet);
        Assert.Throws<YLifetimeException>(() => kept.ChangedTypes);
        Assert.Throws<YLifetimeException>(() => kept.ChangedParentTypes);
    }

    [Fact, Covers("CC-9")]
    public void ACollectionTakenInsideTheTransaction_ThrowsOnceItEnded()
    {
        var doc = Conc.NewDoc();
        System.Collections.Generic.IReadOnlyCollection<YType>? changed = null;
        doc.Transact(tx =>
        {
            changed = tx.ChangedTypes;
            Assert.Empty(changed);
        });
        Assert.Throws<YLifetimeException>(() => changed!.Count);
        Assert.Throws<YLifetimeException>(() => changed!.GetEnumerator().MoveNext());
    }

    [Fact, Covers("CC-9")]
    public void ATransactionKeptPastItsBody_StaysInvalid_WhileALaterTransactionRuns()
    {
        var doc = Conc.NewDoc();
        Transaction? kept = null;
        doc.Transact(tx => kept = tx);
        doc.Transact(_ =>
        {
            Assert.False(kept!.IsValid);
            Assert.Throws<YLifetimeException>(() => kept.Origin);
        }, "later");
        Transaction? keptRead = null;
        doc.ReadTransact(tx => keptRead = tx);
        doc.ReadTransact(_ => Assert.False(keptRead!.IsValid));
    }

    [Fact, Covers("CC-9")]
    public void AReadTransactionKeptPastItsBody_IsInvalid()
    {
        var doc = Conc.NewDoc();
        Transaction? kept = null;
        doc.ReadTransact(tx => kept = tx);
        Assert.False(kept!.IsValid);
        Assert.Throws<YLifetimeException>(() => kept.Doc);
    }

    [Fact, Covers("CC-9")]
    public void InsideItsBody_ATransactionDescribesItsDocument()
    {
        var doc = Conc.NewDoc(7);
        doc.Transact(tx =>
        {
            Assert.Same(doc, tx.Doc);
            Assert.Empty(tx.BeforeState.Clients);
            Assert.Empty(tx.AfterState.Clients);   // empty until cleanup, as in Yjs
            Assert.False(tx.DeleteSet.Contains(new ItemId(7, 0)));
        });
    }

    // ------------------------------------------------------------------ CC-8: the internal entry points of implicit transactions

    [Fact, Covers("CC-8")]
    public void AnImplicitWrite_InsideAnExplicitTransaction_JoinsIt()
    {
        var doc = Conc.NewDoc();
        doc.Transact(outer =>
        {
            var h = doc.EnterWrite(null, local: true, ct: TestContext.Current.CancellationToken);
            try
            {
                Assert.Same(outer, h.Tx);
                Assert.False(h.Initial);
            }
            finally
            {
                doc.Leave(h);
            }

            Assert.True(outer.IsValid);
            Assert.Same(outer, doc.CurrentTransaction);
        });
        Assert.Null(doc.CurrentTransaction);
    }

    [Fact, Covers("CC-8")]
    public void AnImplicitWrite_OutsideATransaction_OpensAndEndsItsOwn()
    {
        var doc = Conc.NewDoc();
        var h = doc.EnterWrite("remote", local: false, ct: TestContext.Current.CancellationToken);
        var tx = h.Tx;
        try
        {
            Assert.True(h.Initial);
            Assert.False(tx.IsLocal);
            Assert.Equal("remote", tx.Origin);
            Assert.Same(tx, doc.CurrentTransaction);
        }
        finally
        {
            doc.Leave(h);
        }

        Assert.False(tx.IsValid);
        Conc.AssertDoesNotWait(() => doc.Transact(static _ => { }), "a writer after the implicit write");
    }

    [Fact, Covers("CC-8")]
    public void AnImplicitRead_JoinsTheCallersReadOrWrite_AndAnotherThreadSeesNone()
    {
        var doc = Conc.NewDoc();
        doc.ReadTransact(read =>
        {
            var h = doc.EnterRead();
            try
            {
                Assert.Same(read, h.Tx);
            }
            finally
            {
                doc.Leave(h);
            }

            Assert.True(read.IsValid);
            Assert.Same(read, doc.CurrentTransaction);
            Transaction? seen = read;
            Bg.Start(() => seen = doc.CurrentTransaction).Join();
            Assert.Null(seen);
        });
    }
}
