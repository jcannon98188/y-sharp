#nullable enable
using System;

namespace YSharp.Codecs;

/// <summary>
/// The decoder half of the codec seam (ADR 0021): one adapter per wire format (V1 here, V2 in spec 18), the shape of Yjs <c>UpdateDecoderV1</c> and
/// <c>UpdateDecoderV2</c>. The format-independent reader (<see cref="UpdateReader"/>) is generic over it, so each adapter is specialised by the JIT
/// without virtual calls. Every read is strict (ADR 0005): it returns false at EOF or on a bad value and sets <see cref="Error"/>.
/// </summary>
internal interface IUpdateDecoder
{
    /// <summary>Why the last failed read failed: <see cref="YError.Malformed"/> or <see cref="YError.LimitExceeded"/>.</summary>
    YError Error { get; }

    /// <summary>
    /// Whether <paramref name="count"/> entries of <paramref name="claim"/> could still be read: checked before a loop or an array is sized from
    /// a count claim. Each format knows the least an entry costs it.
    /// </summary>
    bool CanHold(ulong count, CountClaim claim);

    /// <summary>A plain varuint from the rest buffer: client and struct counts, start clocks, Skip lengths, delete-set counts and clients.</summary>
    bool ReadVarUint(out ulong value);

    /// <summary>Yjs <c>resetDsCurVal</c>: the start of a client's delete ranges (V2 delta-codes them).</summary>
    void ResetDsCurVal();

    bool ReadDsClock(out ulong clock);

    bool ReadDsLen(out ulong length);

    bool ReadClient(out ulong client);

    bool ReadInfo(out byte info);

    bool ReadLeftId(out ItemId id);

    bool ReadRightId(out ItemId id);

    /// <summary>True when the parent is a root type named by the following string, false when it is an item id.</summary>
    bool ReadParentInfo(out bool isRoot);

    bool ReadString(out string value);

    bool ReadKey(out string key);

    bool ReadTypeRef(out ulong typeRef);

    bool ReadLen(out ulong length);

    bool ReadAny(out YValue value);

    bool ReadBuf(out byte[] value);

    /// <summary>A JSON value of an embed or a format (V1: JSON text, V2: Any).</summary>
    bool ReadJson(out YValue value);

    /// <summary>One element of a legacy JSON content: JSON text in both formats, the literal <c>undefined</c> being an undefined element.</summary>
    bool ReadJsonElement(out YValue value);
}

/// <summary>The counts an update claims, for <see cref="IUpdateDecoder.CanHold"/>.</summary>
internal enum CountClaim { Clients, Structs, DeleteClients, DeleteRanges, AnyValues, JsonValues }

/// <summary>The encoder half of the codec seam: the shape of Yjs <c>UpdateEncoderV1</c> and <c>UpdateEncoderV2</c>.</summary>
internal interface IUpdateEncoder
{
    void WriteVarUint(ulong value);

    void ResetDsCurVal();

    void WriteDsClock(long clock);

    void WriteDsLen(long length);

    void WriteClient(ulong client);

    void WriteInfo(byte info);

    void WriteLeftId(ItemId id);

    void WriteRightId(ItemId id);

    void WriteParentInfo(bool isRoot);

    void WriteString(string value);

    void WriteString(ReadOnlySpan<char> value);

    void WriteKey(string key);

    void WriteTypeRef(ulong typeRef);

    void WriteLen(long length);

    void WriteAny(in YValue value);

    void WriteBuf(ReadOnlySpan<byte> value);

    void WriteJson(in YValue value);

    void WriteJsonElement(in YValue value);
}
