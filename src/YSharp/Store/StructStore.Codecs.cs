using System;

namespace YSharp.Store;

// Read accessors for the codecs (spec 14, ADR 0021 additive): walking the clients in id order, a client's structs by index, and an item's text
// and key without making strings.
internal sealed partial class StructStore
{
    /// <summary>The number of clients the client table knows, including clients that only appear as an origin and hold no structs.</summary>
    public int ClientCount => Clients.Count;

    /// <summary>The id of the client at <paramref name="rank"/> in ascending id order.</summary>
    public ulong ClientIdAtRank(int rank) => Clients[Clients.SlotAtRank(rank)].Id;

    /// <summary>How many structs the client at <paramref name="rank"/> holds.</summary>
    public int StructCountAtRank(int rank) => Clients[Clients.SlotAtRank(rank)].Structs.Count;

    /// <summary>The next clock of the client at <paramref name="rank"/> (Yjs <c>getState</c>).</summary>
    public long StateAtRank(int rank) => Clients[Clients.SlotAtRank(rank)].NextClock;

    /// <summary>Struct <paramref name="index"/> of the client at <paramref name="rank"/>, in clock order.</summary>
    public ItemRef StructAtRank(int rank, int index) => new(Clients[Clients.SlotAtRank(rank)].Structs[index]);

    /// <summary>Yjs <c>findIndexSS</c> on the client at <paramref name="rank"/>.</summary>
    public int FindIndexAtRank(int rank, long clock) => Clients[Clients.SlotAtRank(rank)].FindIndex(clock);

    /// <summary>The text of a string item as one span, when it lies in one chunk of the text buffer; otherwise false.</summary>
    public bool TryGetTextSpan(ItemRef item, out ReadOnlySpan<char> text)
    {
        var i = Expect(item, ContentKind.String);
        return Text.TryGetSpan(i.Content, (int)i.Length, out text);
    }

    /// <summary>The map key of a keyed item without making a string; empty for a sequence item (check <see cref="ItemRef.HasParentSub"/>).</summary>
    public ReadOnlySpan<char> ParentSubKey(ItemRef item)
    {
        var key = item.Item!.ParentSub;
        return key < 0 ? default : Keys.Get(key);
    }
}
