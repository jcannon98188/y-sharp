#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using YSharp.Codecs;
using YSharp.Codecs.V1;
using YSharp.Codecs.V2;
using YSharp.Lib0;
using YSharp.Store;

namespace YSharp;

// The lifecycle hooks of spec 27 (Doc.Cleanup.cs): the doc events, the type observers (Yjs callTypeObservers and the deep phase of
// cleanupTransactions) and the update events. Every handler runs contained (ADR 0005): an exception is reported through ObserverFaulted and
// the next handler and the rest of the commit still run; only OutOfMemoryException propagates (ERR-19).
public sealed partial class Doc
{
    /// <summary>The handler faults reported on this thread so far; TryApplyUpdate reports the difference as <see cref="ApplyResult.ObserverFaults"/>.</summary>
    [ThreadStatic] private static int t_observerFaults;

    internal static int ObserverFaultCount => t_observerFaults;

    partial void HookBeforeAllTransactions(Transaction tx) => Raise(BeforeAllTransactions, tx);

    partial void HookBeforeTransaction(Transaction tx) => Raise(BeforeTransaction, tx);

    partial void HookBeforeObservers(Transaction tx) => Raise(BeforeObserverCalls, tx);

    partial void HookAfterTransaction(Transaction tx) => Raise(AfterTransaction, tx);

    /// <summary>Yjs: <c>afterTransactionCleanup</c>, then <c>update</c> and <c>updateV2</c>, encoded only while subscribed and when the transaction changed something (DOC-10, TX-3).</summary>
    partial void HookAfterTransactionCleanup(Transaction tx)
    {
        Raise(AfterTransactionCleanup, tx);
        var state = tx.State;
        var update = Update;
        if (update is not null && state.HasContent)
        {
            using var w = Lib0Writer.Rent(256);
            var encoder = new UpdateEncoderV1(w);
            UpdateWriter.WriteClientsStructs(ref encoder, this, state.BeforeState);
            UpdateWriter.WriteDeleteSet(ref encoder, state.DeleteRanges);
            RaiseUpdate(update, w.WrittenSpan, tx);
        }

        var updateV2 = UpdateV2;
        if (updateV2 is not null && state.HasContent)
        {
            using var encoder = new UpdateEncoderV2();
            var e = encoder;
            UpdateWriter.WriteClientsStructs(ref e, this, state.BeforeState);
            UpdateWriter.WriteDeleteSet(ref e, state.DeleteRanges);
            var buffer = new ArrayBufferWriter<byte>();
            encoder.CopyTo(buffer);
            RaiseUpdate(updateV2, buffer.WrittenSpan, tx);
        }
    }

    partial void HookAfterAllTransactions(IReadOnlyList<Transaction> transactions)
    {
        if (AfterAllTransactions is not { } handlers) return;
        foreach (var handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(transactions, this);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ReportObserverFault(e, this);
            }
        }
    }

    /// <summary>
    /// Yjs <c>callTypeObservers</c> for each changed type in the order it first changed (types whose item is deleted fire nothing, A-E9), then
    /// the deep phase: each observed ancestor, in the order its first event reached it, gets the events of the types below it that are still
    /// alive, top-level events first (TX-10, Q25). An event is only built when someone listens on the type or above it.
    /// </summary>
    partial void HookObservers(Transaction tx)
    {
        var state = tx.State;
        state.ObserversCalled = true;
        if (state.ChangedOrNull is not { Count: > 0 } changed) return;
        var order = state.ChangedOrder;
        List<YType>? deepTypes = null;
        Dictionary<YType, List<YEvent>>? deepEvents = null;
        for (var i = 0; i < order.Count; i++)
        {
            var type = order[i];
            if (!changed.TryGetValue(type, out var subs) || type is YUntyped || type.Branch.Item is { Deleted: true }) continue;
            var handlers = type.Handlers;
            var observedAbove = false;
            for (var b = type.Branch; ; b = b.Item!.Parent!)
            {
                if (b.Owner is YType { DeepHandlers.Length: > 0 }) observedAbove = true;
                if (b.Item is null || observedAbove) break;
            }

            if (handlers.Length == 0 && !observedAbove) continue;
            if (CreateEvent(type, tx, subs) is not { } e) continue;
            if (observedAbove)
            {
                for (var b = type.Branch; ; b = b.Item!.Parent!)
                {
                    if (b.Owner is YType { DeepHandlers.Length: > 0 } ancestor)
                    {
                        deepEvents ??= new Dictionary<YType, List<YEvent>>(ReferenceEqualityComparer.Instance);
                        if (!deepEvents.TryGetValue(ancestor, out var list))
                        {
                            deepEvents[ancestor] = list = new List<YEvent>();
                            (deepTypes ??= new List<YType>()).Add(ancestor);
                        }

                        list.Add(e);
                    }

                    if (b.Item is null) break;
                }
            }

            foreach (var registration in handlers)
            {
                try
                {
                    ((YEventHandler<YEvent>)registration.Handler)(e, tx);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ReportObserverFault(ex, type);
                }
            }
        }

        if (deepTypes is null) return;
        foreach (var type in deepTypes)
        {
            var handlers = type.DeepHandlers;
            if (handlers.Length == 0 || type.Branch.Item is { Deleted: true }) continue;
            var events = new List<YEvent>();
            foreach (var e in deepEvents![type])
                if (e.Target.Branch.Item is not { Deleted: true }) events.Add(e);
            var depths = new int[events.Count];
            for (var i = 0; i < events.Count; i++)
            {
                events[i].SetCurrentTarget(type);
                depths[i] = events[i].DepthBelow(type);
            }

            StableSortByDepth(events, depths);
            foreach (var registration in handlers)
            {
                try
                {
                    ((YDeepEventHandler)registration.Handler)(events, tx);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ReportObserverFault(ex, type);
                }
            }
        }
    }

    /// <summary>
    /// Reports a contained handler exception through <see cref="ObserverFaulted"/> (ADR 0005). A fault in an ObserverFaulted handler is
    /// swallowed: there is nowhere left to report it.
    /// </summary>
    internal void ReportObserverFault(Exception fault, object handlerOwner)
    {
        t_observerFaults++;
        if (ObserverFaulted is not { } handlers) return;
        foreach (var handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(fault, handlerOwner);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
            }
        }
    }

    /// <summary>
    /// The shared type object of <paramref name="branch"/>, created if nobody read it yet: Yjs has an object for every type, so the changed
    /// parent types of a transaction can list it. A root only named by an update becomes its untyped placeholder.
    /// </summary>
    internal YType TypeOf(Branch branch)
    {
        if (branch.Owner is YType owner) return owner;
        if (branch.Item is null && RootNameOf(branch) is { } name) return Get(name);
        return YType.Of(branch, this);
    }

    private static YEvent? CreateEvent(YType type, Transaction tx, HashSet<string?> subs) => type.Branch.TypeRef switch
    {
        TypeRefs.Array => new YArrayEvent(type, tx, subs),
        TypeRefs.Map or TypeRefs.XmlHook => new YMapEvent(type, tx, subs),
        TypeRefs.Text or TypeRefs.XmlText => new YTextEvent(type, tx, subs),
        TypeRefs.XmlElement or TypeRefs.XmlFragment => new YXmlEvent(type, tx, subs),
        _ => null,
    };

    /// <summary>JS <c>Array.prototype.sort</c> is stable: events at the same depth keep their order (Q25).</summary>
    private static void StableSortByDepth(List<YEvent> events, int[] depths)
    {
        for (var i = 1; i < events.Count; i++)
        {
            var e = events[i];
            var d = depths[i];
            var j = i - 1;
            for (; j >= 0 && depths[j] > d; j--)
            {
                events[j + 1] = events[j];
                depths[j + 1] = depths[j];
            }

            events[j + 1] = e;
            depths[j + 1] = d;
        }
    }

    private void Raise(TransactionHandler? handlers, Transaction tx)
    {
        if (handlers is null) return;
        foreach (var handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(tx, this);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ReportObserverFault(e, this);
            }
        }
    }

    private void RaiseUpdate(UpdateHandler handlers, ReadOnlySpan<byte> update, Transaction tx)
    {
        foreach (var handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(update, tx.Origin, this, tx);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ReportObserverFault(e, this);
            }
        }
    }
}
