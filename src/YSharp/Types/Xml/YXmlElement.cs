#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed class YXmlElement : YXmlFragment
{
    public YXmlElement(string nodeName = "UNDEFINED") { }
    public string NodeName => throw Sketch.NI;
    public YType? NextSibling => throw Sketch.NI;
    public YType? PrevSibling => throw Sketch.NI;
    public void SetAttribute(string name, in YValue value) => throw Sketch.NI;
    public bool TryGetAttribute(string name, out YValue value) { value = default; throw Sketch.NI; }
    public bool HasAttribute(string name) => throw Sketch.NI;
    public void RemoveAttribute(string name) => throw Sketch.NI;
    public IReadOnlyDictionary<string, YValue> GetAttributes(YSnapshot? snapshot = null) => throw Sketch.NI;
    public override string ToString() => throw Sketch.NI;                    // invariant lowercasing (X-E3). REVIEW: diverges from toLocaleLowerCase
}

