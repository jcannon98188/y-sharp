#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- base

public abstract partial class YType
{
    /// <summary>A new, unattached type (prelim) with a fresh branch of this type's wire kind.</summary>
    private protected YType()
    {
        var typeRef = WireTypeRef;
        Branch = new Store.Branch(Store.TypeRefs.UsesMarkers(typeRef)) { TypeRef = typeRef, Owner = this };
    }

    /// <summary>The storage of this type (Yjs <c>_start</c>, <c>_map</c>, <c>_item</c>...).</summary>
    internal Store.Branch Branch { get; private set; }

    /// <summary>
    /// Makes this new type the owner of root <paramref name="root"/> of <paramref name="doc"/>, keeping its items (the placeholder upgrade: Yjs
    /// copies <c>_start</c>, <c>_map</c> and <c>_length</c> into the new instance; y-sharp keeps the branch, so no item is re-parented).
    /// </summary>
    internal void AdoptRoot(Store.Branch root, Doc doc)
    {
        root.Upgrade(Branch.TypeRef, Branch.TypeName);
        root.Owner = this;
        Branch = root;
        AttachedDoc = doc;
    }

    /// <summary>
    /// The shared type that owns <paramref name="branch"/>: a nested type read from an update gets its object on first read (Yjs creates it when
    /// decoding <c>ContentType</c>; y-sharp keeps the wire branch and wraps it lazily).
    /// </summary>
    internal static YType Of(Store.Branch branch, Doc doc)
    {
        if (branch.Owner is YType owner) return owner;
        YType created = branch.TypeRef switch
        {
            Store.TypeRefs.Array => new YArray(),
            Store.TypeRefs.Map => new YMap(),
            Store.TypeRefs.Text => new YText(),
            Store.TypeRefs.XmlElement => new YXmlElement(branch.TypeName ?? "UNDEFINED"),
            Store.TypeRefs.XmlFragment => new YXmlFragment(),
            Store.TypeRefs.XmlHook => new YXmlHook(branch.TypeName ?? ""),
            Store.TypeRefs.XmlText => new YXmlText(),
            _ => new YUntyped(),
        };
        created.Branch = branch;
        created.AttachedDoc = branch.Item is null && doc.RootNameOf(branch) is null ? null : doc;
        branch.Owner = created;
        return created;
    }

    /// <summary>
    /// Yjs <c>_integrate</c>: called inside the transaction that integrates this type's item, after it is linked. A type that buffered writes
    /// while prelim replays them here (T-0.6), so they are part of the same change, and nested prelim types follow recursively (T-0.8).
    /// </summary>
    internal virtual void OnIntegrated(Transaction tx) { }

    /// <summary>The wire type ref of this class (Yjs <c>ContentType</c> refs); <see cref="Store.TypeRefs.Untyped"/> for the root placeholder.</summary>
    internal virtual byte WireTypeRef => Store.TypeRefs.Untyped;

    /// <summary>The document this type is integrated into, set when it is (Yjs <c>_integrate</c>).</summary>
    internal Doc? AttachedDoc;

    public Doc? Doc => AttachedDoc;                    // null until integrated (prelim)
    public YType? Parent => Branch.Item?.Parent?.Owner as YType;
    public bool IsIntegrated => Doc != null;

    // REVIEW (T-0.4): reads on an unintegrated type return the empty value, as JS does (no throw). Writes buffer as prelim content.
    public RelativePosition CreateRelativePosition(int index, int assoc = 0) => throw Sketch.NI;
    public abstract YType Clone();
    public abstract YValue ToJson();
}

