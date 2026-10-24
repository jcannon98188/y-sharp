using System;
using YSharp.StoreSpike.Content;

namespace YSharp.StoreSpike.ObjectPerItem;

/// <summary>One client's structs, sorted by clock and contiguous (Yjs <c>StructStore.clients</c> entry). The list holds references in chunks of at most 64 KB.</summary>
public sealed class ClientStructs
{
    internal readonly ulong Id;
    internal readonly SmallStartList<Item> Structs = new();

    internal ClientStructs(ulong id) => Id = id;

    /// <summary>The next clock this client will use (Yjs <c>getState</c>).</summary>
    internal uint NextClock
    {
        get
        {
            if (Structs.Count == 0) return 0;
            var last = Structs[Structs.Count - 1];
            return last.Clock + (uint)last.Length;
        }
    }

    /// <summary>Yjs <c>findIndexSS</c>: pivoted binary search for the struct that contains <paramref name="clock"/>.</summary>
    internal int FindIndex(uint clock)
    {
        var left = 0;
        var right = Structs.Count - 1;
        var mid = Structs[right];
        if (mid.Clock == clock) return right;
        var lastClock = mid.Clock + (uint)mid.Length - 1;
        var midIndex = lastClock == 0 ? 0 : (int)Math.Min(right, (double)clock / lastClock * right);
        while (left <= right)
        {
            mid = Structs[midIndex];
            if (mid.Clock <= clock)
            {
                if (clock < mid.Clock + (uint)mid.Length) return midIndex;
                left = midIndex + 1;
            }
            else
            {
                right = midIndex - 1;
            }

            midIndex = (left + right) / 2;
        }

        throw new InvalidOperationException($"no struct holds clock {clock}");
    }
}

/// <summary>
/// The client table: 53-bit client ids (held as <see cref="ulong"/>, ADR 0018) mapped to a dense slot. Items store the slot, not the id, so an id
/// is 8 bytes inline. Slots are never reused. Both the slot list and the id-sorted index grow in chunks of at most 64 KB.
/// </summary>
public sealed class ClientTable
{
    public const ulong MaxClientId = (1UL << 53) - 1;

    private readonly SmallStartList<ClientStructs> _bySlot = new();
    private readonly SmallStartList<int> _sortedSlots = new();

    public int Count => _bySlot.Count;

    internal ClientStructs this[int slot] => _bySlot[slot];

    /// <summary>The slot of <paramref name="client"/>, adding it if it is new.</summary>
    internal int SlotOf(ulong client)
    {
        if (client > MaxClientId) throw new ArgumentOutOfRangeException(nameof(client), "client ids are 53-bit");
        var at = Search(client);
        if (at >= 0) return _sortedSlots[at];

        var slot = _bySlot.Count;
        _bySlot.Add(new ClientStructs(client));
        _sortedSlots.Insert(~at, slot);
        return slot;
    }

    /// <summary>The slot of <paramref name="client"/>, or -1 when the table has never seen it.</summary>
    internal int TryGetSlot(ulong client)
    {
        var at = Search(client);
        return at >= 0 ? _sortedSlots[at] : -1;
    }

    private int Search(ulong client)
    {
        int lo = 0, hi = _sortedSlots.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var id = _bySlot[_sortedSlots[mid]].Id;
            if (id == client) return mid;
            if (id < client) lo = mid + 1;
            else hi = mid - 1;
        }

        return ~lo;
    }

    internal long MaxChunkBytes
    {
        get
        {
            var max = Math.Max(_bySlot.MaxBlockBytes, _sortedSlots.MaxBlockBytes);
            for (var i = 0; i < _bySlot.Count; i++) max = Math.Max(max, _bySlot[i].Structs.MaxBlockBytes);
            return max;
        }
    }
}
