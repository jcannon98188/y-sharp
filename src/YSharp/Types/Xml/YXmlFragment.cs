#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- XML (toDOM is out of scope)

public class YXmlFragment : YType
{
    internal override byte WireTypeRef => Store.TypeRefs.XmlFragment;

    public int Length => throw Sketch.NI;                                    // prelim length before integration (X-F6)
    public YType? FirstChild => throw Sketch.NI;
    public YType this[int index] => throw Sketch.NI;
    public void Insert(int index, ReadOnlySpan<YType> content) => throw Sketch.NI;
    public void InsertSubdoc(int index, Doc subdoc) => throw Sketch.NI;      // Yjs lets a fragment hold a subdoc; read it back through Doc.Subdocs (indexer throws at that position)
    public void InsertAfter(YType? reference, ReadOnlySpan<YType> content) => throw Sketch.NI;
    public void Push(ReadOnlySpan<YType> content) => throw Sketch.NI;
    public void Unshift(ReadOnlySpan<YType> content) => throw Sketch.NI;
    public void Delete(int index, int length = 1) => throw Sketch.NI;
    public IEnumerable<YType> TreeWalk(Func<YType, bool>? filter = null) => throw Sketch.NI;
    public YXmlElement? QuerySelector(string nodeName) => throw Sketch.NI;
    public IReadOnlyList<YXmlElement> QuerySelectorAll(string nodeName) => throw Sketch.NI;
    public override string ToString() => throw Sketch.NI;
    public override YType Clone() => throw Sketch.NI;
    public override YValue ToJson() => throw Sketch.NI;                      // the XML string (X-F5)
}

