#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed partial class Doc
{
    // ---- root types. Get(name) returns an untyped placeholder that a later typed Get upgrades.
    public YArray GetArray(string name = "") => throw Sketch.NI;
    public YMap GetMap(string name = "") => throw Sketch.NI;
    public YText GetText(string name = "") => throw Sketch.NI;
    public YXmlFragment GetXmlFragment(string name = "") => throw Sketch.NI;
    public YXmlText GetXmlText(string name = "") => throw Sketch.NI;
    public YXmlElement GetXmlElement(string name = "") => throw Sketch.NI;   // nodeName "UNDEFINED" (X-E8)
    public YType Get(string name) => throw Sketch.NI;
    public IReadOnlyDictionary<string, YType> Share => throw Sketch.NI;       // snapshot
    public YValue ToJson() => throw Sketch.NI;                                // deprecated in JS, kept for DOC-07

}
