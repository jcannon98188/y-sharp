#nullable enable
using System;
using System.Buffers;
using YSharp.Lib0;

namespace YSharp.Codecs.V1;

/// <summary>
/// The V1 encoder adapter (Yjs <c>UpdateEncoderV1</c> and <c>DSEncoderV1</c>): every field is written in order into one lib0 buffer. Embeds and
/// format values are JSON text, as V1 writes them.
/// </summary>
internal readonly struct UpdateEncoderV1 : IUpdateEncoder
{
    private static ReadOnlySpan<byte> UndefinedText => "undefined"u8;

    private readonly Lib0Writer writer;

    public UpdateEncoderV1(Lib0Writer writer) => this.writer = writer;

    public Lib0Writer Writer => writer;

    public void WriteVarUint(ulong value) => writer.WriteVarUint(value);

    public void ResetDsCurVal() { }

    public void WriteDsClock(long clock) => writer.WriteVarUint((ulong)clock);

    public void WriteDsLen(long length) => writer.WriteVarUint((ulong)length);

    public void WriteClient(ulong client) => writer.WriteVarUint(client);

    public void WriteInfo(byte info) => writer.WriteUint8(info);

    public void WriteLeftId(ItemId id)
    {
        writer.WriteVarUint(id.Client);
        writer.WriteVarUint((ulong)id.Clock);
    }

    public void WriteRightId(ItemId id) => WriteLeftId(id);

    public void WriteParentInfo(bool isRoot) => writer.WriteVarUint(isRoot ? 1UL : 0UL);

    public void WriteString(string value) => writer.WriteVarString(value);

    public void WriteString(ReadOnlySpan<char> value) => writer.WriteVarString(value);

    public void WriteKey(string key) => writer.WriteVarString(key);

    public void WriteTypeRef(ulong typeRef) => writer.WriteVarUint(typeRef);

    public void WriteLen(long length) => writer.WriteVarUint((ulong)length);

    public void WriteAny(in YValue value)
    {
        var error = YValueAny.Write(writer, value, int.MaxValue);
        if (error != YError.None) throw new YInternalException($"A stored value could not be written as Any ({error}).");
    }

    public void WriteBuf(ReadOnlySpan<byte> value) => writer.WriteVarBytes(value);

    public void WriteJson(in YValue value) => WriteJsonText(value, allowUndefined: false);

    public void WriteJsonElement(in YValue value) => WriteJsonText(value, allowUndefined: true);

    /// <summary><c>JSON.stringify</c> as a varstring; an undefined element of a JSON content is the text <c>undefined</c> (Yjs <c>ContentJSON.write</c>).</summary>
    private void WriteJsonText(in YValue value, bool allowUndefined)
    {
        if (allowUndefined && value.Kind == YValueKind.Undefined)
        {
            writer.WriteVarBytes(UndefinedText);
            return;
        }

        var text = new ArrayBufferWriter<byte>(64);
        var status = JsJsonWriter.Write(value, text);
        if (!status.Ok) throw new YInternalException($"A stored value could not be written as JSON ({status.Error}).");
        writer.WriteVarBytes(text.WrittenSpan);
    }
}
