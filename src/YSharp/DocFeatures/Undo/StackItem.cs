#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- undo

public sealed class StackItem
{
    public DeleteSet Insertions => throw Sketch.NI;
    public DeleteSet Deletions => throw Sketch.NI;
    public Dictionary<object, object?> Meta { get; } = new();   // caller-owned, not protected (CC-20)
}

