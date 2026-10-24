#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed class YArrayEvent : YEvent { public ReadOnlySpan<ListDeltaOp> Delta => throw Sketch.NI; }
public sealed class YMapEvent : YEvent { public IReadOnlyCollection<string> KeysChanged => throw Sketch.NI; }
public sealed class YTextEvent : YEvent { public ReadOnlySpan<TextDeltaOp> Delta => throw Sketch.NI; public bool ChildListChanged => throw Sketch.NI; public IReadOnlyCollection<string> KeysChanged => throw Sketch.NI; }
public sealed class YXmlEvent : YEvent { public ReadOnlySpan<ListDeltaOp> Delta => throw Sketch.NI; public bool ChildListChanged => throw Sketch.NI; public IReadOnlyCollection<string> AttributesChanged => throw Sketch.NI; }
// YXmlText raises YTextEvent and YXmlHook raises YMapEvent (T-0.1).

