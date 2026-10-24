#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public readonly struct ApplyResult
{
    public YError Error { get; }
    public bool Ok => Error == YError.None;
    public int RejectedStructs { get; }
    public int ObserverFaults { get; }
}

