#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed class UndoManagerOptions
{
    public Doc? Doc { get; init; }                               // needed when the initial scope is empty (A8)
    public TimeSpan CaptureTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    public Func<Transaction, bool>? CaptureTransaction { get; init; }
    public Func<ItemView, bool>? DeleteFilter { get; init; }
    public ISet<object?>? TrackedOrigins { get; init; }          // default { null }; UndoManager adds itself
    public bool IgnoreRemoteMapChanges { get; init; }
    public TimeProvider? TimeProvider { get; init; }             // replaces Date.now
}

