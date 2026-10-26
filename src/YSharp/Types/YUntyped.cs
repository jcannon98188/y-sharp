#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed class YUntyped : YType   // placeholder from Doc.Get(name) before a concrete type is requested
{
    // Yjs AbstractType.clone() is unimplemented: there is nothing to copy the content into.
    public override YType Clone() => throw new NotSupportedException("An untyped placeholder cannot be cloned; request its type first.");

    // Yjs AbstractType.toJSON() returns undefined.
    public override YValue ToJson() => default;
}

