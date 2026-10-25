#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed class YXmlText : YText
{
    internal override byte WireTypeRef => Store.TypeRefs.XmlText;

    public YXmlText(string? initial = null) : base(initial) { }
    private protected override YText CreateEmpty() => new YXmlText();
    public YType? NextSibling => throw Sketch.NI;
    public YType? PrevSibling => throw Sketch.NI;
    public override YValue ToJson() => throw Sketch.NI;                      // tagged string, unlike YText (X-T3)
}

