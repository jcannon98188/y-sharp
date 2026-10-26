#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using YSharp.Store;

namespace YSharp;

// Subdocuments (spec 33, SUB-01..SUB-14, DOC-12, DOC-15..DOC-17; Yjs structs/ContentDoc.js, the subdocs part of Transaction.js and Doc.js).
// Lock order: a parent's lock before its subdocuments' (CC-18). A subdocument never holds its own lock while it opens a transaction on its
// parent: Load and Destroy reach the parent with the subdocument's lock released.
public sealed partial class Doc
{
    private readonly SubdocSet _subdocs = new();
    private Item? _subdocItem;   // Yjs _item: the item whose content is this document; written under the parent's lock
    private Doc? _parentDoc;     // the document of that item

    /// <summary>Yjs <c>parentDoc</c>: the document this one is a subdocument of; null for a root or a destroyed subdocument.</summary>
    public Doc? ParentDoc => Volatile.Read(ref _subdocItem) is null ? null : Volatile.Read(ref _parentDoc);

    /// <summary>Yjs <c>doc.subdocs</c>: the live set of subdocuments, in the order they were added (DOC-16).</summary>
    public IReadOnlyCollection<Doc> Subdocs => _subdocs;

    /// <summary>Yjs <c>getSubdocGuids()</c>: a new set of the subdocuments' guids; duplicates collapse (DOC-16).</summary>
    public IReadOnlyCollection<string> GetSubdocGuids()
    {
        var guids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sub in _subdocs) guids.Add(sub.Guid);
        return guids;
    }

    /// <summary>
    /// Yjs <c>subdocs</c> event: raised in the cleanup of a transaction that added, removed or loaded subdocuments, after the update events
    /// (SUB-06).
    /// </summary>
    public event SubdocsHandler? SubdocsChanged;

    /// <summary>Yjs <c>ContentDoc.integrate</c> (SUB-04): the document of a decoded or copied content is made here.</summary>
    partial void HookSubdocIntegrated(Transaction tx, ItemRef item)
    {
        var content = (SubdocContent)Store.GetPayload(item);
        var sub = content.Doc ??= content.CreateDoc(this);
        Volatile.Write(ref sub._parentDoc, this);
        Volatile.Write(ref sub._subdocItem, item.Item);
        var changes = tx.State.SubdocChanges;
        changes.Added.Add(sub);
        if (sub.ShouldLoad) changes.Loaded.Add(sub);
    }

    /// <summary>Yjs <c>ContentDoc.delete</c> (SUB-08): added and deleted in one transaction cancel out, otherwise the document is removed.</summary>
    partial void HookSubdocDeleted(Transaction tx, ItemRef item)
    {
        if (((SubdocContent)Store.GetPayload(item)).Doc is not { } sub) return;
        var changes = tx.State.SubdocChanges;
        if (!changes.Added.Remove(sub)) changes.Removed.Add(sub);
    }

    /// <summary>
    /// The subdocs part of Yjs <c>cleanupTransactions</c> (SUB-06): added documents take this document's client id and, when they have none,
    /// its collection id; then the set changes, the event fires, and every removed document is destroyed.
    /// </summary>
    partial void HookSubdocs(Transaction tx)
    {
        if (tx.State.SubdocChangesOrNull is not { IsEmpty: false } changes) return;
        var added = changes.Added.ToArray();
        var removed = changes.Removed.ToArray();
        var loaded = changes.Loaded.ToArray();
        foreach (var sub in added)
        {
            sub.ClientId = ClientId;
            if (sub.CollectionId is null) sub.CollectionId = CollectionId;
            _subdocs.Add(sub);
        }

        foreach (var sub in removed) _subdocs.Remove(sub);
        var handlers = SubdocsChanged;
        if (handlers is not null)
        {
            var change = new SubdocsChange(added, removed, loaded);
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                try
                {
                    handler(change, this, tx);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    ReportObserverFault(e, this);
                }
            }
        }

        foreach (var sub in removed) sub.Destroy();
    }

    /// <summary>Yjs <c>load()</c> on a subdocument that was not loaded: reported as loaded in a transaction on the parent (DOC-12).</summary>
    partial void OnLoadSubdoc()
    {
        if (Volatile.Read(ref _subdocItem) is null || Volatile.Read(ref _parentDoc) is not { } parent) return;
        var h = parent.EnterWrite(null, local: true);
        try
        {
            h.State.SubdocChanges.Loaded.Add(this);
        }
        finally
        {
            parent.Leave(h);
        }
    }

    /// <summary>
    /// The subdocument part of Yjs <c>destroy()</c> (DOC-15, SUB-11, SUB-12), run with this document's lock released: the item gets a fresh,
    /// unloaded document with the same guid and options, reported as added unless the item was deleted, and this one is reported removed.
    /// </summary>
    private void DetachFromParent(Item item, Doc parent)
    {
        var h = parent.EnterWrite(null, local: true);
        try
        {
            var changes = h.State.SubdocChanges;
            var at = new ItemRef(item);
            if (at.Kind == ContentKind.Doc)   // a collected item holds ContentDeleted: JS sets the new doc on that and nothing reads it
            {
                var content = (SubdocContent)parent.Store.GetPayload(at);
                var doc = content.Doc = content.CreateUnloadedDoc(parent, Guid);
                Volatile.Write(ref doc._parentDoc, parent);
                Volatile.Write(ref doc._subdocItem, item);
                if (!item.Deleted) changes.Added.Add(doc);
            }

            changes.Removed.Add(this);
        }
        finally
        {
            parent.Leave(h);
        }
    }

    /// <summary>An insertion-ordered set of documents, readable from any thread (Yjs uses a JS Set).</summary>
    private sealed class SubdocSet : IReadOnlyCollection<Doc>
    {
        private readonly List<Doc> _order = new();
        private readonly HashSet<Doc> _members = new(ReferenceEqualityComparer.Instance);

        public int Count
        {
            get
            {
                lock (_order) return _order.Count;
            }
        }

        public void Add(Doc doc)
        {
            lock (_order)
                if (_members.Add(doc)) _order.Add(doc);
        }

        public void Remove(Doc doc)
        {
            lock (_order)
                if (_members.Remove(doc)) _order.Remove(doc);
        }

        public Doc[] ToArray()
        {
            lock (_order) return _order.ToArray();
        }

        public IEnumerator<Doc> GetEnumerator() => ((IEnumerable<Doc>)ToArray()).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
