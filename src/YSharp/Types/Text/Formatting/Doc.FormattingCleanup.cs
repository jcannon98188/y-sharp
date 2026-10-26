#nullable enable
using System.Collections.Generic;
using YSharp.Store;

namespace YSharp;

// Yjs cleanupYTextAfterTransaction: after a remote change to a text with formatting, the format items it made redundant are deleted in a
// follow-up local transaction of the same round, so every peer converges on the same deletes as JS (YText._callObserver sets
// _needFormattingCleanup; cleanupTransactions runs it after afterTransaction).
public sealed partial class Doc
{
    partial void HookFormattingCleanup(Transaction tx)
    {
        if (!tx.IsLocal && ChangedFormattedText(tx)) CleanupFormattingAfter(tx);
    }

    /// <summary>YText._callObserver's condition: a changed text, not deleted, that has held a format item.</summary>
    private static bool ChangedFormattedText(Transaction tx)
    {
        var state = tx.State;
        if (state.ChangedOrNull is { } changed)
            foreach (var type in changed.Keys)
                if (NeedsFormattingCleanup(type.Branch)) return true;
        return false;
    }

    private static bool NeedsFormattingCleanup(Branch branch) =>
        branch.TypeRef is TypeRefs.Text or TypeRefs.XmlText && branch.HasFormatting && (branch.Item is null || !branch.Item.Deleted);

    private void CleanupFormattingAfter(Transaction tx)
    {
        var store = Store;
        // texts that get a full cleanup, in the order JS adds them to its Set
        var full = new List<Branch>();
        var fullSet = new HashSet<Branch>(ReferenceEqualityComparer.Instance);
        void AddFull(Branch b)
        {
            if (fullSet.Add(b)) full.Add(b);
        }

        // a format item the transaction added makes its text need a full cleanup
        var clients = store.Clients;
        for (var slot = 0; slot < clients.Count; slot++)
        {
            var client = clients[slot];
            var before = tx.BeforeClock(client.Id);
            var after = tx.AfterClock(client.Id);
            if (after == before) continue;
            // JS passes afterClock as the length (iterateStructs(transaction, structs, clock, afterClock, ...)), so the walk ends at
            // before + after, past structs a follow-up transaction added
            var end = before + after;
            var structs = client.Structs;
            for (var i = client.FindIndex(before); i < structs.Count; i++)
            {
                var item = new ItemRef(structs[i]);
                if (item.Clock >= end) break;
                if (!item.IsDeleted && !item.IsGc && item.Kind == ContentKind.Format) AddFull(item.Parent!);
            }
        }

        var h = EnterWrite(null, local: true);
        try
        {
            // Yjs iterateDeletedStructs walks the clients in the order the transaction first deleted from each (its Map order), not sorted:
            // a format deleted before content in that order turns the content's contextless cleanup into the full one (quill-medium)
            var ds = tx.State.DeleteRanges;
            var order = ds.ClientOrder!;
            for (var c = 0; c < order.Count; c++)
            {
                var client = order[c];
                for (var r = ds.FirstIndexOf(client); r < ds.Count && ds[r].Client == client; r++)
                {
                    var range = ds[r];
                    var count = store.StructCount(range.Client);
                    for (var si = store.FindIndex(range.Client, range.Clock); si < count; si++)
                    {
                        var item = store.StructAt(range.Client, si);
                        if (item.Clock >= range.End) break;
                        if (item.IsGc || item.Parent is not { } parent || !parent.HasFormatting || fullSet.Contains(parent)) continue;
                        if (item.Kind == ContentKind.Format) AddFull(parent);
                        else TypeText.CleanupContextlessFormattingGap(h.Tx, this, item);
                    }
                }
            }

            foreach (var text in full) TypeText.CleanupFormatting(h.Tx, this, text);
        }
        finally
        {
            Leave(h);
        }
    }
}
