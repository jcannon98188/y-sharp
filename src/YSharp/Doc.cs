#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed partial class Doc : IDisposable
{
    public Doc(DocOptions? options = null) { }
    public ulong ClientId { get; set; }
    public bool Gc { get; set; }
    public YLimits Limits => throw Sketch.NI;
    public bool IsFaulted => throw Sketch.NI;   // ADR 0005: readable, refuses changes. Mutators and ApplyUpdate throw YInternalException, TryApplyUpdate returns Rejected

}
