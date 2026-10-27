#nullable enable
using System;
using System.Text;
using YSharp.Lib0;

namespace YSharp.Codecs.V2;

// The V2 column codecs (lib0/encoding.js and lib0/decoding.js, lib0 0.2.99). The writers are byte-exact ports. The readers are strict (ADR 0005):
// a column that runs out is Malformed (ENC-64, ENC-65, ENC-66), except that the last run of an Rle column repeats, because the Rle writer
// never writes the last run's count and valid Yjs output relies on it (ENC-67).

/// <summary>lib0 <c>RleDecoder</c> over uint8 values: a value, then (while bytes follow) varuint(repeats - 1); the last run repeats.</summary>
internal ref struct RleColumnReader
{
    private Lib0Reader reader;
    private byte value;
    private ulong count;
    private bool lastRun;

    public RleColumnReader(ReadOnlySpan<byte> column)
    {
        reader = new Lib0Reader(column);
        value = 0;
        count = 0;
        lastRun = false;
    }

    public bool Read(out byte v)
    {
        v = 0;
        if (count == 0 && !lastRun)
        {
            if (!reader.TryReadUint8(out value)) return false;
            if (reader.HasContent)
            {
                if (!reader.TryReadVarUint(out var repeats)) return false;
                count = repeats + 1;
            }
            else
            {
                lastRun = true;
            }
        }

        if (!lastRun) count--;
        v = value;
        return true;
    }
}

/// <summary>lib0 <c>UintOptRleDecoder</c>: varint(v) is one v; varint(-v) (including -0) is v repeated varuint + 2 times.</summary>
internal ref struct UintOptRleColumnReader
{
    private Lib0Reader reader;
    private ulong value;
    private ulong count;

    public UintOptRleColumnReader(ReadOnlySpan<byte> column)
    {
        reader = new Lib0Reader(column);
        value = 0;
        count = 0;
    }

    public bool Read(out ulong v)
    {
        v = 0;
        if (count == 0)
        {
            if (!reader.TryReadVarInt(out value, out var negative)) return false;
            count = 1;
            if (negative)
            {
                if (!reader.TryReadVarUint(out var repeats)) return false;
                count = repeats + 2;
            }
        }

        count--;
        v = value;
        return true;
    }
}

/// <summary>
/// lib0 <c>IntDiffOptRleDecoder</c>: varint(diff * 2 + hasCount), then varuint(count - 2) when hasCount; each read adds the diff to the running
/// value. The value can go negative; the caller decides what that means.
/// </summary>
internal ref struct IntDiffOptRleColumnReader
{
    /// <summary>
    /// The largest encoded magnitude: a diff between two clocks of 0 to 2^53-1, doubled, plus the run flag. A writer emits up to this for an origin
    /// clock near 2^53 (JS too, and its reader takes the bytes back), so the column allows past 2^53-1; the decoded clock keeps its own bound.
    /// </summary>
    private const ulong MaxEncoded = (1UL << 54) - 1;

    private Lib0Reader reader;
    private long value;
    private long diff;
    private ulong count;

    public IntDiffOptRleColumnReader(ReadOnlySpan<byte> column)
    {
        reader = new Lib0Reader(column);
        value = 0;
        diff = 0;
        count = 0;
    }

    public bool Read(out long v)
    {
        v = 0;
        if (count == 0)
        {
            if (!reader.TryReadVarInt(out var magnitude, out var negative, MaxEncoded)) return false;
            var encoded = negative ? -(long)magnitude : (long)magnitude;
            diff = encoded >> 1;   // Math.floor(encoded / 2)
            count = 1;
            if ((magnitude & 1) != 0)
            {
                if (!reader.TryReadVarUint(out var repeats)) return false;
                count = repeats + 2;
            }
        }

        value += diff;
        count--;
        v = value;
        return true;
    }
}

/// <summary>
/// lib0 <c>StringDecoder</c>: every string of the update joined into one varstring, then a <c>UintOptRle</c> column of their UTF-16 lengths. A
/// length past the end of the joined string is Malformed (ENC-68; JS slices short).
/// </summary>
internal ref struct StringColumnReader
{
    private readonly string text;
    private int position;
    private UintOptRleColumnReader lengths;

    private StringColumnReader(string text, ReadOnlySpan<byte> lengths)
    {
        this.text = text;
        position = 0;
        this.lengths = new UintOptRleColumnReader(lengths);
    }

    /// <summary>Reads the joined string; an empty column fails, as <c>readVarString</c> does in JS.</summary>
    public static bool TryCreate(ReadOnlySpan<byte> column, out StringColumnReader strings)
    {
        strings = default;
        var reader = new Lib0Reader(column);
        if (!reader.TryReadVarString(out var text)) return false;
        strings = new StringColumnReader(text, column[reader.Position..]);
        return true;
    }

    /// <summary>UTF-16 code units of the joined string not yet read.</summary>
    public readonly int CharsLeft => text is null ? 0 : text.Length - position;

    public bool Read(out string value)
    {
        value = "";
        if (text is null || !lengths.Read(out var length) || length > (ulong)(text.Length - position)) return false;
        value = text.Substring(position, (int)length);
        position += (int)length;
        return true;
    }
}

/// <summary>lib0 <c>RleEncoder</c> with <c>writeUint8</c>: the count of the last run is never written.</summary>
internal sealed class RleColumnWriter : IDisposable
{
    private readonly Lib0Writer writer = new(16);
    private byte value;
    private ulong count;

    public Lib0Writer Bytes => writer;

    public void Write(byte v)
    {
        if (count > 0 && value == v)
        {
            count++;
            return;
        }

        if (count > 0) writer.WriteVarUint(count - 1);
        count = 1;
        writer.WriteUint8(v);
        value = v;
    }

    public void Reset()
    {
        writer.Reset();
        count = 0;
    }

    public void Dispose() => writer.Dispose();
}

/// <summary>lib0 <c>UintOptRleEncoder</c>.</summary>
internal sealed class UintOptRleColumnWriter : IDisposable
{
    private readonly Lib0Writer writer = new(16);
    private ulong value;
    private ulong count;

    public void Write(ulong v)
    {
        if (value == v)
        {
            count++;
            return;
        }

        Flush();
        count = 1;
        value = v;
    }

    /// <summary>Writes the open run and returns the column; lib0's <c>toUint8Array</c>.</summary>
    public ReadOnlySpan<byte> Finish()
    {
        Flush();
        count = 0;
        return writer.WrittenSpan;
    }

    private void Flush()
    {
        if (count == 0) return;
        // a run is written as -value, and -0 is its own varint (40)
        writer.WriteVarInt(value, negative: count > 1);
        if (count > 1) writer.WriteVarUint(count - 2);
    }

    public void Reset()
    {
        writer.Reset();
        value = 0;
        count = 0;
    }

    public void Dispose() => writer.Dispose();
}

/// <summary>lib0 <c>IntDiffOptRleEncoder</c>. The first write of 0 continues the initial empty run with diff 0, as in JS.</summary>
internal sealed class IntDiffOptRleColumnWriter : IDisposable
{
    private readonly Lib0Writer writer = new(16);
    private long value;
    private long diff;
    private ulong count;

    public void Write(long v)
    {
        if (diff == v - value)
        {
            value = v;
            count++;
            return;
        }

        Flush();
        count = 1;
        diff = v - value;
        value = v;
    }

    public ReadOnlySpan<byte> Finish()
    {
        Flush();
        count = 0;
        return writer.WrittenSpan;
    }

    private void Flush()
    {
        if (count == 0) return;
        writer.WriteVarInt(diff * 2 + (count == 1 ? 0 : 1));
        if (count > 1) writer.WriteVarUint(count - 2);
    }

    public void Reset()
    {
        writer.Reset();
        value = 0;
        diff = 0;
        count = 0;
    }

    public void Dispose() => writer.Dispose();
}

/// <summary>
/// lib0 <c>StringEncoder</c>: the strings joined in UTF-16 (so a surrogate pair split across two strings is whole again) and written as one
/// varstring, then their UTF-16 lengths as a <c>UintOptRle</c> column.
/// </summary>
internal sealed class StringColumnWriter : IDisposable
{
    private readonly StringBuilder text = new();
    private readonly UintOptRleColumnWriter lengths = new();
    private readonly Lib0Writer writer = new(16);

    public void Write(ReadOnlySpan<char> value)
    {
        text.Append(value);
        lengths.Write((ulong)value.Length);
    }

    public ReadOnlySpan<byte> Finish()
    {
        writer.Reset();
        writer.WriteVarString(text.ToString());
        writer.WriteRaw(lengths.Finish());
        return writer.WrittenSpan;
    }

    public void Reset()
    {
        text.Clear();
        lengths.Reset();
        writer.Reset();
    }

    public void Dispose()
    {
        lengths.Dispose();
        writer.Dispose();
    }
}
