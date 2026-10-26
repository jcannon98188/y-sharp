#nullable enable
using System;
using System.Threading;

namespace YSharp;

/// <summary>One registration from <see cref="YType.Observe"/> or <see cref="YType.ObserveDeep"/>. Disposing removes it; a second dispose does nothing.</summary>
public readonly struct Subscription : IDisposable
{
    private readonly Registration? _registration;

    internal Subscription(Registration registration) => _registration = registration;

    public void Dispose() => _registration?.Remove();
}

/// <summary>A handler as registered: registering the same handler twice makes two registrations, which run twice (CC-13).</summary>
internal sealed class Registration
{
    internal readonly HandlerList List;
    internal readonly Delegate Handler;

    internal Registration(HandlerList list, Delegate handler)
    {
        List = list;
        Handler = handler;
    }

    internal void Remove() => List.Remove(this);
}

/// <summary>
/// The handlers of one type, in registration order. Copy-on-write and lock-free, so subscribing never takes the document lock (CC-12); a
/// dispatch iterates the array it read when it started, so a change made meanwhile applies from the next dispatch (CC-13).
/// </summary>
internal sealed class HandlerList
{
    private Registration[] _items = Array.Empty<Registration>();

    internal Registration[] Snapshot => Volatile.Read(ref _items);

    internal Registration Add(Delegate handler)
    {
        var registration = new Registration(this, handler);
        while (true)
        {
            var current = Volatile.Read(ref _items);
            var next = new Registration[current.Length + 1];
            current.CopyTo(next, 0);
            next[^1] = registration;
            if (Interlocked.CompareExchange(ref _items, next, current) == current) return registration;
        }
    }

    internal void Remove(Registration registration)
    {
        while (true)
        {
            var current = Volatile.Read(ref _items);
            var index = Array.IndexOf(current, registration);
            if (index < 0) return;
            var next = new Registration[current.Length - 1];
            Array.Copy(current, 0, next, 0, index);
            Array.Copy(current, index + 1, next, index, next.Length - index);
            if (Interlocked.CompareExchange(ref _items, next, current) == current) return;
        }
    }

    /// <summary>Yjs <c>removeEventHandlerListener</c>: every registration of <paramref name="handler"/>; none is not an error (A-E10).</summary>
    internal void RemoveAll(Delegate handler)
    {
        while (true)
        {
            var current = Volatile.Read(ref _items);
            var kept = Array.FindAll(current, r => !r.Handler.Equals(handler));
            if (kept.Length == current.Length) return;
            if (Interlocked.CompareExchange(ref _items, kept, current) == current) return;
        }
    }
}
