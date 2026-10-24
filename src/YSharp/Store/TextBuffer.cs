using System;
using System.Text;

namespace YSharp.Store;

/// <summary>
/// The document's string content: one append-only address space of UTF-16 code units, stored in chunks of at most
/// <see cref="ChunkLimits.ChunkBytes"/> (ADR 0018). An item refers to its text by address and length, so splitting an item is address arithmetic:
/// it allocates nothing and copies nothing. An append continues across a chunk boundary, so a slice may span chunks; readers copy it out.
/// <para>
/// Every chunk counts the code units of live slices in it. <see cref="Free"/> subtracts a slice when its item's content is garbage-collected, and a
/// chunk whose count reaches zero is released for the GC to reclaim, unless it is the chunk being appended to (ADR 0023, releasing deleted payloads).
/// A released chunk keeps its index, so addresses stay stable, and reading from it fails.
/// </para>
/// <para>
/// The first chunk starts small and doubles up to the chunk size, so a document with a short text does not hold a full 64 KB chunk.
/// </para>
/// </summary>
internal sealed class TextBuffer
{
    public const int ChunkShift = 15;
    public const int ChunkChars = 1 << ChunkShift;
    private const int ChunkMask = ChunkChars - 1;
    private const int FirstChunkChars = 64;
    private const int SmallChunkChars = 4096;

    private readonly ChunkedList<char[]?> _chunks = new();
    private readonly ChunkedList<int> _live = new();
    private int _end;

    /// <summary>Number of chunk indexes used, released ones included.</summary>
    public int ChunkCount => _chunks.Count;

    /// <summary>The address the next append starts at.</summary>
    public int End => _end;

    /// <summary>Largest chunk allocated, in bytes; never above <see cref="ChunkLimits.ChunkBytes"/>.</summary>
    public long MaxChunkBytes
    {
        get
        {
            long max = _chunks.MaxBlockBytes;
            for (var i = 0; i < _chunks.Count; i++) max = Math.Max(max, (long)(_chunks[i]?.Length ?? 0) * sizeof(char));
            return max;
        }
    }

    /// <summary>Number of chunks released so far.</summary>
    public int ReleasedChunks
    {
        get
        {
            var n = 0;
            for (var i = 0; i < _chunks.Count; i++) if (_chunks[i] is null) n++;
            return n;
        }
    }

    public bool IsReleased(int chunk) => _chunks[chunk] is null;

    /// <summary>Live code units in <paramref name="chunk"/>.</summary>
    public int LiveIn(int chunk) => _live[chunk];

    /// <summary>Copies <paramref name="text"/> to the end of the buffer and returns its address. The copied units count as live.</summary>
    public int Append(ReadOnlySpan<char> text)
    {
        if ((long)_end + text.Length > int.MaxValue) throw new YInternalException("The document's text address space is exhausted.");
        var address = _end;
        while (!text.IsEmpty)
        {
            var chunk = _end >> ChunkShift;
            var offset = _end & ChunkMask;
            var take = Math.Min(text.Length, ChunkChars - offset);
            var data = Writable(chunk, offset + take);
            text[..take].CopyTo(data.AsSpan(offset));
            _live[chunk] += take;
            text = text[take..];
            _end += take;
        }

        return address;
    }

    /// <summary>The slice at <paramref name="address"/> as one span, when it lies in one chunk; otherwise false.</summary>
    public bool TryGetSpan(int address, int length, out ReadOnlySpan<char> span)
    {
        var chunk = address >> ChunkShift;
        var offset = address & ChunkMask;
        if (length == 0)
        {
            span = default;
            return true;
        }

        if (offset + length > ChunkChars)
        {
            span = default;
            return false;
        }

        span = Data(chunk).AsSpan(offset, length);
        return true;
    }

    /// <summary>Copies the slice at <paramref name="address"/> into <paramref name="destination"/>.</summary>
    public void CopyTo(int address, int length, Span<char> destination)
    {
        if (destination.Length < length) throw new ArgumentException("destination too short", nameof(destination));
        while (length > 0)
        {
            var chunk = address >> ChunkShift;
            var offset = address & ChunkMask;
            var take = Math.Min(length, ChunkChars - offset);
            Data(chunk).AsSpan(offset, take).CopyTo(destination);
            destination = destination[take..];
            address += take;
            length -= take;
        }
    }

    public string GetString(int address, int length)
    {
        if (TryGetSpan(address, length, out var span)) return span.ToString();
        return string.Create(length, (this, address), static (dest, s) => s.Item1.CopyTo(s.address, dest.Length, dest));
    }

    public void AppendTo(StringBuilder sb, int address, int length)
    {
        while (length > 0)
        {
            var chunk = address >> ChunkShift;
            var offset = address & ChunkMask;
            var take = Math.Min(length, ChunkChars - offset);
            sb.Append(Data(chunk).AsSpan(offset, take));
            address += take;
            length -= take;
        }
    }

    public char CharAt(int address) => Data(address >> ChunkShift)[address & ChunkMask];

    /// <summary>Overwrites one code unit in place (the surrogate-pair repair of a string split). The caller owns the slice that holds it.</summary>
    public void SetChar(int address, char value) => Data(address >> ChunkShift)[address & ChunkMask] = value;

    /// <summary>Marks the slice at <paramref name="address"/> dead, and releases every chunk left with no live code units except the last one.</summary>
    public void Free(int address, int length)
    {
        while (length > 0)
        {
            var chunk = address >> ChunkShift;
            var offset = address & ChunkMask;
            var take = Math.Min(length, ChunkChars - offset);
            var live = _live[chunk] - take;
            if (live < 0) throw new YInternalException("A text chunk freed more code units than it holds.");
            _live[chunk] = live;
            if (live == 0 && chunk < _chunks.Count - 1) _chunks[chunk] = null;
            address += take;
            length -= take;
        }
    }

    private char[] Data(int chunk) => _chunks[chunk] ?? throw new YInternalException("Read from a released text chunk.");

    /// <summary>The chunk to write <paramref name="needed"/> units into, growing the last chunk or starting a new one.</summary>
    private char[] Writable(int chunk, int needed)
    {
        if (chunk == _chunks.Count)
        {
            // Starting a new chunk: the previous one is no longer appended to, so release it if nothing in it is live.
            var previous = chunk - 1;
            if (previous >= 0 && _live[previous] == 0) _chunks[previous] = null;
            // Only the first chunk starts small: a document whose text has filled one chunk takes full chunks from then on.
            _chunks.Add(new char[chunk == 0 ? Math.Max(FirstChunkChars, RoundUp(needed)) : ChunkChars]);
            _live.Add(0);
        }

        var data = _chunks[chunk]!;
        if (data.Length < needed)
        {
            // Double while the text is small; past SmallChunkChars take the full chunk, so a growing text stops copying early.
            var size = Math.Max(data.Length * 2, RoundUp(needed));
            var grown = new char[size > SmallChunkChars ? ChunkChars : size];
            data.AsSpan().CopyTo(grown);
            _chunks[chunk] = grown;
            data = grown;
        }

        return data;
    }

    private static int RoundUp(int n) => (int)Math.Min(ChunkChars, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, n)));
}
