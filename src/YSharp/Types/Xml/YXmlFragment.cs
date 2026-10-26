#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using YSharp.Store;

namespace YSharp;

// ---------------------------------------------------------------- XML (toDOM is out of scope)

// Yjs YXmlFragment over the list core (Types/Core/TypeList.cs). Every member takes an implicit transaction unless the calling thread has one open
// (CC-8). Until it is integrated, writes go to the prelim buffer as JS splices _prelimContent, and Length is the buffer's length (X-F6); other
// reads see nothing (T-0.4). Integration inserts the buffer at 0 in the integrating transaction (T-0.6). Unlike YArray, Yjs keeps no search
// markers for the XML list types, and Push inserts at Length (after the last undeleted child), as JS push does.
public class YXmlFragment : YType
{
    internal override byte WireTypeRef => Store.TypeRefs.XmlFragment;

    private List<YEntry>? _prelim = new();

    /// <summary>Yjs <c>length</c>: the buffered children while prelim (X-F6).</summary>
    public int Length
    {
        get
        {
            if (AttachedDoc is not { } doc) return _prelim?.Count ?? 0;
            var h = doc.EnterRead();
            try
            {
                return Branch.Length;
            }
            finally
            {
                doc.Leave(h);
            }
        }
    }

    /// <summary>Yjs <c>firstChild</c>: the first undeleted child, or null (X-F4).</summary>
    public YType? FirstChild
    {
        get
        {
            if (AttachedDoc is not { } doc) return null;
            var h = doc.EnterRead();
            try
            {
                var n = StructStore.StartOf(Branch);
                while (!n.IsNull && n.IsDeleted) n = n.Right;
                return !n.IsNull && n.Kind == ContentKind.Type ? YType.Of((Branch)doc.Store.GetPayload(n), doc) : null;
            }
            finally
            {
                doc.Leave(h);
            }
        }
    }

    public YType this[int index]   // ArgumentOutOfRangeException (REVIEW: JS returns undefined)
    {
        get
        {
            if (AttachedDoc is not { } doc) throw new ArgumentOutOfRangeException(nameof(index));
            var h = doc.EnterRead();
            try
            {
                return Node(TypeList.Get(this, index));
            }
            finally
            {
                doc.Leave(h);
            }
        }
    }

    public void Insert(int index, ReadOnlySpan<YType> content)   // index > Length throws, as JS ("Length exceeded!")
    {
        var entries = Entries(content);
        if (AttachedDoc is not { } doc)
        {
            if (index < 0 || index > _prelim!.Count) throw new ArgumentOutOfRangeException(nameof(index), "Length exceeded!");
            _prelim.InsertRange(index, entries);
            return;
        }

        var h = doc.EnterWrite(null, local: true);
        try
        {
            TypeList.Insert(h.Tx, this, index, entries);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    public void InsertSubdoc(int index, Doc subdoc) => throw Sketch.NI;      // subdocuments (spec 33); read it back through Doc.Subdocs

    /// <summary>
    /// Yjs <c>insertAfter</c>: inserts right after <paramref name="reference"/>, or at the start when it is null (X-F3). While prelim, a reference
    /// that is not in the buffer throws "Reference item not found"; once integrated, it must be a child of this node (JS does not check).
    /// </summary>
    public void InsertAfter(YType? reference, ReadOnlySpan<YType> content)
    {
        var entries = Entries(content);
        if (AttachedDoc is not { } doc)
        {
            var index = reference is null ? 0 : _prelim!.FindIndex(e => ReferenceEquals(e.Type, reference)) + 1;
            if (index == 0 && reference is not null) throw new ArgumentException("Reference item not found", nameof(reference));
            _prelim!.InsertRange(index, entries);
            return;
        }

        var h = doc.EnterWrite(null, local: true);
        try
        {
            var after = ItemRef.Null;
            if (reference is not null)
            {
                if (reference.Branch.Item is not { } item || !ReferenceEquals(item.Parent, Branch))
                    throw new ArgumentException("Reference item not found", nameof(reference));
                after = new ItemRef(item);
            }

            TypeList.InsertAfterItem(h.Tx, this, after, entries);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>Yjs <c>push</c>: <c>insert(length, content)</c>, so the new children go after the last undeleted one.</summary>
    public void Push(ReadOnlySpan<YType> content)
    {
        var entries = Entries(content);
        if (AttachedDoc is not { } doc)
        {
            _prelim!.AddRange(entries);
            return;
        }

        var h = doc.EnterWrite(null, local: true);
        try
        {
            TypeList.Insert(h.Tx, this, Branch.Length, entries);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    public void Unshift(ReadOnlySpan<YType> content) => Insert(0, content);

    public void Delete(int index, int length = 1)   // partial effect then throws, as JS (A-5)
    {
        if (AttachedDoc is not { } doc)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            // JS splices the prelim buffer, which never throws: a range past the end removes what exists
            if (index < _prelim!.Count) _prelim.RemoveRange(index, Math.Min(length, _prelim.Count - index));
            return;
        }

        var h = doc.EnterWrite(null, local: true);
        try
        {
            TypeList.Delete(h.Tx, this, index, length);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>
    /// Yjs <c>createTreeWalker</c>: the undeleted descendants in document order (pre-order), descending only into elements and fragments (X-F7).
    /// The tree is read when this is called; <paramref name="filter"/> runs as the result is enumerated.
    /// </summary>
    public IEnumerable<YType> TreeWalk(Func<YType, bool>? filter = null)
    {
        var nodes = Descendants();
        return filter is null ? nodes : nodes.Where(filter);
    }

    /// <summary>Yjs <c>querySelector</c>: the first element whose node name matches, ignoring case; the query is a tag name only (X-F7).</summary>
    public YXmlElement? QuerySelector(string nodeName) => QuerySelectorAll(nodeName, first: true).FirstOrDefault();

    /// <summary>Yjs <c>querySelectorAll</c>: every element whose node name matches, ignoring case, in document order (X-F7).</summary>
    public IReadOnlyList<YXmlElement> QuerySelectorAll(string nodeName) => QuerySelectorAll(nodeName, first: false);

    /// <summary>Yjs <c>toString</c>: the children's <c>toString</c>, concatenated (X-F5).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var child in Children()) sb.Append(XmlStrings.Of(child));
        return sb.ToString();
    }

    /// <summary>Yjs <c>clone</c>: a prelim fragment with the children cloned.</summary>
    public override YType Clone()
    {
        var copy = new YXmlFragment();
        CloneChildrenInto(copy);
        return copy;
    }

    /// <summary>Yjs <c>toJSON</c>: the XML string, not a JSON structure (X-F5).</summary>
    public override YValue ToJson() => ToString();

    /// <summary>Yjs <c>YXmlFragment._integrate</c>: the buffered children go in at 0, inside the integrating transaction.</summary>
    internal override void OnIntegrated(Transaction tx)
    {
        var prelim = _prelim;
        _prelim = null;
        if (prelim is { Count: > 0 }) TypeList.Insert(tx, this, 0, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(prelim));
    }

    /// <summary>Clones this node's children into the prelim buffer of <paramref name="copy"/>, nested types cloned (Yjs <c>clone</c>).</summary>
    private protected void CloneChildrenInto(YXmlFragment copy)
    {
        foreach (var child in Children()) copy._prelim!.Add(child.Kind == YEntryKind.Type ? child.Type!.Clone() : child);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The live children (Yjs <c>toArray</c>), or none while prelim.</summary>
    private YEntry[] Children()
    {
        if (AttachedDoc is not { } doc) return Array.Empty<YEntry>();
        var h = doc.EnterRead();
        try
        {
            return TypeList.ToEntries(this).ToArray();
        }
        finally
        {
            doc.Leave(h);
        }
    }

    private List<YXmlElement> QuerySelectorAll(string nodeName, bool first)
    {
        ArgumentNullException.ThrowIfNull(nodeName);
        var query = nodeName.ToUpperInvariant();
        var result = new List<YXmlElement>();
        // JS matches `element.nodeName && ...`, so an element with an empty node name never matches
        foreach (var node in Descendants())
        {
            if (node is not YXmlElement { NodeName: { Length: > 0 } name } element || name.ToUpperInvariant() != query) continue;
            result.Add(element);
            if (first) break;
        }

        return result;
    }

    /// <summary>The walk of Yjs <c>YXmlTreeWalker</c>, without the filter: undeleted type children, pre-order, into elements and fragments only.</summary>
    private List<YType> Descendants()
    {
        var result = new List<YType>();
        if (AttachedDoc is not { } doc) return result;
        var h = doc.EnterRead();
        try
        {
            // an explicit stack of the positions to resume at, so a deep tree cannot overflow the call stack
            var resume = new Stack<ItemRef>();
            var n = StructStore.StartOf(Branch);
            while (true)
            {
                if (n.IsNull)
                {
                    if (resume.Count == 0) break;
                    n = resume.Pop();
                    continue;
                }

                var current = n;
                n = n.Right;
                if (current.IsDeleted || current.Kind != ContentKind.Type) continue;
                var branch = (Branch)doc.Store.GetPayload(current);
                result.Add(YType.Of(branch, doc));
                if (branch.TypeRef is not (TypeRefs.XmlElement or TypeRefs.XmlFragment)) continue;
                resume.Push(n);
                n = StructStore.StartOf(branch);
            }
        }
        finally
        {
            doc.Leave(h);
        }

        return result;
    }

    private static YEntry[] Entries(ReadOnlySpan<YType> content)
    {
        var entries = new YEntry[content.Length];
        for (var i = 0; i < content.Length; i++) entries[i] = content[i] ?? throw new ArgumentNullException(nameof(content));
        TypeList.Validate(entries, undefinedAllowed: false);
        return entries;
    }

    /// <summary>A child as a node: XML children are types; other content (which only a remote peer can write) cannot be returned as one.</summary>
    private static YType Node(in YEntry entry) =>
        entry.Type ?? throw new InvalidOperationException("The child at this position is not a shared type.");
}
