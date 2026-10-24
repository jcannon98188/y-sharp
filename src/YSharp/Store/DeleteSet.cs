#nullable enable
using System;
using System.Buffers;
using YSharp.Store;

namespace YSharp;

/// <summary>A set of deleted id ranges (Yjs <c>DeleteSet</c>), as a transaction, a snapshot or an undo stack item exposes it.</summary>
public sealed class DeleteSet
{
    private readonly DeleteRanges ranges;

    /// <summary>An empty set. Kept so the public surface of the stub is unchanged.</summary>
    public DeleteSet() : this(new DeleteRanges()) { }

    internal DeleteSet(DeleteRanges ranges) => this.ranges = ranges;

    internal DeleteRanges Ranges => ranges;

    /// <summary>Whether <paramref name="id"/> lies in one of the deleted ranges (Yjs <c>isDeleted</c>).</summary>
    public bool Contains(ItemId id) => ranges.Contains(id.Client, id.Clock);

    // The delete-set encoders belong to the codecs (V1 in spec 14, V2 in spec 18); they implement this member.
    public void Encode(IBufferWriter<byte> writer, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
}
