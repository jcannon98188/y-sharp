#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public readonly struct ApplyResult
{
    internal ApplyResult(YError error, int rejectedStructs, int observerFaults)
    {
        Error = error;
        RejectedStructs = rejectedStructs;
        ObserverFaults = observerFaults;
    }

    public YError Error { get; }
    public bool Ok => Error == YError.None;
    public int RejectedStructs { get; }
    public int ObserverFaults { get; }
}

