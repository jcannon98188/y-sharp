using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Concurrency;

/// <summary>CC-15..CC-17: thread-affine transactions, exception safety, Destroy. Tier: gating. No seeds.</summary>
[Requires(Feature.Transactions)]
[Collection(ConcurrencyCollection.Name)]
[Tier(Tier.Gating)]
public sealed class LifecycleTests
{
    // ------------------------------------------------------------------ CC-15

    [Fact, Covers("CC-15")]
    [LibraryIndependent]
    public void TheScopedForm_IsARefStruct_SoItCannotCrossAnAwaitOrBeDisposedOnAnotherThread()
    {
        // The stub picks the ref struct option TASK-1.14 left open. Thread affinity is then enforced by the compiler: the scope cannot be
        // captured by a lambda, boxed, stored in a field or used across await, so "dispose on a different thread" cannot be written.
        // The YConcurrencyException ADR 0006 once described for a cross-thread Dispose is therefore unreachable by design (ADR 0006 CC-15 records this);
        // the one reachable thread-affinity failure is a transaction object kept past its body, which throws YLifetimeException (HandleTests, CC-9).
        Assert.True(typeof(TransactionScope).IsByRefLike);
        Assert.True(typeof(TransactionScope).IsValueType);
    }

    [Fact, Covers("CC-15")]
    [LibraryIndependent]
    public void Contract_PublicApi_IsSyncOnlyWithNoTaskMembers()
    {
        bool IsTask(Type t)
        {
            if (t.IsByRef || t.IsArray) return IsTask(t.GetElementType()!);
            if (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(Task<>) || t.GetGenericTypeDefinition() == typeof(ValueTask<>))) return true;
            if (t == typeof(Task) || t == typeof(ValueTask) || t.IsSubclassOf(typeof(Task))) return true;
            return t.IsGenericType && t.GetGenericArguments().Any(IsTask);
        }
        var offenders = new List<string>();
        foreach (var type in typeof(Doc).Assembly.GetExportedTypes())
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                if (IsTask(m.ReturnType) || m.GetParameters().Any(p => IsTask(p.ParameterType)))
                    offenders.Add($"{type.Name}.{m.Name}");
        Assert.True(offenders.Count == 0, "async API in a sync-only contract: " + string.Join(", ", offenders));
    }

    [Fact, Covers("CC-15")]
    public void ATransactionObject_UsedAfterItsBodyEnded_IsInvalid_NotAWayToReenterFromAnotherThread()
    {
        var doc = Conc.NewDoc();
        Transaction? leaked = null;
        doc.Transact(tx => leaked = tx);
        var bg = Bg.Start(() => Assert.False(leaked!.IsValid));
        bg.Join();
    }

    [Fact, Covers("CC-15")]
    [LibraryIndependent]
    public void TheCallbackForm_CannotBeAwaitedInside_ItsDelegateReturnsVoid()
    {
        var transact = typeof(Doc).GetMethods().Where(m => m.Name is "Transact" or "ReadTransact" or "TryTransact");
        Assert.All(transact, m => Assert.DoesNotContain(m.GetParameters(), p => typeof(Delegate).IsAssignableFrom(p.ParameterType) && IsAsyncDelegate(p.ParameterType)));
    }

    private static bool IsAsyncDelegate(Type t)
    {
        var invoke = t.GetMethod("Invoke")!;
        return typeof(Task).IsAssignableFrom(invoke.ReturnType) || invoke.ReturnType == typeof(ValueTask);
    }

    // ------------------------------------------------------------------ CC-16

    [Fact, Covers("CC-16")]
    [Requires(Feature.Array, Feature.Events)]
    public void WhenABodyThrows_CommitAndCleanupStillRun_TheLockIsReleased_AndTheExceptionPropagates()
    {
        var doc = Conc.NewDoc();
        var arr = doc.GetArray("a");
        var after = 0;
        var cleanup = 0;
        var updates = 0;
        doc.AfterTransaction += (_, _) => after++;
        doc.AfterTransactionCleanup += (_, _) => cleanup++;
        doc.Update += (_, _, _, _) => updates++;
        var boom = new InvalidOperationException("boom");
        var thrown = Assert.Throws<InvalidOperationException>(() => doc.Transact(_ =>
        {
            arr.Push(Conc.One);
            throw boom;
        }));
        Assert.Same(boom, thrown);
        Assert.Equal(1, after);
        Assert.Equal(1, cleanup);
        Assert.Equal(1, updates);                               // what the body did before it threw is committed
        Assert.Equal(1, arr.Length);
        Conc.AssertDoesNotWait(() => doc.Transact(static _ => { }), "a writer after the throwing body");
    }

    [Fact, Covers("CC-16")]
    public void ReadBody_Throwing_ReleasesTheReadLock()
    {
        var doc = Conc.NewDoc();
        Assert.Throws<InvalidOperationException>(() => doc.ReadTransact(_ => throw new InvalidOperationException("boom")));
        Conc.AssertDoesNotWait(() => doc.Transact(static _ => { }), "a writer after the throwing read");
    }

    [Fact, Covers("CC-16")]
    [Requires(Feature.Array, Feature.Events)]
    public void ANestedBodyThatThrows_AndIsCaught_DoesNotEndTheOuterTransaction()
    {
        var doc = Conc.NewDoc();
        var arr = doc.GetArray("a");
        var updates = 0;
        doc.Update += (_, _, _, _) => updates++;
        doc.Transact(_ =>
        {
            arr.Push(Conc.One);
            Assert.Throws<InvalidOperationException>(() => doc.Transact(_ => throw new InvalidOperationException("inner")));
            arr.Push(Conc.One);
        });
        Assert.Equal(1, updates);
        Assert.Equal(2, arr.Length);
    }

    private static void ThrowInsideScope(Doc doc)
    {
        using var scope = doc.BeginTransaction();
        doc.GetArray("a").Push(Conc.One);
        throw new InvalidOperationException("boom");
    }

    [Fact, Covers("CC-16")]
    [Requires(Feature.Array)]
    public void TheScopedForm_ReleasesOnException_AndDoubleDisposeIsIdempotent()
    {
        var doc = Conc.NewDoc();
        Assert.Throws<InvalidOperationException>(() => ThrowInsideScope(doc));
        Assert.Equal(1, doc.GetArray("a").Length);
        Conc.AssertDoesNotWait(() => doc.Transact(static _ => { }), "a writer after the throwing scope");

        var scope = doc.BeginTransaction();
        scope.Dispose();
        scope.Dispose();                                        // idempotent
        var readScope = doc.BeginReadTransaction();
        readScope.Dispose();
        readScope.Dispose();
        Conc.AssertDoesNotWait(() => doc.Transact(static _ => { }), "a writer after double Dispose");
    }

    private static void LeakAScope(Doc doc)
    {
        var scope = doc.BeginTransaction();
        GC.KeepAlive(scope.Transaction);
        // never disposed
    }

    [Fact, Covers("CC-16")]
    public void ATransactionThatIsNeverDisposed_LeaksTheLock_ItIsACallerErrorAndNotDetected()
    {
        var doc = Conc.NewDoc();
        Bg.Start(() => LeakAScope(doc)).Join();
        TryTransactResult result = default;
        Conc.AssertDoesNotWait(() => result = doc.TryTransact(static _ => { }), "TryTransact on a leaked lock");
        Assert.Equal(TryTransactStatus.Contended, result.Status);
    }

    // ------------------------------------------------------------------ CC-17

    [Fact, Covers("CC-17")]
    public void Destroy_IsAWriteTransaction_AndWaitsForAWriterToFinish()
    {
        var doc = Conc.NewDoc();
        using var holder = Holder.Write(doc);
        Conc.AssertWaitsFor(holder, doc.Destroy, "Destroy while a writer held the lock");
        Assert.True(doc.IsDestroyed);
    }

    [Fact, Covers("CC-17")]
    public void Destroy_WaitsForReaders_Too()
    {
        var doc = Conc.NewDoc();
        using var holder = Holder.Read(doc);
        Conc.AssertWaitsFor(holder, doc.Destroy, "Destroy while a reader held the lock");
        Assert.True(doc.IsDestroyed);
    }

    [Fact, Covers("CC-17")]
    public void Destroy_OnTheOwningThread_JoinsTheTransaction_AndDoesNotDeadlock()
    {
        var doc = Conc.NewDoc();
        var destroying = 0;
        doc.Destroying += _ => destroying++;
        Bg.Start(() => doc.Transact(_ => doc.Destroy())).Join();
        Assert.True(doc.IsDestroyed);
        Assert.Equal(1, destroying);
    }

    [Fact, Covers("CC-17")]
    [Requires(Feature.Array)]
    public void ADestroyedDocument_KeepsAcceptingAcquires_OnlyIsDestroyedChanges()
    {
        var doc = Conc.NewDoc();
        doc.GetArray("a").Push(Conc.One);
        doc.Destroy();
        Assert.True(doc.IsDestroyed);
        var ran = 0;
        doc.Transact(_ => ran++);
        doc.ReadTransact(_ => ran++);
        Assert.True(doc.TryTransact(_ => ran++).Acquired);
        Assert.Equal(3, ran);
        Conc.AssertDoesNotWait(() => doc.Transact(static _ => { }), "a writer on a destroyed document");
    }
}
