#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using YSharp.Store;

namespace YSharp;

/// <summary>
/// Yjs <c>UndoManager</c> (spec 32, UM-01..UM-20; Yjs utils/UndoManager.js and <c>redoItem</c> in structs/Item.js). It captures the tracked
/// transactions that change its scope into stack items, merging changes closer than the capture timeout, and undoes or redoes a stack item in
/// a write transaction whose origin is the manager. Every member runs under the document lock (CC-20); <see cref="Undo"/> and
/// <see cref="Redo"/> are write transactions, the rest read transactions. Events are raised on the calling thread, and a handler that throws
/// is reported through <see cref="Doc.ObserverFaulted"/> (ADR 0005).
/// </summary>
public sealed class UndoManager : IDisposable
{
    private readonly Doc _doc;
    private readonly List<object> _scope = new();          // YType or the Doc itself
    private readonly ISet<object?> _trackedOrigins;
    private readonly Func<Transaction, bool> _captureTransaction;
    private readonly Func<ItemView, bool> _deleteFilter;
    private readonly bool _ignoreRemoteMapChanges;
    private readonly double _captureTimeoutMs;
    private readonly TimeProvider _time;
    private readonly object _gate = new();                  // orders read-transaction mutations of the scope and origins
    private List<StackItem> _undoStack = new();
    private List<StackItem> _redoStack = new();
    private bool _undoing, _redoing;
    private int _poppingThread;                            // the thread inside Undo or Redo; another thread's transactions are captured normally
    private StackItem? _currStackItem;
    private IReadOnlyCollection<YType> _poppedChanged = Array.Empty<YType>();
    private long _lastChange;

    public UndoManager(YType scope, UndoManagerOptions? options = null)
        : this(new object[] { scope ?? throw new ArgumentNullException(nameof(scope)) }, options, options?.Doc ?? scope.Doc)
    {
    }

    public UndoManager(IReadOnlyList<YType> scope, UndoManagerOptions? options = null)
        : this(scope?.Cast<object>().ToArray() ?? throw new ArgumentNullException(nameof(scope)), options,
            options?.Doc ?? (scope.Count > 0 ? scope[0].Doc : null))
    {
    }

    public UndoManager(Doc scope, UndoManagerOptions? options = null)
        : this(new object[] { scope ?? throw new ArgumentNullException(nameof(scope)) }, options, options?.Doc ?? scope)
    {
    }

    private UndoManager(object[] scope, UndoManagerOptions? options, Doc? doc)
    {
        _doc = doc ?? throw new ArgumentException("The undo manager needs a document: pass an integrated type, or UndoManagerOptions.Doc for an empty scope.", nameof(scope));
        foreach (var s in scope) AddScope(s);
        _deleteFilter = options?.DeleteFilter ?? (static _ => true);
        _trackedOrigins = options?.TrackedOrigins ?? new HashSet<object?> { null };
        _trackedOrigins.Add(this);
        _captureTransaction = options?.CaptureTransaction ?? (static _ => true);
        _ignoreRemoteMapChanges = options?.IgnoreRemoteMapChanges ?? false;
        _captureTimeoutMs = (options?.CaptureTimeout ?? TimeSpan.FromMilliseconds(500)).TotalMilliseconds;
        _time = options?.TimeProvider ?? TimeProvider.System;
        _doc.AfterTransaction += OnAfterTransaction;
        _doc.Destroyed += OnDocDestroyed;
    }

    public IReadOnlyList<StackItem> UndoStack => Read(() => _undoStack.ToArray());
    public IReadOnlyList<StackItem> RedoStack => Read(() => _redoStack.ToArray());
    public bool CanUndo => Read(() => _undoStack.Count > 0);
    public bool CanRedo => Read(() => _redoStack.Count > 0);

    /// <summary>Yjs <c>undo()</c>: pops undo stack items until one changes the document, in one write transaction. Null when none did.</summary>
    public StackItem? Undo() => Pop(redo: false);

    /// <summary>Yjs <c>redo()</c>.</summary>
    public StackItem? Redo() => Pop(redo: true);

    /// <summary>Yjs <c>stopCapturing()</c>: the next change starts a new stack item.</summary>
    public void StopCapturing() => Read(() => Volatile.Write(ref _lastChange, 0));

    /// <summary>
    /// Yjs <c>clear()</c>: drops the chosen stacks and releases the deleted items they kept from garbage collection, in a write transaction,
    /// then raises <see cref="StackCleared"/>. Does nothing when the chosen stacks are empty.
    /// </summary>
    public void Clear(bool undo = true, bool redo = true)
    {
        var h = _doc.EnterRead();
        try
        {
            if (!((undo && _undoStack.Count > 0) || (redo && _redoStack.Count > 0))) return;
        }
        finally
        {
            _doc.Leave(h);
        }

        ClearInTransaction(undo, redo);
    }

    /// <summary>Yjs <c>addToScope</c>: a type already in the scope is skipped. A type of another document is added but never matches (UM-16).</summary>
    public void AddToScope(YType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Read(() =>
        {
            lock (_gate) AddScope(type);
        });
    }

    /// <summary>Yjs <c>addTrackedOrigin</c>. A <see cref="Type"/> value matches any origin of that runtime type, as a JS constructor does (UM-01).</summary>
    public void AddTrackedOrigin(object? origin) => Read(() =>
    {
        lock (_gate) _trackedOrigins.Add(origin);
    });

    public void RemoveTrackedOrigin(object? origin) => Read(() =>
    {
        lock (_gate) _trackedOrigins.Remove(origin);
    });

    public event Action<StackItemEvent>? StackItemAdded, StackItemUpdated, StackItemPopped;
    public event Action<bool, bool>? StackCleared;

    /// <summary>
    /// Yjs <c>destroy()</c>: stops capturing, removes the manager from its tracked origins and drops its event handlers. Destroying the document
    /// disposes the manager too (UM-17). The stacks stay, and <see cref="Undo"/> and <see cref="Redo"/> keep working, as in JS.
    /// </summary>
    public void Dispose() => Read(DisposeCore);

    private void DisposeCore()
    {
        lock (_gate) _trackedOrigins.Remove(this);
        _doc.AfterTransaction -= OnAfterTransaction;
        _doc.Destroyed -= OnDocDestroyed;
        StackItemAdded = StackItemUpdated = StackItemPopped = null;
        StackCleared = null;
    }

    // internal test seams (ADR 0015): JS undoManager.undoing, redoing, currStackItem (UM-14)
    internal bool Undoing => _undoing;
    internal bool Redoing => _redoing;
    internal StackItem? CurrStackItem => _currStackItem;

    // ------------------------------------------------------------------ capture

    private void OnDocDestroyed(Doc _) => DisposeCore();   // raised under the document's write lock

    /// <summary>Yjs <c>afterTransactionHandler</c>: runs in the document's after-transaction hook, under the write lock and before garbage collection.</summary>
    private void OnAfterTransaction(Transaction tx, Doc doc)
    {
        var popping = Volatile.Read(ref _poppingThread) == Environment.CurrentManagedThreadId;
        var undoing = popping && _undoing;
        var redoing = popping && _redoing;
        if (popping && ReferenceEquals(tx.Origin, this)) _poppedChanged = tx.ChangedParentTypes.ToArray();
        if (!_captureTransaction(tx) || !ScopeChanged(tx) || !IsTracked(tx.Origin)) return;

        var stack = undoing ? _redoStack : _undoStack;
        if (undoing) Volatile.Write(ref _lastChange, 0);   // the next undo is not appended to the last stack item
        else if (!redoing) ClearRedoStackInHook();

        var insertions = new DeleteRanges(trackClientOrder: true);
        tx.State.AddInsertions(insertions);
        var deletions = Copy(tx.State.DeleteRanges);
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var lastChange = Volatile.Read(ref _lastChange);
        var didAdd = false;
        if (lastChange > 0 && now - lastChange < _captureTimeoutMs && stack.Count > 0 && !undoing && !redoing)
        {
            var last = stack[^1];
            last.DeletionRanges = Merge(last.DeletionRanges, deletions);
            last.InsertionRanges = Merge(last.InsertionRanges, insertions);
        }
        else
        {
            stack.Add(new StackItem(deletions, insertions));
            didAdd = true;
        }

        if (!undoing && !redoing) Volatile.Write(ref _lastChange, now);
        // deleted structs in scope must not be collected: an undo may need their content
        var store = doc.Store;
        IterateDeletedStructs(store, deletions, item =>
        {
            if (!item.IsGc && InScope(item)) StructStore.Keep(item, true);
        });
        var e = new StackItemEvent(stack[^1], tx.Origin, undoing, tx.ChangedParentTypes.ToArray());
        Raise(didAdd ? StackItemAdded : StackItemUpdated, e);
    }

    private bool ScopeChanged(Transaction tx)
    {
        var changed = tx.ChangedParentTypes;
        foreach (var s in _scope)
        {
            if (ReferenceEquals(s, _doc)) return true;
            if (s is YType type)
                foreach (var c in changed)
                    if (ReferenceEquals(c, type)) return true;
        }

        return false;
    }

    /// <summary>JS <c>trackedOrigins.has(origin) || (origin &amp;&amp; trackedOrigins.has(origin.constructor))</c>.</summary>
    private bool IsTracked(object? origin)
    {
        lock (_gate)
        {
            if (_trackedOrigins.Contains(origin)) return true;
            return !IsFalsy(origin) && _trackedOrigins.Contains(origin!.GetType());
        }
    }

    private static bool IsFalsy(object? v) => v switch
    {
        null => true,
        bool b => !b,
        string s => s.Length == 0,
        double d => d == 0 || double.IsNaN(d),
        float f => f == 0 || float.IsNaN(f),
        int i => i == 0,
        long l => l == 0,
        _ => false,
    };

    /// <summary>JS <c>scope.some(type =&gt; type === doc || isParentOf(type, item))</c>.</summary>
    private bool InScope(ItemRef item)
    {
        foreach (var s in _scope)
        {
            if (ReferenceEquals(s, _doc)) return true;
            if (s is YType type && IsParentOf(type.Branch, item)) return true;
        }

        return false;
    }

    /// <summary>Yjs <c>isParentOf</c>: whether <paramref name="parent"/> is the parent of the item or of one of its ancestors.</summary>
    private static bool IsParentOf(Branch parent, ItemRef child)
    {
        while (!child.IsNull && child.Parent is { } p)
        {
            if (ReferenceEquals(p, parent)) return true;
            child = StructStore.ItemOf(p);
        }

        return false;
    }

    // ------------------------------------------------------------------ clear

    /// <summary>Clearing the redo stack from the capture hook: JS calls <c>clear(false, true)</c>, whose transaction becomes a follow-up one here.</summary>
    private void ClearRedoStackInHook()
    {
        if (_redoStack.Count > 0) ClearInTransaction(undo: false, redo: true);
    }

    private void ClearInTransaction(bool undo, bool redo)
    {
        var h = _doc.EnterWrite(null, local: true);
        try
        {
            if (undo)
            {
                foreach (var item in _undoStack) Release(item);
                _undoStack = new List<StackItem>();
            }

            if (redo)
            {
                foreach (var item in _redoStack) Release(item);
                _redoStack = new List<StackItem>();
            }

            var cleared = StackCleared;
            if (cleared is not null)
            {
                foreach (var handler in Delegate.EnumerateInvocationList(cleared))
                {
                    try
                    {
                        handler(undo, redo);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        _doc.ReportObserverFault(ex, this);
                    }
                }
            }
        }
        finally
        {
            _doc.Leave(h);
        }
    }

    /// <summary>Yjs <c>clearUndoManagerStackItem</c>: deleted items in scope are no longer kept.</summary>
    private void Release(StackItem stackItem) =>
        IterateDeletedStructs(_doc.Store, stackItem.DeletionRanges, item =>
        {
            if (!item.IsGc && InScope(item)) StructStore.Keep(item, false);
        });

    // ------------------------------------------------------------------ undo and redo

    private StackItem? Pop(bool redo)
    {
        var h = _doc.EnterWrite(this, local: true);
        StackItem? popped = null;
        var outer = Volatile.Read(ref _poppingThread);
        try
        {
            Volatile.Write(ref _poppingThread, Environment.CurrentManagedThreadId);
            _undoing = !redo;
            _redoing = redo;
            _poppedChanged = Array.Empty<YType>();
            try
            {
                popped = PopStackItems(h.Tx, redo ? _redoStack : _undoStack);
                _currStackItem = popped;
            }
            finally
            {
                _doc.Leave(h);   // cleanup runs here, with the flags set, so the capture hook files the change on the other stack
            }

            if (popped is not null)
            {
                Raise(StackItemPopped, new StackItemEvent(popped, this, redo, _poppedChanged));
                Interlocked.CompareExchange(ref _currStackItem, null, popped);
            }
        }
        finally
        {
            _undoing = _redoing = false;
            Volatile.Write(ref _poppingThread, outer);
        }

        return popped;
    }

    /// <summary>Yjs <c>popStackItem</c>, the body of its transaction.</summary>
    private StackItem? PopStackItems(Transaction tx, List<StackItem> stack)
    {
        var store = _doc.Store;
        StackItem? result = null;
        while (stack.Count > 0 && result is null)
        {
            var stackItem = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            var itemsToRedo = new List<ItemRef>();
            var redoSet = new HashSet<ItemRef>();
            var itemsToDelete = new List<ItemRef>();
            var performedChange = false;
            IterateDeletedStructs(store, stackItem.InsertionRanges, s =>
            {
                if (s.IsGc) return;
                if (store.RedoneOf(s) is not null)
                {
                    var (item, diff) = store.FollowRedone(store.IdOf(s));
                    if (diff > 0) item = store.GetItemCleanStart(store.ClientOf(item), item.Clock + diff);
                    s = item;
                }

                if (!s.IsDeleted && InScope(s)) itemsToDelete.Add(s);
            });
            IterateDeletedStructs(store, stackItem.DeletionRanges, s =>
            {
                // never redo structs in the insertions: they were created and deleted in the same capture interval
                if (!s.IsGc && InScope(s) && !stackItem.InsertionRanges.Contains(store.ClientOf(s), s.Clock) && redoSet.Add(s)) itemsToRedo.Add(s);
            });
            foreach (var s in itemsToRedo)
                performedChange = !RedoItem(tx, s, redoSet, stackItem.InsertionRanges).IsNull || performedChange;
            // delete in reverse order, so children go before their parents and the filter sees more
            for (var i = itemsToDelete.Count - 1; i >= 0; i--)
            {
                var item = itemsToDelete[i];
                if (_deleteFilter(new ItemView(item, store)))
                {
                    _doc.DeleteItem(tx, item);
                    performedChange = true;
                }
            }

            result = performedChange ? stackItem : null;
        }

        // the integrations above bypass the position caches: drop them
        foreach (var (type, keys) in tx.State.Changed)
            if (keys.Contains(null)) type.Branch.Markers?.Clear();
        return result;
    }

    /// <summary>
    /// Yjs <c>redoItem</c>: integrates a copy of the deleted <paramref name="item"/> next to where it was, redoing its deleted parent first when
    /// that is being redone too. Returns the copy, or null when the item cannot be restored.
    /// </summary>
    private ItemRef RedoItem(Transaction tx, ItemRef item, HashSet<ItemRef> redoItems, DeleteRanges insertions)
    {
        var store = _doc.Store;
        if (store.RedoneOf(item) is { } redone) return store.GetItemCleanStart(redone.Client, redone.Clock);
        var parentItem = StructStore.ItemOf(item.Parent!);
        if (!parentItem.IsNull && parentItem.IsDeleted)
        {
            // try to redo the parent if it will be redone anyway
            if (store.RedoneOf(parentItem) is null && (!redoItems.Contains(parentItem) || RedoItem(tx, parentItem, redoItems, insertions).IsNull))
                return ItemRef.Null;
            while (store.RedoneOf(parentItem) is { } next) parentItem = store.GetItemCleanStart(next.Client, next.Clock);
        }

        var parentType = parentItem.IsNull ? item.Parent! : (Branch)store.GetPayload(parentItem);
        ItemRef left, right;
        string? key = null;
        if (!item.HasParentSub)
        {
            // an array item: insert at the old position, next to the redone copies of its neighbours where there are any
            left = item.Left;
            right = item;
            while (!left.IsNull)
            {
                var trace = TraceToParent(store, left, parentItem);
                if (!trace.IsNull)
                {
                    left = trace;
                    break;
                }

                left = left.Left;
            }

            while (!right.IsNull)
            {
                var trace = TraceToParent(store, right, parentItem);
                if (!trace.IsNull)
                {
                    right = trace;
                    break;
                }

                right = right.Right;
            }
        }
        else
        {
            key = store.ParentSubOf(item)!;
            right = ItemRef.Null;
            if (!item.Right.IsNull && !_ignoreRemoteMapChanges)
            {
                left = item;
                // walk right while the right item is to be deleted: the redone item is meant to replace it
                while (!left.IsNull && !left.Right.IsNull && (store.RedoneOf(left.Right) is not null || insertions.Contains(store.ClientOf(left.Right), left.Right.Clock)
                    || IsDeletedByStack(_undoStack, store, left.Right) || IsDeletedByStack(_redoStack, store, left.Right)))
                {
                    left = left.Right;
                    while (store.RedoneOf(left) is { } next) left = store.GetItemCleanStart(next.Client, next.Clock);
                }

                // a change from another client conflicts: this item cannot be redone
                if (!left.IsNull && !left.Right.IsNull) return ItemRef.Null;
            }
            else
            {
                left = new ItemRef(parentType.Map?.Get(key));
            }

            // drop a left item of another parent, so its id does not mislead the remote (yjs#757)
            if (!left.IsNull && StructStore.ItemOf(left.Parent!) != parentItem) left = new ItemRef(parentType.Map?.Get(key));
        }

        var client = _doc.ClientId;
        var nextId = new ItemId(client, store.GetState(client));
        var (kind, content, length) = store.CopyContent(item);
        var redoneItem = store.CreateLocalItem(client, parentType, left, right, key, key is not null, kind, content, length);
        store.SetRedone(item, nextId);
        StructStore.Keep(redoneItem, true);
        _doc.IntegrateLocal(tx, redoneItem);
        return redoneItem;
    }

    /// <summary>The item, or the copy it was redone into and so on, whose parent item is <paramref name="parentItem"/>; null when there is none.</summary>
    private static ItemRef TraceToParent(StructStore store, ItemRef item, ItemRef parentItem)
    {
        var trace = item;
        while (!trace.IsNull && (trace.Parent is not { } p || StructStore.ItemOf(p) != parentItem))
            trace = store.RedoneOf(trace) is { } r ? store.GetItemCleanStart(r.Client, r.Clock) : ItemRef.Null;
        return trace;
    }

    /// <summary>Yjs <c>isDeletedByUndoStack</c>.</summary>
    private static bool IsDeletedByStack(List<StackItem> stack, StructStore store, ItemRef item)
    {
        var client = store.ClientOf(item);
        foreach (var s in stack)
            if (s.DeletionRanges.Contains(client, item.Clock)) return true;
        return false;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Yjs <c>iterateDeletedStructs</c>: every struct in the ranges, split so each lies wholly inside one, skipping ranges past the client's
    /// state. Clients run in the set's client order, as the JS Map does: the order which decides the redone items' clocks.
    /// </summary>
    private static void IterateDeletedStructs(StructStore store, DeleteRanges ds, Action<ItemRef> f)
    {
        var order = ds.ClientOrder!;
        for (var c = 0; c < order.Count; c++)
        {
            var client = order[c];
            if (store.StructCount(client) == 0) continue;
            var state = store.GetState(client);
            for (var i = ds.FirstIndexOf(client); i < ds.Count && ds[i].Client == client && ds[i].Clock < state; i++)
                IterateStructs(store, client, ds[i].Clock, ds[i].Length, f);
        }
    }

    /// <summary>Yjs <c>iterateStructs</c>: a clean start at the first clock, a clean end at the last.</summary>
    private static void IterateStructs(StructStore store, ulong client, long clock, long length, Action<ItemRef> f)
    {
        if (length == 0) return;
        var end = clock + length;
        var index = store.FindIndexCleanStart(client, clock);
        do
        {
            var s = store.StructAt(client, index++);
            if (end < s.Clock + s.Length) store.FindIndexCleanStart(client, end);
            f(s);
        }
        while (index < store.StructCount(client) && store.StructAt(client, index).Clock < end);
    }

    /// <summary>A copy that keeps the client order of <paramref name="ranges"/>, which must track it.</summary>
    private static DeleteRanges Copy(DeleteRanges ranges)
    {
        var copy = new DeleteRanges(trackClientOrder: true);
        AddInOrder(copy, ranges);
        return copy;
    }

    /// <summary>Yjs <c>mergeDeleteSets</c>, into a new set: the clients of <paramref name="a"/>, then those new in <paramref name="b"/>.</summary>
    private static DeleteRanges Merge(DeleteRanges a, DeleteRanges b)
    {
        var merged = Copy(a);
        AddInOrder(merged, b);
        return merged;
    }

    private static void AddInOrder(DeleteRanges into, DeleteRanges from)
    {
        var order = from.ClientOrder!;
        for (var c = 0; c < order.Count; c++)
            for (var i = from.FirstIndexOf(order[c]); i < from.Count && from[i].Client == order[c]; i++)
                into.Add(from[i].Client, from[i].Clock, from[i].Length);
    }

    private void AddScope(object scope)
    {
        foreach (var s in _scope)
            if (ReferenceEquals(s, scope)) return;
        _scope.Add(scope);   // JS warns "[yjs#509] Not same Y.Doc" for another document's type and adds it anyway
    }

    private T Read<T>(Func<T> body)
    {
        var h = _doc.EnterRead();
        try
        {
            return body();
        }
        finally
        {
            _doc.Leave(h);
        }
    }

    private void Read(Action body)
    {
        var h = _doc.EnterRead();
        try
        {
            body();
        }
        finally
        {
            _doc.Leave(h);
        }
    }

    private void Raise(Action<StackItemEvent>? handlers, StackItemEvent e)
    {
        if (handlers is null) return;
        foreach (var handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(e);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _doc.ReportObserverFault(ex, this);
            }
        }
    }
}
