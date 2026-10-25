#nullable enable
using System;
using System.Buffers;
using YSharp.Lib0;

namespace YSharp.Codecs.V2;

/// <summary>
/// The V2 encoder adapter (Yjs <c>UpdateEncoderV2</c> and <c>DSEncoderV2</c>): fields go to their columns, the rest to one buffer, and
/// <see cref="CopyTo"/> writes the flag 0, the nine columns and the rest buffer. Embeds and format values are Any (ENC-75); JSON content elements
/// are JSON text in the string column. Every key is written as a new key (ENC-76): Yjs never fills its key map, so a key is never reused.
/// </summary>
internal sealed class UpdateEncoderV2 : IUpdateEncoder, IDisposable
{
    private readonly Lib0Writer rest = new(256);
    private readonly IntDiffOptRleColumnWriter keyClock = new();
    private readonly UintOptRleColumnWriter client = new();
    private readonly IntDiffOptRleColumnWriter leftClock = new();
    private readonly IntDiffOptRleColumnWriter rightClock = new();
    private readonly RleColumnWriter info = new();
    private readonly StringColumnWriter strings = new();
    private readonly RleColumnWriter parentInfo = new();
    private readonly UintOptRleColumnWriter typeRef = new();
    private readonly UintOptRleColumnWriter len = new();
    private long keyCount;
    private long dsCurrVal;

    /// <summary>Yjs <c>UpdateEncoderV2.toUint8Array</c>. The encoder is finished afterwards.</summary>
    public void CopyTo(IBufferWriter<byte> destination)
    {
        using var w = new Lib0Writer(rest.Length + 64);
        w.WriteVarUint(0);   // the feature flag
        w.WriteVarBytes(keyClock.Finish());
        w.WriteVarBytes(client.Finish());
        w.WriteVarBytes(leftClock.Finish());
        w.WriteVarBytes(rightClock.Finish());
        w.WriteVarBytes(info.Bytes.WrittenSpan);
        w.WriteVarBytes(strings.Finish());
        w.WriteVarBytes(parentInfo.Bytes.WrittenSpan);
        w.WriteVarBytes(typeRef.Finish());
        w.WriteVarBytes(len.Finish());
        w.WriteRaw(rest.WrittenSpan);
        w.CopyTo(destination);
    }

    /// <summary>Yjs <c>DSEncoderV2.toUint8Array</c>: the rest buffer alone.</summary>
    public void CopyRestTo(IBufferWriter<byte> destination) => rest.CopyTo(destination);

    public void Dispose()
    {
        rest.Dispose();
        keyClock.Dispose();
        client.Dispose();
        leftClock.Dispose();
        rightClock.Dispose();
        info.Dispose();
        strings.Dispose();
        parentInfo.Dispose();
        typeRef.Dispose();
        len.Dispose();
    }

    public void WriteVarUint(ulong value) => rest.WriteVarUint(value);

    public void ResetDsCurVal() => dsCurrVal = 0;

    /// <summary>
    /// The clock as a difference from the end of the client's previous range. Ranges out of clock order (a delete set passed through as read)
    /// give a negative difference, which JS <c>writeVarUint</c> writes as one byte of its low seven bits; that byte is kept.
    /// </summary>
    public void WriteDsClock(long clock)
    {
        var diff = clock - dsCurrVal;
        dsCurrVal = clock;
        if (diff < 0) rest.WriteUint8((byte)(diff & 0x7f));
        else rest.WriteVarUint((ulong)diff);
    }

    public void WriteDsLen(long length)
    {
        if (length <= 0) throw new YInternalException("A delete range of length 0 cannot be written as V2.");
        rest.WriteVarUint((ulong)(length - 1));
        dsCurrVal += length;
    }

    public void WriteClient(ulong value) => client.Write(value);

    public void WriteInfo(byte value) => info.Write(value);

    public void WriteLeftId(ItemId id)
    {
        client.Write(id.Client);
        leftClock.Write(id.Clock);
    }

    public void WriteRightId(ItemId id)
    {
        client.Write(id.Client);
        rightClock.Write(id.Clock);
    }

    public void WriteParentInfo(bool isRoot) => parentInfo.Write(isRoot ? (byte)1 : (byte)0);

    public void WriteString(string value) => strings.Write(value);

    public void WriteString(ReadOnlySpan<char> value) => strings.Write(value);

    public void WriteKey(string key)
    {
        keyClock.Write(keyCount++);
        strings.Write(key);
    }

    public void WriteTypeRef(ulong value) => typeRef.Write(value);

    public void WriteLen(long length) => len.Write((ulong)length);

    public void WriteAny(in YValue value)
    {
        var error = YValueAny.Write(rest, value, int.MaxValue);
        if (error != YError.None) throw new YInternalException($"A stored value could not be written as Any ({error}).");
    }

    public void WriteBuf(ReadOnlySpan<byte> value) => rest.WriteVarBytes(value);

    public void WriteJson(in YValue value) => WriteAny(value);

    /// <summary>Yjs <c>ContentJSON.write</c>: <c>JSON.stringify</c> of the element, or <c>undefined</c>, through the string column.</summary>
    public void WriteJsonElement(in YValue value)
    {
        if (value.Kind == YValueKind.Undefined)
        {
            strings.Write("undefined");
            return;
        }

        var text = new ArrayBufferWriter<byte>(64);
        var status = JsJsonWriter.Write(value, text);
        if (!status.Ok) throw new YInternalException($"A stored value could not be written as JSON ({status.Error}).");
        strings.Write(System.Text.Encoding.UTF8.GetString(text.WrittenSpan));
    }
}
