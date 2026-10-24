#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public readonly record struct YPathSegment(string? Key, int Index);   // Key for map parents, Index for list parents (V-2)

public abstract class YEvent
{
    public YType Target => throw Sketch.NI;
    public YType CurrentTarget => throw Sketch.NI;       // changes per deep handler call (V-3)
    public Transaction Transaction => throw Sketch.NI;
    public bool IsValid => throw Sketch.NI;
    public IReadOnlyList<YPathSegment> GetPath() => throw Sketch.NI;    // lazy, allocates
    public bool Adds(ItemId id) => throw Sketch.NI;
    public bool Deletes(ItemId id) => throw Sketch.NI;
    public IReadOnlyDictionary<string, KeyChange> Keys => throw Sketch.NI;   // lazy
}

