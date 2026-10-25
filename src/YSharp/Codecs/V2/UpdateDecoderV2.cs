#nullable enable
using System;
using System.Collections.Generic;
using YSharp.Lib0;

namespace YSharp.Codecs.V2;

/// <summary>
/// The V2 decoder adapter (Yjs <c>UpdateDecoderV2</c> and <c>DSDecoderV2</c>): a feature flag (ignored, ENC-55), nine columns, then the rest
/// buffer, which holds the counts, start clocks, Skip lengths, Any values, buffers and the delete set. A column longer than the bytes left is
/// Malformed (ENC-56), and so is a column that runs out (ENC-64 to ENC-66) or a negative clock (Q22). Strings are capped at
/// <see cref="YLimits.MaxStringBytes"/> each, Any values at <see cref="YLimits.MaxAnyDepth"/>.
/// </summary>
internal ref struct UpdateDecoderV2 : IUpdateDecoder
{
    private Lib0Reader rest;
    private IntDiffOptRleColumnReader keyClock;
    private UintOptRleColumnReader client;
    private IntDiffOptRleColumnReader leftClock;
    private IntDiffOptRleColumnReader rightClock;
    private RleColumnReader info;
    private StringColumnReader strings;
    private RleColumnReader parentInfo;
    private UintOptRleColumnReader typeRef;
    private UintOptRleColumnReader len;
    private readonly YLimits limits;
    private List<string>? keys;
    private ulong dsCurrVal;
    private YError error;

    public UpdateDecoderV2(ReadOnlySpan<byte> update, YLimits limits)
    {
        this = default;
        this.limits = limits;
        var reader = new Lib0Reader(update, limits.MaxStringBytes);
        if (!reader.TryReadVarUint(out _)
            || !reader.TryReadVarBytes(out var keyClockColumn)
            || !reader.TryReadVarBytes(out var clientColumn)
            || !reader.TryReadVarBytes(out var leftClockColumn)
            || !reader.TryReadVarBytes(out var rightClockColumn)
            || !reader.TryReadVarBytes(out var infoColumn)
            || !reader.TryReadVarBytes(out var stringColumn)
            || !reader.TryReadVarBytes(out var parentInfoColumn)
            || !reader.TryReadVarBytes(out var typeRefColumn)
            || !reader.TryReadVarBytes(out var lenColumn)
            || !StringColumnReader.TryCreate(stringColumn, out strings))
        {
            error = YError.Malformed;
            return;
        }

        keyClock = new IntDiffOptRleColumnReader(keyClockColumn);
        client = new UintOptRleColumnReader(clientColumn);
        leftClock = new IntDiffOptRleColumnReader(leftClockColumn);
        rightClock = new IntDiffOptRleColumnReader(rightClockColumn);
        info = new RleColumnReader(infoColumn);
        parentInfo = new RleColumnReader(parentInfoColumn);
        typeRef = new UintOptRleColumnReader(typeRefColumn);
        len = new UintOptRleColumnReader(lenColumn);
        rest = reader;
    }

    public readonly YError Error => error != YError.None ? error : rest.Error;

    private bool Fail(YError e = YError.Malformed)
    {
        error = e;
        return false;
    }

    /// <summary>
    /// What each entry costs at least: a client block two rest bytes (struct count, start clock), a delete-set client or range two, an Any value
    /// one, a JSON element one character of the string column. A struct can cost nothing (every column may repeat its last run), so a struct
    /// count is bounded by <see cref="YLimits.MaxStructsPerUpdate"/> alone.
    /// </summary>
    public readonly bool CanHold(ulong count, CountClaim claim) => claim switch
    {
        CountClaim.Structs => true,
        CountClaim.AnyValues => count <= (ulong)rest.Remaining,
        CountClaim.JsonValues => count <= (ulong)strings.CharsLeft,
        _ => count <= (ulong)rest.Remaining / 2,
    };

    public bool ReadVarUint(out ulong value) => rest.TryReadVarUint(out value) || Fail(rest.Error);

    public void ResetDsCurVal() => dsCurrVal = 0;

    public bool ReadDsClock(out ulong clock)
    {
        clock = 0;
        if (!rest.TryReadVarUint(out var diff)) return Fail(rest.Error);
        dsCurrVal += diff;
        clock = dsCurrVal;
        return true;
    }

    public bool ReadDsLen(out ulong length)
    {
        length = 0;
        if (!rest.TryReadVarUint(out var lengthMinusOne)) return Fail(rest.Error);
        length = lengthMinusOne + 1;
        dsCurrVal += length;
        return true;
    }

    public bool ReadClient(out ulong value) => client.Read(out value) || Fail();

    public bool ReadInfo(out byte value) => info.Read(out value) || Fail();

    public bool ReadLeftId(out ItemId id) => ReadId(ref leftClock, out id);

    public bool ReadRightId(out ItemId id) => ReadId(ref rightClock, out id);

    private bool ReadId(ref IntDiffOptRleColumnReader clocks, out ItemId id)
    {
        id = default;
        if (!client.Read(out var c) || !ReadClock(ref clocks, out var clock)) return Fail();
        id = new ItemId(c, clock);
        return true;
    }

    /// <summary>A clock from a diff column: below 0 or above 2^53-1 is Malformed (Q22; JS would use the negative clock).</summary>
    private static bool ReadClock(ref IntDiffOptRleColumnReader clocks, out long clock) =>
        clocks.Read(out clock) && clock >= 0 && clock <= UpdateReader.MaxClock;

    /// <summary>Only 1 means a root key (Yjs tests <c>=== 1</c>).</summary>
    public bool ReadParentInfo(out bool isRoot)
    {
        isRoot = false;
        if (!parentInfo.Read(out var v)) return Fail();
        isRoot = v == 1;
        return true;
    }

    public bool ReadString(out string value)
    {
        if (!strings.Read(out value)) return Fail();
        return WithinStringLimit(value) || Fail(YError.LimitExceeded);
    }

    private readonly bool WithinStringLimit(string value) =>
        (ulong)value.Length * 3 <= (ulong)limits.MaxStringBytes || System.Text.Encoding.UTF8.GetByteCount(value) <= limits.MaxStringBytes;

    /// <summary>
    /// Yjs <c>readKey</c>: a key clock below the number of keys read so far names one of them; any other clock reads the next string as a new
    /// key. A negative key clock is Malformed (JS would return <c>undefined</c>).
    /// </summary>
    public bool ReadKey(out string key)
    {
        key = "";
        if (!keyClock.Read(out var clock) || clock < 0) return Fail();
        keys ??= new List<string>();
        if (clock < keys.Count)
        {
            key = keys[(int)clock];
            return true;
        }

        if (!ReadString(out key)) return false;
        keys.Add(key);
        return true;
    }

    public bool ReadTypeRef(out ulong value) => typeRef.Read(out value) || Fail();

    public bool ReadLen(out ulong value) => len.Read(out value) || Fail();

    public bool ReadAny(out YValue value)
    {
        var e = YValueAny.Read(ref rest, limits.MaxAnyDepth, out value);
        return e == YError.None || Fail(e);
    }

    public bool ReadBuf(out byte[] value)
    {
        value = Array.Empty<byte>();
        if (!rest.TryReadVarBytes(out var bytes)) return Fail(rest.Error);
        value = bytes.ToArray();
        return true;
    }

    /// <summary>An embed or a format value is an Any in V2 (ENC-75).</summary>
    public bool ReadJson(out YValue value) => ReadAny(out value);

    /// <summary>A JSON content element: JSON text from the string column, the exact text <c>undefined</c> being Undefined.</summary>
    public bool ReadJsonElement(out YValue value)
    {
        value = default;
        if (!ReadString(out var text)) return false;
        if (text == "undefined") return true;
        var status = JsJsonReader.Read(System.Text.Encoding.UTF8.GetBytes(text), limits, out value);
        return status.Ok || Fail(status.Error);
    }
}
