#nullable enable
using System;
using System.Buffers;
using YSharp.Lib0;

namespace YSharp;

// ---------------------------------------------------------------- sync + auth protocols

public enum SyncMessageType : byte { Step1 = 0, Step2 = 1, Update = 2 }

public readonly struct SyncReadResult
{
    internal SyncReadResult(SyncMessageType type, ApplyResult apply, bool replyWritten)
    {
        Type = type;
        Apply = apply;
        ReplyWritten = replyWritten;
    }

    public SyncMessageType Type { get; }
    public ApplyResult Apply { get; }      // meaningful for Step2/Update; Ok for Step1
    public bool ReplyWritten { get; }      // Step1 appends a Step2 reply (SY-12)
}

/// <summary>
/// y-protocols <c>sync.js</c> (SY-1..SY-20): a message is <c>varUint(type) · varUint8Array(payload)</c> with V1 payloads. The writers append to the
/// given writer, so messages combine with an envelope written before them (SY-6). Nothing here tracks a sync state; that is the provider's job (PR-5).
/// </summary>
public static class YSync
{
    /// <summary>Yjs <c>writeSyncStep1</c> (SY-3): <c>[0, len, ...encodeStateVector(doc)]</c>.</summary>
    public static void WriteStep1(IBufferWriter<byte> writer, Doc doc)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(doc);
        var sv = new ArrayBufferWriter<byte>();
        doc.EncodeStateVector(sv);
        Write(writer, SyncMessageType.Step1, sv.WrittenSpan);
    }

    /// <summary>
    /// Yjs <c>writeSyncStep2</c> (SY-4): <c>[1, len, ...encodeStateAsUpdate(doc, sv)]</c>; an empty <paramref name="targetStateVector"/> means the whole
    /// document. Throws <see cref="YDecodeException"/> when the state vector is malformed.
    /// </summary>
    public static void WriteStep2(IBufferWriter<byte> writer, Doc doc, ReadOnlySpan<byte> targetStateVector = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(doc);
        var update = new ArrayBufferWriter<byte>();
        doc.EncodeStateAsUpdate(update, targetStateVector);
        Write(writer, SyncMessageType.Step2, update.WrittenSpan);
    }

    /// <summary>
    /// <c>writeSyncStep1</c> with no document (SY-3): <c>[0, len, ...stateVector]</c>, for a server that holds a document as merged updates
    /// (ADR 0024 SRV-2). The state vector bytes are not validated.
    /// </summary>
    public static void WriteStep1(IBufferWriter<byte> writer, ReadOnlySpan<byte> stateVector)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Write(writer, SyncMessageType.Step1, stateVector);
    }

    /// <summary><c>writeSyncStep2</c> with no document (SY-4): <c>[1, len, ...update]</c>, the update already computed; its bytes are not validated.</summary>
    public static void WriteStep2(IBufferWriter<byte> writer, ReadOnlySpan<byte> update)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Write(writer, SyncMessageType.Step2, update);
    }

    /// <summary>Yjs <c>writeUpdate</c> (SY-5): <c>[2, len, ...update]</c>; the update bytes are not validated.</summary>
    public static void WriteUpdate(IBufferWriter<byte> writer, ReadOnlySpan<byte> update)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Write(writer, SyncMessageType.Update, update);
    }

    /// <summary>
    /// Yjs <c>readSyncMessage</c> (SY-7..SY-12, SY-19, SY-20). Reads one message from the front of <paramref name="input"/>; unknown type or truncation
    /// is an error result, never an exception (ADR 0005). Step1 appends a Step2 reply to <paramref name="reply"/> and leaves the document alone.
    /// Step2 and Update apply the payload with <paramref name="origin"/>; a payload that cannot be read or applied goes to
    /// <paramref name="errorHandler"/>, as in JS, and is also the returned status, with <paramref name="result"/> naming the type and the failure in
    /// <see cref="SyncReadResult.Apply"/>. An unknown type or a bad Step1 is an error status with no handler call, where JS throws (SY-20).
    /// <paramref name="input"/> advances past what was read, as the JS decoder does, also on failure.
    /// </summary>
    public static YStatus TryReadMessage(ref ReadOnlySpan<byte> input, IBufferWriter<byte> reply, Doc doc, object? origin, out SyncReadResult result, Action<YError>? errorHandler = null)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(doc);
        result = default;
        var reader = new Lib0Reader(input);
        try
        {
            if (!reader.TryReadVarUint(out var type)) return new YStatus(reader.Error);
            switch (type)
            {
                case (ulong)SyncMessageType.Step1:
                {
                    // JS writes the reply's type byte before encodeStateAsUpdate throws on a bad vector; here the reply is written whole or not at all.
                    // An empty payload is no state vector in JS either (decoding it throws), though EncodeStateAsUpdate would read it as "everything".
                    if (!reader.TryReadVarBytes(out var sv)) return new YStatus(reader.Error);
                    var status = StateVector.TryDecode(sv, out _);
                    if (!status.Ok) return status;
                    var update = new ArrayBufferWriter<byte>();
                    doc.EncodeStateAsUpdate(update, sv);
                    Write(reply, SyncMessageType.Step2, update.WrittenSpan);
                    result = new SyncReadResult(SyncMessageType.Step1, default, true);
                    return default;
                }
                case (ulong)SyncMessageType.Step2:
                case (ulong)SyncMessageType.Update:
                {
                    // Yjs readSyncStep2, which is readUpdate (SY-9): the read and the apply are both inside its try, so either failure goes to the handler
                    var apply = reader.TryReadVarBytes(out var payload)
                        ? doc.TryApplyUpdate(payload, origin)
                        : new ApplyResult(reader.Error, 0, 0);
                    result = new SyncReadResult((SyncMessageType)type, apply, false);
                    if (apply.Ok) return default;
                    errorHandler?.Invoke(apply.Error);
                    return new YStatus(apply.Error);
                }
                default:
                    return new YStatus(YError.UnknownMessage);
            }
        }
        finally
        {
            input = input[reader.Position..];
        }
    }

    /// <summary>
    /// Reads one message's frame from the front of <paramref name="input"/> without acting on it: its type and its payload, a slice of
    /// <paramref name="input"/>, for a server that answers with no document (ADR 0024 SRV-2, SRV-17). An unknown type is
    /// <see cref="YError.UnknownMessage"/> and truncation an error result, never an exception (ADR 0005). The payload is not validated.
    /// <paramref name="input"/> advances past what was read, also on failure.
    /// </summary>
    public static YStatus TryReadFrame(ref ReadOnlySpan<byte> input, out SyncMessageType type, out ReadOnlySpan<byte> payload)
    {
        type = default;
        payload = default;
        var reader = new Lib0Reader(input);
        try
        {
            if (!reader.TryReadVarUint(out var t)) return new YStatus(reader.Error);
            if (t > (ulong)SyncMessageType.Update) return new YStatus(YError.UnknownMessage);
            if (!reader.TryReadVarBytes(out var bytes)) return new YStatus(reader.Error);
            type = (SyncMessageType)t;
            payload = bytes;
            return default;
        }
        finally
        {
            input = input[reader.Position..];
        }
    }

    private static void Write(IBufferWriter<byte> writer, SyncMessageType type, ReadOnlySpan<byte> payload)
    {
        using var w = Lib0Writer.Rent(payload.Length + 8);
        w.WriteVarUint((ulong)type);
        w.WriteVarBytes(payload);
        w.CopyTo(writer);
    }
}
