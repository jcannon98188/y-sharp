using System;

namespace YSharp.Store;

/// <summary>
/// The document's map keys (Yjs <c>parentSub</c>). A key is stored once per (map, key) and referred to by its index; every item that ever holds the
/// key's value carries that index, so overwriting a key adds no key storage. Key text is kept contiguous in chunks of at most
/// <see cref="ChunkLimits.ChunkBytes"/>, so it can be read back as one span. A single key longer than a chunk gets a chunk of its own, the one
/// payload exception ADR 0018 allows. Keys are never released: a map keeps a deleted key's last item.
/// </summary>
internal sealed class KeyTable
{
    private const int ChunkChars = TextBuffer.ChunkChars;
    private const int FirstChunkChars = 64;

    private readonly struct KeySlice
    {
        public readonly int Chunk;
        public readonly int Start;
        public readonly int Length;

        public KeySlice(int chunk, int start, int length)
        {
            Chunk = chunk;
            Start = start;
            Length = length;
        }
    }

    private readonly ChunkedList<char[]> _chunks = new();
    private readonly ChunkedList<KeySlice> _keys = new();
    private int _current = -1;
    private int _used;

    public int Count => _keys.Count;

    public long MaxChunkBytes
    {
        get
        {
            long max = Math.Max(_chunks.MaxBlockBytes, _keys.MaxBlockBytes);
            for (var i = 0; i < _chunks.Count; i++) if (_chunks[i].Length <= ChunkChars) max = Math.Max(max, (long)_chunks[i].Length * sizeof(char));
            return max;
        }
    }

    /// <summary>Stores <paramref name="key"/> and returns its index.</summary>
    public int Add(ReadOnlySpan<char> key)
    {
        int chunk, start;
        if (key.Length > ChunkChars)
        {
            chunk = _chunks.Count;
            _chunks.Add(key.ToArray());
            start = 0;
        }
        else
        {
            if (_current < 0 || _used + key.Length > ChunkChars)
            {
                _current = _chunks.Count;
                _chunks.Add(new char[_current == 0 ? Math.Max(FirstChunkChars, key.Length) : ChunkChars]);
                _used = 0;
            }

            var data = _chunks[_current];
            if (data.Length < _used + key.Length)
            {
                var grown = new char[Math.Min(ChunkChars, Math.Max(data.Length * 2, _used + key.Length))];
                data.AsSpan(0, _used).CopyTo(grown);
                _chunks[_current] = grown;
                data = grown;
            }

            key.CopyTo(data.AsSpan(_used));
            chunk = _current;
            start = _used;
            _used += key.Length;
        }

        _keys.Add(new KeySlice(chunk, start, key.Length));
        return _keys.Count - 1;
    }

    public ReadOnlySpan<char> Get(int index)
    {
        var k = _keys[index];
        return _chunks[k.Chunk].AsSpan(k.Start, k.Length);
    }

    public int LengthOf(int index) => _keys[index].Length;
}
