using System;
using System.Collections.Generic;

namespace YSharp.StoreSpike.Content;

/// <summary>
/// Append-only character storage in chunks of at most <see cref="ChunkLimits.ChunkBytes"/>. Items refer to it with <see cref="ContentSlice"/>
/// and never copy their text. A single append must fit in one chunk; the driver appends single characters and short keys only.
/// </summary>
public sealed class TextBuffer
{
    public const int ChunkChars = ChunkLimits.ChunkBytes / sizeof(char);

    private readonly ChunkedList<char[]> _chunks = new();
    private int _used;

    public int ChunkCount => _chunks.Count;

    /// <summary>Largest chunk allocated, in bytes; never above <see cref="ChunkLimits.ChunkBytes"/>.</summary>
    public long MaxChunkBytes => _chunks.MaxBlockBytes;

    public ContentSlice Append(ReadOnlySpan<char> text)
    {
        if (text.Length == 0 || text.Length > ChunkChars) throw new ArgumentOutOfRangeException(nameof(text), "a slice must fit in one chunk");
        if (_chunks.Count == 0 || _used + text.Length > ChunkChars)
        {
            _chunks.Add(new char[ChunkChars]);
            _used = 0;
        }

        var chunk = _chunks.Count - 1;
        text.CopyTo(_chunks[chunk].AsSpan(_used));
        var slice = new ContentSlice(chunk, _used, text.Length);
        _used += text.Length;
        return slice;
    }

    public ReadOnlySpan<char> Get(ContentSlice slice) => _chunks[slice.Chunk].AsSpan(slice.Start, slice.Length);

    /// <summary>
    /// Drops a chunk that no live slice refers to, so the GC can reclaim it (ADR 0018: deleted content releases its payload storage). The caller
    /// tracks which chunks are dead. The chunk being appended to is never released; its index stays reserved and reading a released chunk fails.
    /// </summary>
    public void Release(int chunk)
    {
        if (chunk == _chunks.Count - 1) throw new InvalidOperationException("the chunk being appended to cannot be released");
        _chunks[chunk] = Array.Empty<char>();
    }

    public bool IsReleased(int chunk) => _chunks[chunk].Length == 0;
}
