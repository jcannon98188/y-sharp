#nullable enable
using System;
using System.Collections.Generic;
using YSharp.Store;
using YSharp.Transactions;

namespace YSharp;

public readonly record struct YPathSegment(string? Key, int Index);   // Key for map parents, Index for list parents (V-2)

/// <summary>
/// Yjs <c>YEvent</c>: what one transaction changed in one type. It is valid during its dispatch only: once the transaction ends, every member
/// but <see cref="IsValid"/> throws <see cref="YLifetimeException"/> (M-E5, ADR 0005). The computed members are lazy and cached.
/// </summary>
public abstract class YEvent
{
    private readonly YType _target;
    private readonly Transaction _tx;
    private readonly HashSet<string?> _subs;
    private YType _currentTarget;
    private YPathSegment[]? _path;
    private Dictionary<string, KeyChange>? _keys;
    private string[]? _keysChanged;

    private protected YEvent(YType target, Transaction tx, HashSet<string?> subs)
    {
        _target = target;
        _currentTarget = target;
        _tx = tx;
        _subs = subs;
    }

    public YType Target
    {
        get
        {
            _ = State;
            return _target;
        }
    }

    /// <summary>The type whose handler is running: the target for <see cref="YType.Observe"/>, the observed ancestor for a deep handler (V-3).</summary>
    public YType CurrentTarget
    {
        get
        {
            _ = State;
            return _currentTarget;
        }
    }

    public Transaction Transaction
    {
        get
        {
            _ = State;
            return _tx;
        }
    }

    /// <summary>False once the transaction ended. The only member that never throws.</summary>
    public bool IsValid => _tx.IsValid;

    /// <summary>Yjs <c>path</c>: the keys and indices from <see cref="CurrentTarget"/> down to <see cref="Target"/>.</summary>
    public IReadOnlyList<YPathSegment> GetPath()
    {
        var state = State;
        return _path ??= PathTo(state.Doc.Store, _currentTarget.Branch, _target.Branch);
    }

    /// <summary>Yjs <c>adds</c>: whether the struct with this id was created by the transaction.</summary>
    public bool Adds(ItemId id) => id.Clock >= State.BeforeClock(id.Client);

    /// <summary>Yjs <c>deletes</c>: whether the transaction deleted the struct with this id.</summary>
    public bool Deletes(ItemId id) => State.DeleteRanges.Contains(id.Client, id.Clock);

    /// <summary>Yjs <c>keys</c>: the net change of each changed key, with the value it had before the transaction (V-5).</summary>
    public IReadOnlyDictionary<string, KeyChange> Keys
    {
        get
        {
            var state = State;
            return _keys ??= ComputeKeys(state);
        }
    }

    // ------------------------------------------------------------------ for the typed events

    /// <summary>The live transaction state; throws <see cref="YLifetimeException"/> once the transaction ended.</summary>
    private protected TxState State => _tx.State;

    /// <summary>Whether the child list changed (Yjs <c>childListChanged</c>: the changed keys hold null).</summary>
    private protected bool ListChanged
    {
        get
        {
            _ = State;
            return _subs.Contains(null);
        }
    }

    /// <summary>The changed keys (Yjs <c>keysChanged</c>, <c>attributesChanged</c>).</summary>
    private protected IReadOnlyCollection<string> ChangedKeys
    {
        get
        {
            _ = State;
            if (_keysChanged is { } cached) return cached;
            var keys = new List<string>(_subs.Count);
            foreach (var sub in _subs)
                if (sub is not null) keys.Add(sub);
            return _keysChanged = keys.ToArray();
        }
    }

    private protected YType TargetType => _target;

    private protected static bool AddsItem(TxState state, ItemRef item) => item.Clock >= state.BeforeClock(state.Doc.Store.ClientIdOf(item));

    private protected static bool DeletesItem(TxState state, ItemRef item) => state.DeleteRanges.Contains(state.Doc.Store.ClientIdOf(item), item.Clock);

    /// <summary>Yjs <c>changes.delta</c> for the list types: inserts, deletes and retains in list order; a trailing retain is dropped (V-6).</summary>
    private protected ListDeltaOp[] ListDelta()
    {
        var state = State;
        if (!_subs.Contains(null)) return Array.Empty<ListDeltaOp>();
        var doc = state.Doc;
        var ops = new List<ListDeltaOp>();
        DeltaKind? kind = null;
        var length = 0;
        List<YEntry>? inserted = null;

        void Pack()
        {
            if (kind is not { } k) return;
            ops.Add(k == DeltaKind.Insert ? new ListDeltaOp(k, inserted!.Count, inserted.ToArray()) : new ListDeltaOp(k, length));
            kind = null;
            length = 0;
            inserted = null;
        }

        for (var n = StructStore.StartOf(_target.Branch); !n.IsNull; n = n.Right)
        {
            if (n.IsDeleted)
            {
                if (!DeletesItem(state, n) || AddsItem(state, n)) continue;
                if (kind != DeltaKind.Delete)
                {
                    Pack();
                    kind = DeltaKind.Delete;
                }

                length += (int)n.Length;
            }
            else if (AddsItem(state, n))
            {
                if (kind != DeltaKind.Insert)
                {
                    Pack();
                    kind = DeltaKind.Insert;
                    inserted = new List<YEntry>();
                }

                TypeList.AddContent(doc, n, inserted!);
            }
            else
            {
                if (kind != DeltaKind.Retain)
                {
                    Pack();
                    kind = DeltaKind.Retain;
                }

                length += (int)n.Length;
            }
        }

        if (kind != DeltaKind.Retain) Pack();
        return ops.ToArray();
    }

    /// <summary>The deep phase moves the event to the next observed ancestor: its path is relative to that one (Yjs resets <c>_path</c>).</summary>
    internal void SetCurrentTarget(YType currentTarget)
    {
        _currentTarget = currentTarget;
        _path = null;
    }

    /// <summary>The number of path segments from <paramref name="ancestor"/> down to the target, for the deep phase's sort.</summary>
    internal int DepthBelow(YType ancestor)
    {
        var depth = 0;
        for (var b = _target.Branch; b.Item is { } item && !ReferenceEquals(b, ancestor.Branch); b = item.Parent!) depth++;
        return depth;
    }

    /// <summary>Yjs <c>getPathTo</c>: a key for a keyed child, else the index counting the undeleted countable items before it.</summary>
    private static YPathSegment[] PathTo(StructStore store, Branch parent, Branch child)
    {
        var path = new List<YPathSegment>();
        while (child.Item is { } item && !ReferenceEquals(child, parent))
        {
            var itemRef = new ItemRef(item);
            if (store.ParentSubOf(itemRef) is { } key)
            {
                path.Add(new YPathSegment(key, 0));
            }
            else
            {
                var index = 0;
                for (var c = StructStore.StartOf(item.Parent!); !c.IsNull && c != itemRef; c = c.Right)
                    if (!c.IsDeleted && c.IsCountable) index += (int)c.Length;
                path.Add(new YPathSegment(null, index));
            }

            child = item.Parent!;
        }

        path.Reverse();
        return path.ToArray();
    }

    /// <summary>Yjs <c>YEvent.keys</c>.</summary>
    private Dictionary<string, KeyChange> ComputeKeys(TxState state)
    {
        var keys = new Dictionary<string, KeyChange>(StringComparer.Ordinal);
        var map = _target.Branch.Map;
        foreach (var key in _subs)
        {
            if (key is null || map?.Get(key) is not { } current) continue;
            var item = new ItemRef(current);
            if (AddsItem(state, item))
            {
                var prev = item.Left;
                while (!prev.IsNull && AddsItem(state, prev)) prev = prev.Left;
                if (DeletesItem(state, item))
                {
                    if (!prev.IsNull && DeletesItem(state, prev)) keys[key] = new KeyChange(KeyChangeAction.Delete, LastContent(state.Doc, prev));
                }
                else if (!prev.IsNull && DeletesItem(state, prev))
                {
                    keys[key] = new KeyChange(KeyChangeAction.Update, LastContent(state.Doc, prev));
                }
                else
                {
                    keys[key] = new KeyChange(KeyChangeAction.Add, null);
                }
            }
            else if (DeletesItem(state, item))
            {
                keys[key] = new KeyChange(KeyChangeAction.Delete, LastContent(state.Doc, item));
            }
        }

        return keys;
    }

    /// <summary>JS <c>item.content.getContent()[item.length - 1]</c>: undefined (null here) for garbage-collected content.</summary>
    private static YEntry? LastContent(Doc doc, ItemRef item) =>
        item.Kind is ContentKind.Deleted or ContentKind.Format ? (YEntry?)null : TypeList.ContentAt(doc, item, (int)item.Length - 1);
}
