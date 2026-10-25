#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- base

public abstract class YType
{
    /// <summary>A new, unattached type (prelim) with a fresh branch of this type's wire kind.</summary>
    private protected YType()
    {
        var typeRef = WireTypeRef;
        Branch = new Store.Branch(Store.TypeRefs.UsesMarkers(typeRef)) { TypeRef = typeRef, Owner = this };
    }

    /// <summary>The storage of this type (Yjs <c>_start</c>, <c>_map</c>, <c>_item</c>...).</summary>
    internal Store.Branch Branch { get; }

    /// <summary>The wire type ref of this class (Yjs <c>ContentType</c> refs); <see cref="Store.TypeRefs.Untyped"/> for the root placeholder.</summary>
    internal virtual byte WireTypeRef => Store.TypeRefs.Untyped;

    /// <summary>The document this type is integrated into, set when it is (Yjs <c>_integrate</c>).</summary>
    internal Doc? AttachedDoc;

    public Doc? Doc => AttachedDoc;                    // null until integrated (prelim)
    public YType? Parent => Branch.Item?.Parent?.Owner as YType;
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

