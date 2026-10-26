#nullable enable
using System.Collections.Generic;
using YSharp.Store;

namespace YSharp;

// ---------------------------------------------------------------- undo

/// <summary>
/// Yjs <c>StackItem</c>: one undo or redo step, as the ids it inserted and the ids it deleted (UM-06). Merging a change into the item replaces
/// both sets, as in Yjs, so a set read earlier keeps what it held then.
/// </summary>
public sealed class StackItem
{
    internal StackItem(DeleteRanges deletions, DeleteRanges insertions)
    {
        DeletionRanges = deletions;
        InsertionRanges = insertions;
    }

    internal DeleteRanges DeletionRanges;
    internal DeleteRanges InsertionRanges;

    public DeleteSet Insertions => new(InsertionRanges);
    public DeleteSet Deletions => new(DeletionRanges);
    public Dictionary<object, object?> Meta { get; } = new();   // caller-owned, not protected (CC-20)
}
