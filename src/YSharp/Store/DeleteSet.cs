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

    /// <summary>Writes the set as an update's delete set (Yjs <c>writeDeleteSet</c>): clients descending, each client's ranges by clock.</summary>
    public void Encode(IBufferWriter<byte> writer, UpdateFormat format = UpdateFormat.V1)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (format == UpdateFormat.V2)
        {
            YSharp.Codecs.V2.UpdateCodecV2.EncodeDeleteSet(this, writer);
            return;
        }

        using var w = new YSharp.Lib0.Lib0Writer(16);
        var encoder = new YSharp.Codecs.V1.UpdateEncoderV1(w);
        YSharp.Codecs.UpdateWriter.WriteDeleteSet(ref encoder, ranges);
        w.CopyTo(writer);
    }
}
