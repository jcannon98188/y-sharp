#nullable enable
using System;
using YSharp.Lib0;

namespace YSharp.Codecs.V1;

/// <summary>
/// The V1 decoder adapter (Yjs <c>UpdateDecoderV1</c> and <c>DSDecoderV1</c>): every field is a plain lib0 value read in order from one buffer.
/// Strings are capped at <see cref="YLimits.MaxStringBytes"/> and Any values at <see cref="YLimits.MaxAnyDepth"/>.
/// </summary>
internal ref struct UpdateDecoderV1 : IUpdateDecoder
{
    private static ReadOnlySpan<byte> UndefinedText => "undefined"u8;

    private Lib0Reader reader;
    private readonly YLimits limits;
    private YError error;

    public UpdateDecoderV1(ReadOnlySpan<byte> update, YLimits limits)
    {
        reader = new Lib0Reader(update, limits.MaxStringBytes);
        this.limits = limits;
        error = YError.None;
    }

    public readonly YError Error => error != YError.None ? error : reader.Error;

    /// <summary>Every entry is a few bytes of the one buffer: a client block three (struct count, client, start clock), a struct two (info and a
    /// length or a parent), a delete-set client or range two, a value one.</summary>
    public readonly bool CanHold(ulong count, CountClaim claim) => claim switch
    {
        CountClaim.Clients => count <= (ulong)reader.Remaining / 3,
        CountClaim.AnyValues or CountClaim.JsonValues => count <= (ulong)reader.Remaining,
        _ => count <= (ulong)reader.Remaining / 2,
    };

    private bool Fail(YError e = YError.Malformed)
    {
        error = e;
        return false;
    }

    public bool ReadVarUint(out ulong value) => reader.TryReadVarUint(out value);

    public void ResetDsCurVal() { }

    public bool ReadDsClock(out ulong clock) => reader.TryReadVarUint(out clock);

    public bool ReadDsLen(out ulong length) => reader.TryReadVarUint(out length);

    public bool ReadClient(out ulong client) => reader.TryReadVarUint(out client);

    public bool ReadInfo(out byte info) => reader.TryReadUint8(out info);

    public bool ReadLeftId(out ItemId id) => ReadId(out id);

    public bool ReadRightId(out ItemId id) => ReadId(out id);

    private bool ReadId(out ItemId id)
    {
        id = default;
        if (!reader.TryReadVarUint(out var client) || !reader.TryReadVarUint(out var clock)) return false;
        id = new ItemId(client, (long)clock);
        return true;
    }

    /// <summary>A varuint; only 1 means a root key (Yjs tests <c>=== 1</c>), every other value means a parent id (ENC-49).</summary>
    public bool ReadParentInfo(out bool isRoot)
    {
        isRoot = false;
        if (!reader.TryReadVarUint(out var v)) return false;
        isRoot = v == 1;
        return true;
    }

    public bool ReadString(out string value) => reader.TryReadVarString(out value);

    public bool ReadKey(out string key) => reader.TryReadVarString(out key);

    public bool ReadTypeRef(out ulong typeRef) => reader.TryReadVarUint(out typeRef);

    public bool ReadLen(out ulong length) => reader.TryReadVarUint(out length);

    public bool ReadAny(out YValue value)
    {
        var e = YValueAny.Read(ref reader, limits.MaxAnyDepth, out value);
        return e == YError.None || Fail(e);
    }

    public bool ReadBuf(out byte[] value)
    {
        value = Array.Empty<byte>();
        if (!reader.TryReadVarBytes(out var bytes)) return false;
        value = bytes.ToArray();
        return true;
    }

    public bool ReadJson(out YValue value) => ReadJsonText(out value, allowUndefined: false);

    public bool ReadJsonElement(out YValue value) => ReadJsonText(out value, allowUndefined: true);

    /// <summary>A varstring holding JSON text, parsed as <c>JSON.parse</c> does; for a JSON content element the exact text <c>undefined</c> is Undefined.</summary>
    private bool ReadJsonText(out YValue value, bool allowUndefined)
    {
        value = default;
        if (!reader.TryReadVarUint(out var claim)) return false;
        if (claim > (ulong)limits.MaxStringBytes) return Fail(YError.LimitExceeded);
        if (claim > (ulong)reader.Remaining) return Fail();
        if (!reader.TryReadBytes((int)claim, out var text)) return false;
        if (!System.Text.Unicode.Utf8.IsValid(text)) return Fail();
        if (allowUndefined && text.SequenceEqual(UndefinedText)) return true;
        var status = JsJsonReader.Read(text, limits, out value);
        return status.Ok || Fail(status.Error);
    }
}
