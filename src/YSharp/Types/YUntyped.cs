#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed class YUntyped : YType   // placeholder from Doc.Get(name) before a concrete type is requested
{
    public override YType Clone() => throw Sketch.NI;
    public override YValue ToJson() => throw Sketch.NI;
}

