#nullable enable
using System.Collections.Generic;
using YSharp.Integration;
using YSharp.Store;

namespace YSharp;

// The YATA core: Yjs integrateStructs, Item.getMissing, Item.integrate and Item.delete (docs/research/structs.md sections C, D, E1, K2, K3).
public sealed partial class Doc
{
    // the conflict scan's two sets (Yjs conflictingItems and itemsBeforeOrigin), reused: integration runs under the write lock
    private HashSet<ItemRef>? _conflicting;
    private HashSet<ItemRef>? _beforeOrigin;

    // Content hooks for the modules that own what a content does on integration and deletion (ADR 0021, partial methods compile away):
    // the shared types bind a nested type (TASK-4.21), text formatting marks its parent (TASK-4.25), subdocuments (TASK-4.33).
    partial void HookTypeIntegrated(Transaction tx, Branch type);
    partial void HookFormatIntegrated(Transaction tx, ItemRef item);
    partial void HookSubdocIntegrated(Transaction tx, ItemRef item);
    partial void HookSubdocDeleted(Transaction tx, ItemRef item);

    /// <summary>What <see cref="Resolve"/> found for a struct.</summary>
    private enum Resolution
    {
        Ready,
        Missing,
        Rejected,
    }

    /// <summary>The neighbours and parent <see cref="Resolve"/> found (Yjs sets them on the item in <c>getMissing</c>).</summary>
    private struct Resolved
    {
        public ItemRef Left;
        public ItemRef Right;

        /// <summary>Yjs <c>origin = left.lastId</c>, taken before the right-origin lookup can split <see cref="Left"/>.</summary>
        public ItemId? Origin;
        public Branch? Parent;
        public string? ParentSub;

        /// <summary>A dry run only: the struct would be stored as a GC (a collected neighbour or parent, or no parent at all).</summary>
        public bool Collected;
    }

    // ------------------------------------------------------------------ integrateStructs (K2, K3)

    /// <summary>
    /// Yjs <c>integrateStructs</c>: integrates every struct whose dependencies are known, following missing dependencies into other clients'
    /// structs of the same update, and returns what cannot be integrated yet, or null. A struct that can never be integrated is rejected with
    /// the rest of its client's structs and everything in this update that depends on them (ADR 0005, ERR-07); rejected structs are counted
    /// in <paramref name="rejected"/> and are not kept. With <paramref name="dry"/> nothing is integrated: the structs are placed in the dry run's
    /// virtual state instead, so the same walk predicts what would be left (<see cref="PredictPendingBytes"/>).
    /// </summary>
    private PendingStructs? IntegrateStructs(Transaction tx, UpdateRefs update, ref int rejected, PendingDryRun? dry = null)
    {
        if (update.ClientCount == 0) return null;
        // higher client ids first: in case of conflicts the lower id will probably not conflict with the higher one
        var ids = new List<ulong>();
        foreach (var c in update.Clients) ids.Add(c.Client);
        ids.Sort();

        var cur = NextTarget(update, ids);
        if (cur is null) return null;

        var stack = new List<StructRef>();
        PendingStructs? rest = null;
        Dictionary<ulong, long>? rejectedFrom = null;
        var state = new Dictionary<ulong, long>();
        var head = cur.Refs[cur.Next++];

        while (true)
        {
            if (head.Kind != RefKind.Skip)
            {
                if (!state.TryGetValue(head.Client, out var localClock)) state[head.Client] = localClock = StateOf(head.Client, dry);
                var offset = localClock - head.Clock;
                if (offset < 0)
                {
                    // an earlier struct of the same client is missing
                    stack.Add(head);
                    (rest ??= new PendingStructs()).AddMissing(head.Client, head.Clock - 1);
                    AddStackToRest(update, ids, stack, rest);
                }
                else
                {
                    var resolution = Resolve(in head, dry, out var resolved, out var missingClient, out var missingClock);
                    if (resolution == Resolution.Missing && rejectedFrom is not null && rejectedFrom.TryGetValue(missingClient, out var from) &&
                        missingClock >= from)
                        resolution = Resolution.Rejected;   // it depends on a rejected struct

                    if (resolution == Resolution.Rejected)
                    {
                        rejected += Reject(update, ids, head, ref rejectedFrom);
                    }
                    else if (resolution == Resolution.Missing)
                    {
                        stack.Add(head);
                        if (update.TryGet(missingClient, out var refs) && refs.Next < refs.Refs.Count)
                        {
                            head = refs.Refs[refs.Next++];
                            continue;
                        }

                        // this update depends on another update that has not arrived
                        (rest ??= new PendingStructs()).AddMissing(missingClient, StateOf(missingClient, dry));
                        AddStackToRest(update, ids, stack, rest);
                    }
                    else if (offset == 0 || offset < head.Length)
                    {
                        if (dry is null) Integrate(tx, head, offset, resolved);
                        else dry.Place(head.Client, head.Clock + offset, head.End, head.Kind == RefKind.Gc || resolved.Collected ? ContentKind.Gc : head.Content);
                        state[head.Client] = head.End;
                    }
                    // else: already known entirely, dropped (STR-62)
                }
            }

            if (stack.Count > 0)
            {
                head = stack[^1];
                stack.RemoveAt(stack.Count - 1);
            }
            else if (cur is not null && cur.Next < cur.Refs.Count)
            {
                head = cur.Refs[cur.Next++];
            }
            else
            {
                cur = NextTarget(update, ids);
                if (cur is null) break;
                head = cur.Refs[cur.Next++];
            }
        }

        return rest is { ClientCount: > 0 } ? rest : null;
    }

    private static ClientRefs? NextTarget(UpdateRefs update, List<ulong> ids)
    {
        while (ids.Count > 0)
        {
            if (update.TryGet(ids[^1], out var target) && target.Next < target.Refs.Count) return target;
            ids.RemoveAt(ids.Count - 1);
        }

        return null;
    }

    /// <summary>Yjs <c>addStackToRestSS</c>: moves the stacked structs' clients, from their failed struct on, into the rest.</summary>
    private static void AddStackToRest(UpdateRefs update, List<ulong> ids, List<StructRef> stack, PendingStructs rest)
    {
        foreach (var item in stack)
        {
            var client = item.Client;
            if (update.TryGet(client, out var refs))
            {
                // the client's last read struct is the one that could not be applied
                refs.Next--;
                rest.Set(client, refs.Refs.GetRange(refs.Next, refs.Refs.Count - refs.Next));
                update.Remove(client);
                refs.Next = 0;
                refs.Refs = new List<StructRef>();
            }
            else
            {
                rest.Set(client, new[] { item });
            }

            ids.Remove(client);
        }

        stack.Clear();
    }

    /// <summary>Rejects <paramref name="head"/> and the rest of its client's structs in this update. Returns how many were rejected.</summary>
    private static int Reject(UpdateRefs update, List<ulong> ids, in StructRef head, ref Dictionary<ulong, long>? rejectedFrom)
    {
        rejectedFrom ??= new Dictionary<ulong, long>();
        if (!rejectedFrom.TryGetValue(head.Client, out var from) || from > head.Clock) rejectedFrom[head.Client] = head.Clock;
        var count = 1;
        if (update.TryGet(head.Client, out var refs))
        {
            for (var i = refs.Next; i < refs.Refs.Count; i++)
                if (refs.Refs[i].Kind != RefKind.Skip) count++;
            update.Remove(head.Client);
            refs.Next = 0;
            refs.Refs = new List<StructRef>();
        }

        ids.Remove(head.Client);
        return count;
    }

    // ------------------------------------------------------------------ getMissing (C1 to C4)

    /// <summary>
    /// Yjs <c>Item.getMissing</c>: the first missing dependency in the order origin, right origin, parent id (from another client, at or beyond
    /// that client's state); otherwise finds the neighbours, splitting the structs they lie in (even if the struct is never integrated), and the
    /// parent. A dependency of the struct's own client must lie before it, and a parent id must name a type (or a collected struct, which
    /// turns the struct into a GC); anything else is rejected (ADR 0005) where Yjs would fail or misbehave.
    /// </summary>
    private Resolution Resolve(in StructRef r, PendingDryRun? dry, out Resolved resolved, out ulong missingClient, out long missingClock)
    {
        resolved = default;
        missingClient = 0;
        missingClock = 0;
        if (r.Kind != RefKind.Item) return Resolution.Ready;   // GC and Skip never wait (STR-16)

        if (SelfDependencyAhead(r.Origin, r) || SelfDependencyAhead(r.RightOrigin, r) || SelfDependencyAhead(r.ParentId, r)) return Resolution.Rejected;
        if (IsMissing(r.Origin, r.Client, dry, out missingClient, out missingClock) ||
            IsMissing(r.RightOrigin, r.Client, dry, out missingClient, out missingClock) ||
            IsMissing(r.ParentId, r.Client, dry, out missingClient, out missingClock))
            return Resolution.Missing;

        if (dry is not null)
        {
            var dryResolution = DryResolve(dry, in r, out var collected);
            resolved.Collected = collected;
            return dryResolution;
        }

        if (r.Origin is { } o)
        {
            resolved.Left = Store.GetItemCleanEnd(o.Client, o.Clock);
            resolved.Origin = new ItemId(Store.ClientIdOf(resolved.Left), resolved.Left.LastClock);
        }
        if (r.RightOrigin is { } ro) resolved.Right = Store.GetItemCleanStart(ro.Client, ro.Clock);

        if ((!resolved.Left.IsNull && resolved.Left.IsGc) || (!resolved.Right.IsNull && resolved.Right.IsGc))
        {
            resolved.Parent = null;   // a neighbour was collected: the struct becomes a GC
        }
        else if (r.ParentRoot is null && r.ParentId is null)
        {
            var from = !resolved.Left.IsNull ? resolved.Left : resolved.Right;
            if (!from.IsNull)
            {
                resolved.Parent = from.Parent;
                resolved.ParentSub = Store.ParentSubOf(from);
            }
        }
        else if (r.ParentId is { } p)
        {
            var parentItem = Store.Find(p.Client, p.Clock);
            switch (parentItem.Kind)
            {
                case ContentKind.Gc:
                case ContentKind.Deleted:
                    resolved.Parent = null;   // the parent type was collected (Yjs: GC, or ContentDeleted whose .type is undefined)
                    break;
                case ContentKind.Type:
                    if (Store.GetPayload(parentItem) is not Branch type) return Resolution.Rejected;
                    resolved.Parent = type;
                    resolved.ParentSub = r.ParentSub;
                    break;
                default:
                    return Resolution.Rejected;   // the parent is not a type (STR-71, ADR 0005)
            }
        }
        else
        {
            resolved.Parent = RootBranch(r.ParentRoot!);
            resolved.ParentSub = r.ParentSub;
        }

        return Resolution.Ready;
    }

    private bool IsMissing(ItemId? dep, ulong client, PendingDryRun? dry, out ulong missingClient, out long missingClock)
    {
        missingClient = 0;
        missingClock = 0;
        if (dep is not { } d || d.Client == client || d.Clock < StateOf(d.Client, dry)) return false;
        missingClient = d.Client;
        missingClock = d.Clock;
        return true;
    }

    /// <summary>A dependency on the struct's own client at or after its own clock: contiguity cannot satisfy it, so the struct is malformed.</summary>
    private static bool SelfDependencyAhead(ItemId? dep, in StructRef r) => dep is { } d && d.Client == r.Client && d.Clock >= r.Clock;

    // ------------------------------------------------------------------ integrate (D1 to D9)

    /// <summary>Integrates a decoded struct from <paramref name="offset"/> on, with the neighbours and parent <see cref="Resolve"/> found.</summary>
    private void Integrate(Transaction tx, StructRef r, long offset, Resolved resolved)
    {
        if (r.Kind == RefKind.Gc)
        {
            Store.AddGc(r.Client, r.Clock + offset, r.Length - offset);
            return;
        }

        var left = resolved.Left;
        var origin = resolved.Origin;
        if (offset > 0)
        {
            r = r.Slice(offset);
            left = Store.GetItemCleanEnd(r.Client, r.Clock - 1);
            origin = new ItemId(r.Client, r.Clock - 1);
        }

        if (resolved.Parent is not { } parent)
        {
            Store.AddGc(r.Client, r.Clock, r.Length);   // the parent is gone: a GC of the full length (STR-24)
            return;
        }

        var right = resolved.Right;
        ItemId? rightOrigin = right.IsNull ? null : Store.IdOf(right);
        var content = r.Content switch
        {
            ContentKind.String => Store.AppendText(r.Text),
            ContentKind.Any or ContentKind.Json => Store.AppendValues(r.Values.Span),
            ContentKind.Deleted => 0,
            _ => Store.AddPayload(r.Payload ?? throw new YInternalException($"A {r.Content} struct has no payload.")),
        };
        var keyed = resolved.ParentSub is not null;
        var item = Store.CreateItem(r.Client, r.Clock, origin, rightOrigin, parent, resolved.ParentSub, keyed, r.Content, content, r.Length);
        IntegrateItem(tx, item, left, right);
    }

    /// <summary>
    /// Yjs <c>Item.integrate</c> once the item exists and has a parent: the YATA conflict scan picks the left neighbour, then the item is linked,
    /// counted and stored, its content integrated, its parent recorded as changed, and it is deleted at once when its parent is deleted or it
    /// lost a map-key race.
    /// </summary>
    private void IntegrateItem(Transaction tx, ItemRef item, ItemRef left, ItemRef right)
    {
        var parent = item.Parent ?? throw new YInternalException("An item without a parent cannot be integrated.");
        var keyed = item.HasParentSub;
        if ((left.IsNull && (right.IsNull || !right.Left.IsNull)) || (!left.IsNull && left.Right != right))
            left = ScanConflicts(item, parent, keyed, left, right);

        Store.Link(item, left);
        right = item.Right;
        if (keyed && right.IsNull && !left.IsNull) DeleteDeep(tx, left);   // the new current value replaces the old one (last writer wins)

        switch (item.Kind)
        {
            case ContentKind.Deleted:
                Store.Delete(item, tx.DeleteRanges);   // ContentDeleted.integrate: marked deleted and in the delete set, never counted (STR-25)
                break;
            case ContentKind.Type:
                var type = Store.GetPayload(item) as Branch ?? throw new YInternalException("A type item's payload is not a branch.");
                StructStore.Bind(type, item);
                HookTypeIntegrated(tx, type);
                break;
            case ContentKind.Format:
                HookFormatIntegrated(tx, item);
                break;
            case ContentKind.Doc:
                HookSubdocIntegrated(tx, item);
                break;
        }

        var parentSub = keyed ? Store.ParentSubOf(item) : null;
        AddChangedType(tx, parent, parentSub);
        var parentItem = StructStore.ItemOf(parent);
        if ((!parentItem.IsNull && parentItem.IsDeleted) || (keyed && !right.IsNull)) DeleteDeep(tx, item);
    }

    /// <summary>The YATA scan (D3, D4): walks the conflict zone between the natural neighbours and returns the left neighbour to link after.</summary>
    private ItemRef ScanConflicts(ItemRef item, Branch parent, bool keyed, ItemRef left, ItemRef right)
    {
        ItemRef o;
        if (!left.IsNull) o = left.Right;
        else if (keyed) o = Store.FirstOfKey(parent, item);
        else o = StructStore.StartOf(parent);

        var conflicting = _conflicting ??= new HashSet<ItemRef>();
        var beforeOrigin = _beforeOrigin ??= new HashSet<ItemRef>();
        conflicting.Clear();
        beforeOrigin.Clear();
        var client = Store.ClientIdOf(item);
        while (!o.IsNull && o != right)
        {
            beforeOrigin.Add(o);
            conflicting.Add(o);
            if (Store.SameOrigin(item, o))
            {
                // case 1: same origin; the lower client id goes left
                if (Store.ClientIdOf(o) < client)
                {
                    left = o;
                    conflicting.Clear();
                }
                else if (Store.SameRightOrigin(item, o))
                {
                    break;   // same integration points: this goes before o
                }
            }
            else if (Store.HasOrigin(o) && beforeOrigin.Contains(Store.OriginItem(o)))
            {
                // case 2: o's origin lies in the scanned zone
                if (!conflicting.Contains(Store.OriginItem(o)))
                {
                    left = o;
                    conflicting.Clear();
                }
            }
            else
            {
                break;
            }

            o = o.Right;
        }

        conflicting.Clear();
        beforeOrigin.Clear();
        return left;
    }

    /// <summary>
    /// Yjs <c>addChangedTypeToTransaction</c>: records the change when the type is a root, or its item existed before the transaction and is
    /// not deleted. Yjs has an object for every type, so a nested type read from an update that nobody has read yet gets its object here (its
    /// observers cannot exist yet, but deep observers above it, the formatting cleanup and the changed types must see it). A root that no
    /// object owns was only named by an update and has no observers to call: it is skipped.
    /// </summary>
    private static void AddChangedType(Transaction tx, Branch parent, string? parentSub)
    {
        var item = StructStore.ItemOf(parent);
        if (!item.IsNull && (item.IsDeleted || item.Clock >= tx.BeforeClock(tx.Doc.Store.ClientIdOf(item)))) return;
        YType type;
        if (parent.Owner is YType owner) type = owner;
        else if (!item.IsNull) type = YType.Of(parent, tx.Doc);
        else return;

        var state = tx.State;
        var changed = state.Changed;
        if (!changed.TryGetValue(type, out var keys))
        {
            changed[type] = keys = state.RentKeySet();
            state.ChangedOrder.Add(type);
        }

        keys.Add(parentSub);
    }

    // ------------------------------------------------------------------ delete (E1, H2)

    /// <summary>One frame of the iterative nested-type delete: the type, its next list child, and its map values once the list is done.</summary>
    private struct DeleteFrame
    {
        public Branch Type;
        public ItemRef Next;
        public List<ItemRef>? MapItems;
        public int MapIndex;
    }

    /// <summary>
    /// Yjs <c>Item.delete</c> with <c>ContentType.delete</c>: deletes the item, then every live child of a deleted type depth first (old
    /// tombstones are queued for merging instead), in the order the Yjs recursion takes, without recursing on the document's depth.
    /// </summary>
    private void DeleteDeep(Transaction tx, ItemRef item)
    {
        var type = DeleteOne(tx, item);
        if (type is null) return;

        var stack = new List<DeleteFrame> { new() { Type = type, Next = StructStore.StartOf(type) } };
        while (stack.Count > 0)
        {
            var top = stack.Count - 1;
            var frame = stack[top];
            ItemRef child;
            if (!frame.Next.IsNull)
            {
                child = frame.Next;
                frame.Next = child.Right;
            }
            else
            {
                if (frame.MapItems is null)
                {
                    frame.MapItems = new List<ItemRef>();
                    StructStore.CopyMapItems(frame.Type, frame.MapItems);
                }

                if (frame.MapIndex >= frame.MapItems.Count)
                {
                    stack.RemoveAt(top);
                    if (frame.Type.Owner is YType owner) tx.Changed.Remove(owner);   // no events for a deleted type
                    continue;
                }

                child = frame.MapItems[frame.MapIndex++];
            }

            stack[top] = frame;
            if (!child.IsDeleted)
            {
                if (DeleteOne(tx, child) is { } nested) stack.Add(new DeleteFrame { Type = nested, Next = StructStore.StartOf(nested) });
            }
            else if (child.Clock < tx.BeforeClock(Store.ClientIdOf(child)))
            {
                Store.AddMergeStruct(child);
            }
        }
    }

    /// <summary>Deletes one item (E1). Returns the type to descend into when the item held a nested type, else null.</summary>
    private Branch? DeleteOne(Transaction tx, ItemRef item)
    {
        if (!Store.Delete(item, tx.DeleteRanges)) return null;
        var parent = item.Parent;
        if (parent is not null) AddChangedType(tx, parent, item.HasParentSub ? Store.ParentSubOf(item) : null);
        switch (item.Kind)
        {
            case ContentKind.Type:
                return Store.GetPayload(item) as Branch;
            case ContentKind.Doc:
                HookSubdocDeleted(tx, item);
                break;
        }

        return null;
    }
}
