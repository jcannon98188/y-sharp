#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using YSharp.Lib0;

namespace YSharp;

/// <summary>
/// How much of each client's history a document holds: for every client the clock of the next struct it expects. Immutable. Clients are
/// listed in descending id order, as the V1 encoding writes them (ENC-09); ids and clocks are at most 2^53-1 (ADR 0008, Q12).
/// </summary>
public sealed class StateVector
{
    private readonly KeyValuePair<ulong, long>[] entries;
    private readonly ReadOnlyCollection<KeyValuePair<ulong, long>> view;

    private StateVector(KeyValuePair<ulong, long>[] entries)
    {
        this.entries = entries;
        view = new ReadOnlyCollection<KeyValuePair<ulong, long>>(entries);
    }

    /// <summary>Decodes a V1 state vector (<c>varuint count, then varuint client and varuint clock per entry</c>). Throws <see cref="YDecodeException"/> when it is malformed.</summary>
    public static StateVector Decode(ReadOnlySpan<byte> v1)
    {
        var status = TryDecode(v1, out var sv);
        return status.Ok ? sv! : throw new YDecodeException(status.Error);
    }

    /// <summary>
    /// The non-throwing <see cref="Decode"/>. Trailing bytes and overlong varints are accepted, as in JS; running out of bytes, a value above
    /// 2^53-1 and a count the remaining bytes cannot hold are Malformed. A client listed twice keeps its last clock.
    /// </summary>
    public static YStatus TryDecode(ReadOnlySpan<byte> v1, out StateVector? stateVector)
    {
        stateVector = null;
        var reader = new Lib0Reader(v1);
        if (!reader.TryReadVarUint(out var count)) return new YStatus(reader.Error);
        // an entry is at least two bytes, so a larger claim can only be a lie; checked before anything is sized from it
        if (count > (ulong)reader.Remaining / 2) return new YStatus(YError.Malformed);
        var read = new KeyValuePair<ulong, long>[(int)count];
        for (var i = 0; i < read.Length; i++)
        {
            if (!reader.TryReadVarUint(out var client) || !reader.TryReadVarUint(out var clock)) return new YStatus(reader.Error);
            read[i] = new KeyValuePair<ulong, long>(client, (long)clock);
        }
        stateVector = Normalise(read);
        return default;
    }

    /// <summary>The state vector of <paramref name="doc"/> as it is now.</summary>
    public static StateVector Of(Doc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var writer = new ArrayBufferWriter<byte>();
        doc.EncodeStateVector(writer);
        return Decode(writer.WrittenSpan);
    }

    /// <summary>A state vector from entries in any order. A client listed twice keeps its last clock.</summary>
    internal static StateVector Create(ReadOnlySpan<KeyValuePair<ulong, long>> clients) => Normalise(clients.ToArray());

    /// <summary>The clock of <paramref name="client"/>, or 0 when the state vector has no entry for it.</summary>
    public long this[ulong client] => TryGetClock(client, out var clock) ? clock : 0;

    /// <summary>The clock of <paramref name="client"/>; false when the state vector has no entry for it (an entry can hold clock 0).</summary>
    internal bool TryGetClock(ulong client, out long clock)
    {
        // entries are sorted by descending client id
        int lo = 0, hi = entries.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var id = entries[mid].Key;
            if (id == client)
            {
                clock = entries[mid].Value;
                return true;
            }
            if (id > client) lo = mid + 1; else hi = mid - 1;
        }
        clock = 0;
        return false;
    }

    /// <summary>Every client with an entry, by descending id.</summary>
    public IEnumerable<KeyValuePair<ulong, long>> Clients => view;

    /// <summary>The number of clients with an entry.</summary>
    internal int Count => entries.Length;

    /// <summary>Writes the V1 encoding: the count, then each client by descending id with its clock.</summary>
    public void Encode(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        using var w = new Lib0Writer(1 + entries.Length * 4);
        w.WriteVarUint((uint)entries.Length);
        foreach (var e in entries)
        {
            w.WriteVarUint(e.Key);
            w.WriteVarUint((ulong)e.Value);
        }
        w.CopyTo(writer);
    }

    /// <summary>Sorts by descending client and keeps the last entry of a repeated client. Takes ownership of <paramref name="list"/>.</summary>
    private static StateVector Normalise(KeyValuePair<ulong, long>[] list)
    {
        var order = new int[list.Length];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        // descending client, then input order, so the last duplicate comes last of its run
        System.Array.Sort(order, (x, y) =>
        {
            var c = list[y].Key.CompareTo(list[x].Key);
            return c != 0 ? c : x.CompareTo(y);
        });
        var result = new List<KeyValuePair<ulong, long>>(list.Length);
        for (var i = 0; i < order.Length; i++)
        {
            var entry = list[order[i]];
            if (result.Count > 0 && result[^1].Key == entry.Key) result[^1] = entry;
            else result.Add(entry);
        }
        return new StateVector(result.ToArray());
    }
}
