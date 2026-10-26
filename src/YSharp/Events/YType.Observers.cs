#nullable enable
using System;
using System.Threading;

namespace YSharp;

// Yjs AbstractType._eH and _dEH: the observers of a type and its deep observers. Registering never takes the document lock (CC-12).
public abstract partial class YType
{
    private HandlerList? _handlers;
    private HandlerList? _deepHandlers;

    /// <summary>Yjs <c>observe</c>: called with this type's event after each transaction that changed it.</summary>
    public Subscription Observe(YEventHandler<YEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new Subscription(ListOf(ref _handlers).Add(handler));
    }

    /// <summary>Yjs <c>observeDeep</c>: called with the events of this type and its descendants, top-level events first.</summary>
    public Subscription ObserveDeep(YDeepEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new Subscription(ListOf(ref _deepHandlers).Add(handler));
    }

    /// <summary>Yjs <c>unobserve</c>: removes every registration of <paramref name="handler"/>.</summary>
    public void Unobserve(YEventHandler<YEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Volatile.Read(ref _handlers)?.RemoveAll(handler);
    }

    /// <summary>Yjs <c>unobserveDeep</c>: removes every registration of <paramref name="handler"/>.</summary>
    public void UnobserveDeep(YDeepEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Volatile.Read(ref _deepHandlers)?.RemoveAll(handler);
    }

    internal Registration[] Handlers => Volatile.Read(ref _handlers)?.Snapshot ?? Array.Empty<Registration>();

    internal Registration[] DeepHandlers => Volatile.Read(ref _deepHandlers)?.Snapshot ?? Array.Empty<Registration>();

    private static HandlerList ListOf(ref HandlerList? field)
    {
        if (Volatile.Read(ref field) is { } list) return list;
        Interlocked.CompareExchange(ref field, new HandlerList(), null);
        return field!;
    }
}
