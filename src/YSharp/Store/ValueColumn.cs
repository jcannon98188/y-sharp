using System;

namespace YSharp.Store;

/// <summary>
/// The document's Any and JSON values (Yjs <c>ContentAny.arr</c>, <c>ContentJSON.arr</c>): one 8-byte cell per value in chunks of at most
/// <see cref="ChunkLimits.ChunkBytes"/>, so an array of scalars costs 8 bytes per element (ADR 0023, the array gate). An item refers to its values
/// by start index and length, so splitting is index arithmetic and two runs that are next to each other merge without copying.
/// <para>
/// A cell holds the scalar bits of a Number, BigInt or Bool, and nothing for Null and Undefined. A string, byte string, array or object goes in a
/// side list of references and the cell holds its index there. The value kind of each cell is run-encoded, one run per change of kind, so a
/// homogeneous column has one run in all and mixed columns pay only where the kind changes (the ADR 0023 risk on mixed arrays).
/// </para>
/// <para>
/// Cells are not reclaimed piecewise (ADR 0023). <see cref="Free"/> drops the references that garbage-collected values held.
/// </para>
/// </summary>
internal sealed class ValueColumn
{
    private readonly struct KindRun
    {
        public readonly int Start;
        public readonly YValueKind Kind;

        public KindRun(int start, YValueKind kind)
        {
            Start = start;
            Kind = kind;
        }
    }

    private readonly ChunkedList<long> _cells = new();
    private readonly ChunkedList<KindRun> _runs = new();
    private readonly ChunkedList<object?> _refs = new();

    public int Count => _cells.Count;

    /// <summary>Number of kind runs; one for a homogeneous column.</summary>
    public int RunCount => _runs.Count;

    public long MaxChunkBytes => Math.Max(_cells.MaxBlockBytes, Math.Max(_runs.MaxBlockBytes, _refs.MaxBlockBytes));

    /// <summary>Appends one value and returns its index.</summary>
    public int Append(in YValue value)
    {
        var kind = value.Kind;
        if (_runs.Count == 0 || _runs.Last.Kind != kind) _runs.Add(new KindRun(_cells.Count, kind));

        long cell;
        if (IsReference(kind))
        {
            cell = _refs.Count;
            _refs.Add(value.Payload);
        }
        else
        {
            cell = value.Bits;
        }

        _cells.Add(cell);
        return _cells.Count - 1;
    }

    /// <summary>Appends <paramref name="values"/> as one run and returns the index of the first.</summary>
    public int AppendRange(ReadOnlySpan<YValue> values)
    {
        var start = _cells.Count;
        foreach (ref readonly var v in values) Append(v);
        return start;
    }

    /// <summary>Appends a copy of the run at <paramref name="start"/> and returns the copy's start (a merge of runs that are not adjacent).</summary>
    public int AppendCopy(int start, int length)
    {
        var copy = _cells.Count;
        for (var i = 0; i < length; i++) Append(Get(start + i));
        return copy;
    }

    public YValue Get(int index)
    {
        var cell = _cells[index];
        return KindAt(index) switch
        {
            YValueKind.Undefined => YValue.Undefined,
            YValueKind.Null => YValue.Null,
            YValueKind.Bool => YValue.FromBool(cell != 0),
            YValueKind.Number => YValue.FromNumber(BitConverter.Int64BitsToDouble(cell)),
            YValueKind.BigInt => YValue.BigInt(cell),
            YValueKind.String => (string)Ref(cell),
            YValueKind.Bytes => YValue.BytesOwned((byte[])Ref(cell)),
            YValueKind.Array => YValue.FromArrayOwned((YValue[])Ref(cell)),
            _ => YValue.FromObjectOwned((YObjectData)Ref(cell)),
        };
    }

    public void CopyTo(int start, Span<YValue> destination)
    {
        for (var i = 0; i < destination.Length; i++) destination[i] = Get(start + i);
    }

    /// <summary>Drops the references held by the run at <paramref name="start"/>, so garbage-collected strings, bytes and containers are reclaimed.</summary>
    public void Free(int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            if (IsReference(KindAt(i))) _refs[(int)_cells[i]] = null;
        }
    }

    private object Ref(long cell) => _refs[(int)cell] ?? throw new YInternalException("Read of a released value.");

    private YValueKind KindAt(int index)
    {
        int lo = 0, hi = _runs.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >>> 1;
            if (_runs[mid].Start <= index) lo = mid;
            else hi = mid - 1;
        }

        return _runs[lo].Kind;
    }

    private static bool IsReference(YValueKind kind) => kind is YValueKind.String or YValueKind.Bytes or YValueKind.Array or YValueKind.Object;
}
