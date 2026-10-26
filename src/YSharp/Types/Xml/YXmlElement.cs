#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace YSharp;

// Yjs YXmlElement: a fragment with a node name, which is part of its wire encoding and never changes (X-E1), and attributes on the map core
// (X-E2). Attributes set while prelim are buffered as JS buffers _prelimAttrs and set, in insertion order, after the children on integration.
public sealed class YXmlElement : YXmlFragment
{
    internal override byte WireTypeRef => Store.TypeRefs.XmlElement;

    private OrderedDictionary<string, YValue>? _prelimAttrs = new(StringComparer.Ordinal);

    public YXmlElement(string nodeName = "UNDEFINED")
    {
        ArgumentNullException.ThrowIfNull(nodeName);
        Branch.TypeName = nodeName;
    }

    public string NodeName => Branch.TypeName ?? "UNDEFINED";

    /// <summary>Yjs <c>nextSibling</c>: the next undeleted node in the parent, or null (X-E4).</summary>
    public YType? NextSibling => XmlStrings.Sibling(this, next: true);

    /// <summary>Yjs <c>prevSibling</c>: the previous undeleted node in the parent, or null (X-E4).</summary>
    public YType? PrevSibling => XmlStrings.Sibling(this, next: false);

    /// <summary>Yjs <c>setAttribute</c>: any value (X-E2).</summary>
    public void SetAttribute(string name, in YValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        YEntry entry = value;
        TypeList.Validate(new ReadOnlySpan<YEntry>(in entry), undefinedAllowed: true);
        if (AttachedDoc is not { } doc)
        {
            _prelimAttrs![name] = value;
            return;
        }

        var h = doc.EnterWrite(null, local: true);
        try
        {
            TypeMap.Set(h.Tx, this, name, entry);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>Yjs <c>getAttribute</c>: false when the attribute is absent or deleted, and always while prelim.</summary>
    public bool TryGetAttribute(string name, out YValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        value = default;
        if (AttachedDoc is not { } doc) return false;
        var h = doc.EnterRead();
        try
        {
            if (!TypeMap.TryGet(this, name, out var entry)) return false;
            value = entry.Value;
            return true;
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>Yjs <c>hasAttribute</c>.</summary>
    public bool HasAttribute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (AttachedDoc is not { } doc) return false;
        var h = doc.EnterRead();
        try
        {
            return TypeMap.Has(this, name);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>Yjs <c>removeAttribute</c>.</summary>
    public void RemoveAttribute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (AttachedDoc is not { } doc)
        {
            _prelimAttrs!.Remove(name);
            return;
        }

        var h = doc.EnterWrite(null, local: true);
        try
        {
            TypeMap.Delete(h.Tx, this, name);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>Yjs <c>getAttributes</c>: the live attributes in the order they were first stored, a snapshot (X-2). Snapshots are spec 30.</summary>
    public IReadOnlyDictionary<string, YValue> GetAttributes(YSnapshot? snapshot = null)
    {
        if (snapshot is not null) throw Sketch.NI;   // snapshots (spec 30)
        var result = new OrderedDictionary<string, YValue>(StringComparer.Ordinal);
        foreach (var (key, entry) in AttributeEntries()) result[key] = entry.Value;
        return result;
    }

    /// <summary>
    /// Yjs <c>toString</c>: <c>&lt;nodename key="value"...&gt;children&lt;/nodename&gt;</c> with the attributes sorted by key, their values converted as
    /// JS <c>String()</c> does and nothing escaped (X-E3). The node name is lowercased with the invariant culture, where JS uses the locale's.
    /// </summary>
    public override string ToString()
    {
        var attrs = AttributeEntries();
        attrs.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        var nodeName = NodeName.ToLowerInvariant();
        var sb = new StringBuilder().Append('<').Append(nodeName);
        foreach (var (key, value) in attrs) sb.Append(' ').Append(key).Append("=\"").Append(XmlStrings.Of(value)).Append('"');
        return sb.Append('>').Append(base.ToString()).Append("</").Append(nodeName).Append('>').ToString();
    }

    /// <summary>Yjs <c>clone</c>: a prelim element with the same node name and attributes, children cloned (X-E6).</summary>
    public override YType Clone()
    {
        var copy = new YXmlElement(NodeName);
        foreach (var (key, entry) in AttributeEntries()) copy._prelimAttrs![key] = entry.Value;
        CloneChildrenInto(copy);
        return copy;
    }

    /// <summary>Yjs <c>YXmlElement._integrate</c>: the children first, then the buffered attributes in insertion order.</summary>
    internal override void OnIntegrated(Transaction tx)
    {
        base.OnIntegrated(tx);
        var attrs = _prelimAttrs;
        _prelimAttrs = null;
        if (attrs is null) return;
        foreach (var (key, value) in attrs) TypeMap.Set(tx, this, key, value);
    }

    private List<KeyValuePair<string, YEntry>> AttributeEntries()
    {
        if (AttachedDoc is not { } doc) return new List<KeyValuePair<string, YEntry>>();
        var h = doc.EnterRead();
        try
        {
            return TypeMap.Entries(this);
        }
        finally
        {
            doc.Leave(h);
        }
    }
}
