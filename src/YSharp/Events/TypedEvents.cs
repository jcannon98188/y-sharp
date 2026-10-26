#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using YSharp.Store;
using YSharp.Transactions;

namespace YSharp;

/// <summary>Yjs <c>YArrayEvent</c>.</summary>
public sealed class YArrayEvent : YEvent
{
    private ListDeltaOp[]? _delta;

    internal YArrayEvent(YType target, Transaction tx, HashSet<string?> subs) : base(target, tx, subs) { }

    /// <summary>Yjs <c>changes.delta</c> (V-6).</summary>
    public ReadOnlySpan<ListDeltaOp> Delta
    {
        get
        {
            _ = State;
            return _delta ??= ListDelta();
        }
    }
}

/// <summary>Yjs <c>YMapEvent</c>.</summary>
public sealed class YMapEvent : YEvent
{
    internal YMapEvent(YType target, Transaction tx, HashSet<string?> subs) : base(target, tx, subs) { }

    /// <summary>Yjs <c>keysChanged</c>: every key the transaction wrote, even when the net effect is none (M-E1).</summary>
    public IReadOnlyCollection<string> KeysChanged => ChangedKeys;
}

/// <summary>Yjs <c>YTextEvent</c>, raised by <see cref="YText"/> and <see cref="YXmlText"/>.</summary>
public sealed class YTextEvent : YEvent
{
    private TextDeltaOp[]? _delta;

    internal YTextEvent(YType target, Transaction tx, HashSet<string?> subs) : base(target, tx, subs) { }

    /// <summary>Yjs <c>delta</c>: the change as a Quill delta; a trailing retain without attributes is dropped (Y-2).</summary>
    public ReadOnlySpan<TextDeltaOp> Delta
    {
        get
        {
            var state = State;
            return _delta ??= TextDelta(state);
        }
    }

    /// <summary>Yjs <c>childListChanged</c>: the content changed, not only the attributes (Y-1).</summary>
    public bool ChildListChanged => ListChanged;

    /// <summary>Yjs <c>keysChanged</c>: the attributes the transaction wrote (Y-1).</summary>
    public IReadOnlyCollection<string> KeysChanged => ChangedKeys;

    /// <summary>
    /// Yjs <c>YTextEvent.delta</c>, side effect included (Y-4): like Yjs, the walk runs in a transaction (a follow-up one while observers run)
    /// and deletes the redundant format items it meets, so a replica that reads the delta sends the same deletes as a JS one. Yjs's own
    /// formatting cleanup misses these items, so the delete is observable.
    /// </summary>
    private TextDeltaOp[] TextDelta(TxState state)
    {
        var doc = state.Doc;
        var h = doc.EnterWrite(null, local: true);
        try
        {
            return TextDelta(state, h.Tx);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    private TextDeltaOp[] TextDelta(TxState state, Transaction tx)
    {
        var doc = state.Doc;
        var store = doc.Store;
        var delta = new List<TextDeltaOp>();
        var current = new OrderedDictionary<string, YValue>(StringComparer.Ordinal);
        var old = new OrderedDictionary<string, YValue>(StringComparer.Ordinal);
        var attributes = new OrderedDictionary<string, YValue>(StringComparer.Ordinal);
        DeltaKind? action = null;
        var insert = new StringBuilder();
        YEntry? embed = null;
        var retain = 0;
        var deleteLength = 0;

        void AddOp()
        {
            if (action is not { } a) return;
            switch (a)
            {
                case DeltaKind.Delete:
                    if (deleteLength > 0) delta.Add(new TextDeltaOp(DeltaKind.Delete, deleteLength));
                    deleteLength = 0;
                    break;
                case DeltaKind.Insert:
                    if (embed is { } e) delta.Add(new TextDeltaOp(DeltaKind.Insert, 1, null, e, TypeText.AttributesOf(current)));
                    else if (insert.Length > 0) delta.Add(new TextDeltaOp(DeltaKind.Insert, insert.Length, insert.ToString(), default, TypeText.AttributesOf(current)));
                    insert.Clear();
                    embed = null;
                    break;
                case DeltaKind.Retain:
                    if (retain > 0) delta.Add(new TextDeltaOp(DeltaKind.Retain, retain, null, default, TypeText.AttributesOf(attributes)));
                    retain = 0;
                    break;
            }

            action = null;
        }

        for (var n = StructStore.StartOf(TargetType.Branch); !n.IsNull; n = n.Right)
        {
            switch (n.Kind)
            {
                case ContentKind.Type:
                case ContentKind.Embed:
                    if (AddsItem(state, n))
                    {
                        if (!DeletesItem(state, n))
                        {
                            AddOp();
                            action = DeltaKind.Insert;
                            embed = TypeList.ContentAt(doc, n, 0);
                            AddOp();
                        }
                    }
                    else if (DeletesItem(state, n))
                    {
                        if (action != DeltaKind.Delete)
                        {
                            AddOp();
                            action = DeltaKind.Delete;
                        }

                        deleteLength += 1;
                    }
                    else if (!n.IsDeleted)
                    {
                        if (action != DeltaKind.Retain)
                        {
                            AddOp();
                            action = DeltaKind.Retain;
                        }

                        retain += 1;
                    }

                    break;
                case ContentKind.String:
                    if (AddsItem(state, n))
                    {
                        if (!DeletesItem(state, n))
                        {
                            if (action != DeltaKind.Insert)
                            {
                                AddOp();
                                action = DeltaKind.Insert;
                            }

                            store.AppendString(insert, n);
                        }
                    }
                    else if (DeletesItem(state, n))
                    {
                        if (action != DeltaKind.Delete)
                        {
                            AddOp();
                            action = DeltaKind.Delete;
                        }

                        deleteLength += (int)n.Length;
                    }
                    else if (!n.IsDeleted)
                    {
                        if (action != DeltaKind.Retain)
                        {
                            AddOp();
                            action = DeltaKind.Retain;
                        }

                        retain += (int)n.Length;
                    }

                    break;
                case ContentKind.Format:
                {
                    var format = TypeText.Format(store, n);
                    var (key, value) = (format.Key, format.Value);
                    var deleted = n.IsDeleted;
                    if (AddsItem(state, n))
                    {
                        if (!DeletesItem(state, n))
                        {
                            var curVal = current.TryGetValue(key, out var c) ? c : YValue.Null;
                            if (!TypeText.EqualAttrs(curVal, value))
                            {
                                if (action == DeltaKind.Retain) AddOp();
                                var oldVal = old.TryGetValue(key, out var o) ? o : YValue.Null;
                                if (TypeText.EqualAttrs(value, oldVal)) attributes.Remove(key);
                                else attributes[key] = value;
                            }
                            else if (value.Kind != YValueKind.Null)
                            {
                                doc.DeleteItem(tx, n);
                                deleted = true;
                            }
                        }
                    }
                    else if (DeletesItem(state, n))
                    {
                        old[key] = value;
                        var curVal = current.TryGetValue(key, out var c) ? c : YValue.Null;
                        if (!TypeText.EqualAttrs(curVal, value))
                        {
                            if (action == DeltaKind.Retain) AddOp();
                            attributes[key] = curVal;
                        }
                    }
                    else if (!deleted)
                    {
                        old[key] = value;
                        if (attributes.TryGetValue(key, out var attr))
                        {
                            if (!TypeText.EqualAttrs(attr, value))
                            {
                                if (action == DeltaKind.Retain) AddOp();
                                if (value.Kind == YValueKind.Null) attributes.Remove(key);
                                else attributes[key] = value;
                            }
                            else if (attr.Kind != YValueKind.Null)
                            {
                                doc.DeleteItem(tx, n);
                                deleted = true;
                            }
                        }
                    }

                    if (!deleted)
                    {
                        if (action == DeltaKind.Insert) AddOp();
                        TypeText.UpdateCurrent(current, format);
                    }

                    break;
                }
            }
        }

        AddOp();
        var count = delta.Count;
        while (count > 0 && delta[count - 1].Kind == DeltaKind.Retain && delta[count - 1].Attributes.Kind == YValueKind.Undefined) count--;
        delta.RemoveRange(count, delta.Count - count);
        return delta.ToArray();
    }
}

/// <summary>Yjs <c>YXmlEvent</c>, raised by <see cref="YXmlFragment"/> and <see cref="YXmlElement"/>.</summary>
public sealed class YXmlEvent : YEvent
{
    private ListDeltaOp[]? _delta;

    internal YXmlEvent(YType target, Transaction tx, HashSet<string?> subs) : base(target, tx, subs) { }

    /// <summary>Yjs <c>changes.delta</c>.</summary>
    public ReadOnlySpan<ListDeltaOp> Delta
    {
        get
        {
            _ = State;
            return _delta ??= ListDelta();
        }
    }

    /// <summary>Yjs <c>childListChanged</c>.</summary>
    public bool ChildListChanged => ListChanged;

    /// <summary>Yjs <c>attributesChanged</c>.</summary>
    public IReadOnlyCollection<string> AttributesChanged => ChangedKeys;
}
// YXmlText raises YTextEvent and YXmlHook raises YMapEvent (T-0.1).
