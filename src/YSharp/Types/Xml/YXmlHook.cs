#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed class YXmlHook : YMap
{
    internal override byte WireTypeRef => Store.TypeRefs.XmlHook;

    public YXmlHook(string hookName) { Branch.TypeName = hookName; }
    public string HookName => throw Sketch.NI;
}

