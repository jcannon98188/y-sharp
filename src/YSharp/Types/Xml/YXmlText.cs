#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed class YXmlText : YText
{
    public YXmlText(string? initial = null) : base(initial) { }
    public YType? NextSibling => throw Sketch.NI;
    public YType? PrevSibling => throw Sketch.NI;
    public override YValue ToJson() => throw Sketch.NI;                      // tagged string, unlike YText (X-T3)
}

