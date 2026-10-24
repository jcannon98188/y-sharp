#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- base

public abstract class YType
{
    public Doc? Doc => throw Sketch.NI;               // null until integrated (prelim)
    public YType? Parent => throw Sketch.NI;
    public bool IsIntegrated => Doc != null;

    // REVIEW (T-0.4): reads on an unintegrated type return the empty value, as JS does (no throw). Writes buffer as prelim content.
    public Subscription Observe(YEventHandler<YEvent> handler) => throw Sketch.NI;
    public Subscription ObserveDeep(YDeepEventHandler handler) => throw Sketch.NI;
    public void Unobserve(YEventHandler<YEvent> handler) => throw Sketch.NI;
    public void UnobserveDeep(YDeepEventHandler handler) => throw Sketch.NI;

    public RelativePosition CreateRelativePosition(int index, int assoc = 0) => throw Sketch.NI;
    public abstract YType Clone();
    public abstract YValue ToJson();
}

