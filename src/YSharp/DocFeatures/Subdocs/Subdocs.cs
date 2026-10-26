#nullable enable
using System;
using System.Collections.Generic;

namespace YSharp;

public delegate void SubdocsHandler(SubdocsChange change, Doc doc, Transaction tx);

/// <summary>Yjs <c>{ loaded, added, removed }</c> of the <c>subdocs</c> event, each in the order the transaction met the documents.</summary>
public readonly struct SubdocsChange
{
    internal SubdocsChange(IReadOnlyList<Doc> added, IReadOnlyList<Doc> removed, IReadOnlyList<Doc> loaded)
    {
        Added = added;
        Removed = removed;
        Loaded = loaded;
    }

    public IReadOnlyList<Doc> Added { get; }
    public IReadOnlyList<Doc> Removed { get; }
    public IReadOnlyList<Doc> Loaded { get; }
}

/// <summary>A transaction's Yjs <c>subdocsAdded</c>, <c>subdocsRemoved</c> and <c>subdocsLoaded</c>: insertion-ordered sets.</summary>
internal sealed class SubdocChanges
{
    public readonly OrderedDocSet Added = new(), Removed = new(), Loaded = new();

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Loaded.Count == 0;
}

internal sealed class OrderedDocSet
{
    private readonly List<Doc> _order = new();
    private readonly HashSet<Doc> _members = new(ReferenceEqualityComparer.Instance);

    public int Count => _order.Count;

    public void Add(Doc doc)
    {
        if (_members.Add(doc)) _order.Add(doc);
    }

    public bool Remove(Doc doc)
    {
        if (!_members.Remove(doc)) return false;
        _order.Remove(doc);
        return true;
    }

    public Doc[] ToArray() => _order.ToArray();
}
