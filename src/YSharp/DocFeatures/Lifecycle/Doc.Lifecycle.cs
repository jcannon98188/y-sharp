#nullable enable
using System;
using System.Threading;
using YSharp.Store;

namespace YSharp;

// Doc options, load, sync and destroy (spec 29, DOC-01, DOC-12..DOC-15, DOC-17; Yjs utils/Doc.js).
public sealed partial class Doc
{
    private string? _guid;
    private volatile bool _loadRequested;
    private volatile bool _isLoaded, _isSynced, _isDestroyed;

    /// <summary>The document's id: the option, else a random uuid v4 as in Yjs (<c>random.uuidv4()</c>, DOC-01).</summary>
    public string Guid
    {
        get
        {
            var guid = Volatile.Read(ref _guid);
            if (guid is not null) return guid;
            Interlocked.CompareExchange(ref _guid, _options.Guid ?? System.Guid.NewGuid().ToString("D"), null);
            return _guid!;
        }
    }

    public bool IsLoaded => _isLoaded;
    public bool IsSynced => _isSynced;
    public bool IsDestroyed => _isDestroyed;

    // Spec 8: members JS exposes that the public sketch hides.
    internal bool ShouldLoad => _loadRequested || _options.ShouldLoad;
    internal bool AutoLoad => _options.AutoLoad;
    /// <summary>Yjs <c>collectionid</c>: the option, or the parent's when the document joins one without its own (DOC-17).</summary>
    internal string? CollectionId
    {
        get => Volatile.Read(ref _collectionId) ?? _options.CollectionId;
        set => Volatile.Write(ref _collectionId, value);
    }

    private string? _collectionId;
    internal YValue Meta => _options.Meta;

    /// <summary>
    /// Manual garbage collection of the whole store (JS <c>Y.tryGc(createDeleteSetFromStructStore(store), store, gcFilter)</c>): every deleted
    /// item that is not kept and passes the filter gives up its content, whatever <see cref="Gc"/> says, and deleted structs merge. Like Yjs it
    /// opens no transaction, so no event fires; it holds the write lock while it runs.
    /// </summary>
    internal void CollectGarbage()
    {
        _lock.EnterWrite(Timeout.Infinite, default);
        try
        {
            ThrowIfFaulted();
            var ds = new DeleteRanges();
            AddStoreDeletes(ds);
            TryGcDeleteSet(ds);
            TryMergeDeleteSet(ds);
            MergeSplitStructs();
        }
        finally
        {
            _lock.ExitWrite();
        }
    }

    /// <summary>JS <c>emit('load', [doc])</c>: the provider hook that marks the document loaded (DOC-13).</summary>
    internal void SignalLoaded()
    {
        _isLoaded = true;
        RaiseDocEvent(Loaded);
    }

    /// <summary>JS <c>emit('sync', [isSynced, doc])</c>: sets <see cref="IsSynced"/>, loads a synced document that is not loaded yet, then raises <see cref="Synced"/> (DOC-14).</summary>
    internal void SignalSynced(bool isSynced)
    {
        _isSynced = isSynced;
        if (isSynced && !_isLoaded) SignalLoaded();
        RaiseDocEvent(Synced);
    }

    public event Action<Doc>? Loaded, Synced, Destroying, Destroyed;

    /// <summary>
    /// Yjs <c>load()</c>: marks the document to be loaded. A root document only sets the flag; a subdocument that was not loaded is reported
    /// in its parent's <see cref="SubdocsChanged"/> (spec 33). Idempotent (DOC-12).
    /// </summary>
    public void Load()
    {
        if (!ShouldLoad) OnLoadSubdoc();
        _loadRequested = true;
    }

    /// <summary>
    /// Yjs <c>destroy()</c> (CC-17): marks the document destroyed, destroys its subdocuments first, hands a subdocument's item a fresh unloaded
    /// document (reported in the parent's <see cref="SubdocsChanged"/>), raises <see cref="Destroying"/> then <see cref="Destroyed"/> (the two
    /// Yjs emits, <c>destroyed</c> and <c>destroy</c>, in that order), and then drops every handler of the document's events, as lib0's
    /// <c>ObservableV2.destroy</c> does. Type observers stay, and the document keeps accepting transactions (DOC-15). Each step waits its
    /// turn for the write lock; the subdocuments and the parent are reached with this document's lock released (CC-18).
    /// </summary>
    public void Destroy()
    {
        Doc[] subdocs;
        Item? item;
        Doc? parent;
        _lock.EnterWrite(Timeout.Infinite, default);
        try
        {
            _isDestroyed = true;
            subdocs = _subdocs.ToArray();
            item = Interlocked.Exchange(ref _subdocItem, null);
            parent = Volatile.Read(ref _parentDoc);
        }
        finally
        {
            _lock.ExitWrite();
        }

        foreach (var sub in subdocs) sub.Destroy();
        if (item is not null && parent is not null) DetachFromParent(item, parent);
        _lock.EnterWrite(Timeout.Infinite, default);
        try
        {
            RaiseDocEvent(Destroying);
            RaiseDocEvent(Destroyed);
            Loaded = Synced = Destroying = Destroyed = null;
            BeforeAllTransactions = BeforeTransaction = BeforeObserverCalls = AfterTransaction = AfterTransactionCleanup = null;
            AfterAllTransactions = null;
            Update = UpdateV2 = null;
            SubdocsChanged = null;
        }
        finally
        {
            _lock.ExitWrite();
        }
    }

    public void Dispose() => Destroy();

    /// <summary>A subdocument that was not loaded joins its parent's loaded subdocuments (Doc.Subdocs.cs).</summary>
    partial void OnLoadSubdoc();

    private void RaiseDocEvent(Action<Doc>? handlers)
    {
        if (handlers is null) return;
        foreach (var handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(this);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ReportObserverFault(e, this);
            }
        }
    }
}
