#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using YSharp.Integration;
using YSharp.Store;

namespace YSharp;

// Item integration, pending structs, rejection and faulted documents (TASK-4.13, ADR 0005). The entry points other modules call:
//   IntegrateRemote / IntegrateUpdate  a decoded update (codecs, spec 14): Yjs readUpdateV2 without the decoding; TryIntegrateRemote
//                                      (Doc.PendingLimit.cs) checks MaxPendingBytes first
//   IntegrateLocal                     a new local item the types created with StructStore.CreateLocalItem: Yjs item.integrate(tx, 0)
//   DeleteItem                         Yjs item.delete(tx), nested types included, iteratively
//   ThrowIfFaulted / IsFaulted         the faulted-document state; every entry point above refuses on a faulted document
//   RootBranch / RootNameOf            the root types by name (Yjs doc.share), created on demand as Yjs doc.get does
//   Pending / PendingDeletes           the pending state, for the encoder (EncodeStateAsUpdate includes it) and HasPending
public sealed partial class Doc
{
    private bool _faulted;
    private bool _integrityBroken;
    private Dictionary<string, Branch>? _roots;
    private PendingStructs? _pending;
    private DeleteRanges? _pendingDeletes;

    // ------------------------------------------------------------------ faults (ADR 0005, ERR-20)

    /// Test-only fault hook (ADR 0005, ERR-20), reached through InternalsVisibleTo: marks the struct store as failing its integrity check without
    /// any public API call, as a broken invariant (for example a gap in the local client's struct list) would. Reads are unaffected. The next
    /// operation that changes the store (integrating, deleting or applying) runs the check, throws YInternalException and faults the document.
    /// No public input can cause this by design (ERR-21).
    internal void InjectInvariantViolation() => _integrityBroken = true;

    /// <summary>Throws <see cref="YInternalException"/> when the document is faulted: it refuses every change (ADR 0005).</summary>
    internal void ThrowIfFaulted()
    {
        if (Volatile.Read(ref _faulted)) throw new YInternalException("The document is faulted: an internal invariant was violated, so it refuses changes.");
    }

    /// <summary>Marks the document faulted, for good. Returns false so it can run as an exception filter without catching anything.</summary>
    internal bool MarkFaulted()
    {
        Volatile.Write(ref _faulted, true);
        return false;
    }

    /// <summary>The check that precedes every store change: refuses on a faulted document, and detects a broken store.</summary>
    private void BeginChange()
    {
        ThrowIfFaulted();
        if (_integrityBroken) throw new YInternalException("The struct store failed its integrity check.");
    }

    // ------------------------------------------------------------------ root types

    /// <summary>The storage of root type <paramref name="name"/>, created on first use (Yjs <c>doc.get(name)</c> creates an untyped placeholder).</summary>
    internal Branch RootBranch(string name)
    {
        lock (_rootGate) return RootBranchLocked(name);
    }

    /// <summary>Guards the root table: roots are named by the user's Get calls and by updates being integrated.</summary>
    private readonly object _rootGate = new();

    private Branch RootBranchLocked(string name)
    {
        _roots ??= new Dictionary<string, Branch>(StringComparer.Ordinal);
        if (!_roots.TryGetValue(name, out var branch)) _roots[name] = branch = new Branch();
        return branch;
    }

    internal bool TryGetRootBranch(string name, out Branch branch)
    {
        branch = null!;
        return _roots is not null && _roots.TryGetValue(name, out branch!);
    }

    /// <summary>The name of a root branch (Yjs <c>findRootTypeKey</c>), or null when <paramref name="branch"/> is not a root.</summary>
    internal string? RootNameOf(Branch branch)
    {
        if (_roots is null) return null;
        foreach (var (name, b) in _roots)
            if (ReferenceEquals(b, branch)) return name;
        return null;
    }

    /// <summary>Every root branch by name, in creation order.</summary>
    internal IEnumerable<KeyValuePair<string, Branch>> RootBranches => _roots ?? (IEnumerable<KeyValuePair<string, Branch>>)Array.Empty<KeyValuePair<string, Branch>>();

    // ------------------------------------------------------------------ pending state (STR-63, STR-64, STR-65)

    /// <summary>The structs waiting for missing dependencies (Yjs <c>store.pendingStructs</c>), or null.</summary>
    internal PendingStructs? Pending => _pending;

    /// <summary>The delete ranges waiting for their structs (Yjs <c>store.pendingDs</c>), or null.</summary>
    internal DeleteRanges? PendingDeletes => _pendingDeletes;

    // ------------------------------------------------------------------ entry points

    /// <summary>
    /// Integrates a decoded update in a remote transaction of its own, or in the calling thread's open one (Yjs <c>applyUpdate</c> without the
    /// decoding), with no pending-state limit (<see cref="TryIntegrateRemote"/> checks one). Returns how many structs were rejected (ADR 0005).
    /// Throws <see cref="YInternalException"/> on a faulted document.
    /// </summary>
    internal int IntegrateRemote(UpdateRefs update, object? origin = null)
    {
        ThrowIfFaulted();
        var h = EnterWrite(origin, local: false);
        try
        {
            return IntegrateUpdate(h.Tx, update);
        }
        finally
        {
            Leave(h);
        }
    }

    /// <summary>
    /// Yjs <c>readUpdateV2</c> after decoding: integrates the structs, merges what cannot be integrated into the pending state, applies the
    /// delete set and the pending delete set, and retries the pending structs once when a missing dependency arrived. The caller's write
    /// transaction is marked remote, as Yjs forces <c>transaction.local = false</c>. Returns the number of rejected structs.
    /// </summary>
    internal int IntegrateUpdate(Transaction tx, UpdateRefs update)
    {
        try
        {
            BeginChange();
            tx.State.IsLocal = false;
            var rejected = 0;
            ReadUpdate(tx, update, ref rejected);
            return rejected;
        }
        catch (YInternalException) when (MarkFaulted())
        {
            throw;
        }
    }

    /// <summary>
    /// Integrates a new local item that the caller created with <see cref="StructStore.CreateLocalItem"/> (left and right already set) and whose
    /// content is stored: Yjs <c>item.integrate(transaction, 0)</c>.
    /// </summary>
    internal void IntegrateLocal(Transaction tx, ItemRef item)
    {
        try
        {
            BeginChange();
            IntegrateItem(tx, item, item.Left, item.Right);
        }
        catch (YInternalException) when (MarkFaulted())
        {
            throw;
        }
    }

    /// <summary>Yjs <c>item.delete(transaction)</c>: marks the item deleted and, for a nested type, everything in it. Iterative on depth.</summary>
    internal void DeleteItem(Transaction tx, ItemRef item)
    {
        try
        {
            BeginChange();
            DeleteDeep(tx, item);
        }
        catch (YInternalException) when (MarkFaulted())
        {
            throw;
        }
    }

    // ------------------------------------------------------------------ readUpdateV2

    private void ReadUpdate(Transaction tx, UpdateRefs update, ref int rejected)
    {
        var reach = ReachableDeleteClients(update, _pendingDeletes);
        var rest = IntegrateStructs(tx, update, ref rejected);
        var retry = false;
        var pending = _pending;
        if (pending is not null)
        {
            // something became applicable when a missing clock is now below the client's state: the store flags it as it happens
            retry = Store.AwaitedPassed;
            Debug.Assert(retry == AnyAwaitedPassed(pending.Missing, null), "The store's flag disagrees with the missing clocks.");
            if (rest is not null)
            {
                pending.MergeFrom(rest);
                if (AnyAwaitedPassed(rest.Missing, null)) Store.AwaitedPassed = true;
            }
        }
        else
        {
            SetPending(rest);
        }

        var dsRest = ApplyDeleteSet(tx, update.Deletes);
        SettlePendingDeletes(tx, null, reach, dsRest, ref _pendingDeletes, ref _pendingDeleteSize, change: true);
        _retiredClientIds = null;

        if (retry)
        {
            var again = _pending!.ToUpdate();
            SetPending(null);
            ReadUpdate(tx, again, ref rejected);
        }
    }

    /// <summary>Replaces the pending structs, and has the store watch their missing clocks for a retry (Yjs checks them on every update).</summary>
    private void SetPending(PendingStructs? pending)
    {
        _pending = pending;
        Store.Awaited = pending?.Missing;
        Store.AwaitedPassed = pending is not null && AnyAwaitedPassed(pending.Missing, null);
    }

    /// <summary>Whether a missing clock is below its client's state, in the store or, for a dry run, in its virtual state.</summary>
    private bool AnyAwaitedPassed(Dictionary<ulong, long> missing, PendingDryRun? dry)
    {
        foreach (var (client, clock) in missing)
            if (clock < StateOf(client, dry)) return true;
        return false;
    }

    /// <summary>The ranges of a delete set in the order Yjs <c>writeDeleteSet</c> writes them: clients descending, clocks ascending.</summary>
    private static List<DeleteRange> InWriteOrder(DeleteRanges ranges)
    {
        var list = new List<DeleteRange>(ranges.Count);
        var end = ranges.Count;
        while (end > 0)
        {
            var client = ranges[end - 1].Client;
            var start = end - 1;
            while (start > 0 && ranges[start - 1].Client == client) start--;
            for (var i = start; i < end; i++) list.Add(ranges[i]);
            end = start;
        }

        return list;
    }

    /// <summary>
    /// Yjs <c>readAndApplyDeleteSet</c> on decoded ranges: deletes the known part of every range, splitting the first and last struct where a
    /// range starts or ends inside a live item, and returns the unknown part (beyond the client's state), or null when everything applied.
    /// </summary>
    private DeleteRanges? ApplyDeleteSet(Transaction tx, List<DeleteRange> ranges)
    {
        DeleteRanges? unapplied = null;
        foreach (var range in ranges)
        {
            var client = range.Client;
            var clock = range.Clock;
            var end = range.End;
            var state = Store.GetState(client);
            if (clock >= state)
            {
                (unapplied ??= new DeleteRanges()).Add(client, clock, range.Length);
                continue;
            }

            if (state < end) (unapplied ??= new DeleteRanges()).Add(client, state, end - state);
            var s = Store.Find(client, clock);
            if (!s.IsDeleted && s.Clock < clock) s = Store.GetItemCleanStart(client, clock);
            while (true)
            {
                if (s.Clock >= end) break;
                if (!s.IsDeleted)
                {
                    if (end < s.Clock + s.Length) Store.GetItemCleanEnd(client, end - 1);
                    DeleteDeep(tx, s);
                }

                var next = s.Clock + s.Length;
                if (next >= state) break;
                s = Store.Find(client, next);
            }
        }

        return unapplied;
    }
}
