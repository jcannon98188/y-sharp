#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed partial class Doc
{
    /// Test-only fault hook (ADR 0005, ERR-20), reached through InternalsVisibleTo: violates a struct-store invariant (for example breaks the clock
    /// continuity of the local client's struct list) without any public API call. The next operation that touches the store detects it, throws
    /// YInternalException and marks the document faulted. No public input can cause this by design (ERR-21).
    internal void InjectInvariantViolation() => throw Sketch.NI;

}
