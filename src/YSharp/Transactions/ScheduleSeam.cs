#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

/// Lock transition reporting for the schedule explorer (ADR 0006, 0015). A static with a null check on the hot path: both delegates are null in production.
/// Point: called at every lock, observer-list and lock-order transition with a stable id such as "lock.write.acquire". Block: called instead of a raw
/// Monitor.Wait or spin when the caller cannot proceed; the predicate turns true when it can. Both are invoked with no internal Monitor held.
internal static class ScheduleSeam
{
    internal static Action<string>? Point;
    internal static Action<string, Func<bool>>? Block;
}

