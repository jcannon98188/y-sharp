using System;

namespace YSharp.StoreSpike.Content;

/// <summary>One map payload: the key's text as a slice, and the index of its 8-byte value in the value store.</summary>
public readonly record struct MapPayloadRef(ContentSlice Key, int Value);

/// <summary>
/// Map payloads in the shared layer: keys are slices of a <see cref="TextBuffer"/>, values are 8-byte entries of a <see cref="ChunkedList{T}"/>.
/// Candidates own the key lookup table (key to payload index); this type stores the payloads only.
/// </summary>
public sealed class MapPayloads
{
    private readonly TextBuffer _keys = new();
    private readonly ChunkedList<double> _values = new();
    private readonly ChunkedList<MapPayloadRef> _payloads = new();

    public int Count => _payloads.Count;

    public long MaxChunkBytes => Math.Max(_keys.MaxChunkBytes, Math.Max(_values.MaxBlockBytes, _payloads.MaxBlockBytes));

    /// <summary>Stores a new payload and returns its index.</summary>
    public int Add(ReadOnlySpan<char> key, double value)
    {
        var slice = _keys.Append(key);
        var valueIndex = _values.Count;
        _values.Add(value);
        _payloads.Add(new MapPayloadRef(slice, valueIndex));
        return _payloads.Count - 1;
    }

    public ReadOnlySpan<char> KeyOf(int index) => _keys.Get(_payloads[index].Key);

    public double ValueOf(int index) => _values[_payloads[index].Value];

    public void SetValue(int index, double value) => _values[_payloads[index].Value] = value;
}
