using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;

namespace YSharp.TestKit.Sim;

/// <summary>Insertion-ordered set with JS <c>Set</c> semantics: re-adding keeps the position, remove then add moves to the end.</summary>
public sealed class OrderedSet<T> : IEnumerable<T> where T : notnull
{
    private readonly List<T> _items = new();
    private readonly HashSet<T> _index = new();

    public int Count => _items.Count;
    public bool Contains(T item) => _index.Contains(item);

    public bool Add(T item)
    {
        if (!_index.Add(item)) return false;
        _items.Add(item);
        return true;
    }

    public bool Remove(T item)
    {
        if (!_index.Remove(item)) return false;
        _items.Remove(item);
        return true;
    }

    public T[] ToArray() => _items.ToArray();
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items.ToArray()).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Payload counters (<c>_uniqueNumber</c>, <c>charCounter</c> upstream). SIM-05 (proposed ID): they belong to one run and start
/// at zero, so a seed alone reproduces payload text.
/// </summary>
public sealed class PayloadCounters
{
    private int _unique;
    private int _char;
    public int NextUniqueNumber() => _unique++;
    public int NextChar() => _char++;
}

/// <summary>
/// Sync message framing for the simulation. V1 runs use the library's <see cref="YSync"/>. V2 runs (SIM-04, ADR 0017) use a
/// test-only framing with the same message-type numbers and a V2 payload, because y-protocols has no V2 sync messages:
/// <c>varuint type, varuint length, payload</c>. This is TestKit framing, not a y-protocols claim.
/// </summary>
public static class SimFraming
{
    public static byte[] FrameV2(SyncMessageType type, ReadOnlySpan<byte> payload)
    {
        var w = new ArrayBufferWriter<byte>();
        WriteVarUint(w, (uint)type);
        WriteVarUint(w, (uint)payload.Length);
        w.Write(payload);
        return w.WrittenSpan.ToArray();
    }

    /// <exception cref="FormatException">The frame is truncated or malformed.</exception>
    public static (SyncMessageType Type, byte[] Payload) UnframeV2(ReadOnlySpan<byte> frame)
    {
        var type = ReadVarUint(ref frame);
        var len = ReadVarUint(ref frame);
        if (type > 2) throw new FormatException($"Unknown sync message type {type}.");
        if (len != frame.Length) throw new FormatException($"Frame payload is {frame.Length} bytes, header says {len}.");
        return ((SyncMessageType)type, frame.ToArray());
    }


    public static byte[] Step1(UpdateFormat format, Doc doc)
    {
        if (format == UpdateFormat.V1)
        {
            var w = new ArrayBufferWriter<byte>();
            YSync.WriteStep1(w, doc);
            return w.WrittenSpan.ToArray();
        }
        var sv = new ArrayBufferWriter<byte>();
        doc.EncodeStateVector(sv);
        return FrameV2(SyncMessageType.Step1, sv.WrittenSpan);
    }

    public static byte[] FrameV1Update(ReadOnlySpan<byte> update)
    {
        var w = new ArrayBufferWriter<byte>();
        YSync.WriteUpdate(w, update);
        return w.WrittenSpan.ToArray();
    }

    /// <summary>Applies one message to <paramref name="doc"/> with <paramref name="origin"/>; returns the reply message, if any.</summary>
    public static byte[]? Read(UpdateFormat format, byte[] message, Doc doc, object origin)
    {
        if (format == UpdateFormat.V1)
        {
            var reply = new ArrayBufferWriter<byte>();
            ReadOnlySpan<byte> input = message;
            var status = YSync.TryReadMessage(ref input, reply, doc, origin, out var result);
            if (!status.Ok || !result.Apply.Ok) Xunit.Assert.Fail($"Sync message rejected: {status.Error} / {result.Apply.Error}");
            return reply.WrittenCount > 0 ? reply.WrittenSpan.ToArray() : null;
        }

        var (type, payload) = UnframeV2(message);
        switch (type)
        {
            case SyncMessageType.Step1:
                return FrameV2(SyncMessageType.Step2, doc.EncodeStateAsUpdate(payload, UpdateFormat.V2));
            default:
                var applied = doc.TryApplyUpdate(payload, origin, UpdateFormat.V2);
                if (!applied.Ok) Xunit.Assert.Fail($"V2 sync update rejected: {applied.Error}");
                return null;
        }
    }

    public static void WriteVarUint(IBufferWriter<byte> w, uint value)
    {
        while (value > 0x7F)
        {
            var span = w.GetSpan(1);
            span[0] = (byte)(0x80 | (value & 0x7F));
            w.Advance(1);
            value >>= 7;
        }
        var last = w.GetSpan(1);
        last[0] = (byte)value;
        w.Advance(1);
    }

    public static uint ReadVarUint(ref ReadOnlySpan<byte> input)
    {
        uint result = 0;
        var shift = 0;
        while (true)
        {
            if (input.IsEmpty || shift > 28) throw new FormatException("Truncated or oversized varuint.");
            var b = input[0];
            input = input[1..];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }
}
