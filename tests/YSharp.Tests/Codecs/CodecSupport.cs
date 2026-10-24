using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Stubs;

namespace YSharp.Tests.Codecs;

/// <summary>
/// Shared helpers of the values-and-codecs tests (TASK-2.5). Tests compile against the API stub and skip until the
/// implementation exists (ADR 0019), so every body that touches y-sharp runs inside <see cref="Stub.Run(Action)"/>.
/// </summary>
internal static class Stub
{
    public const string Reason = ApiStub.Reason;

    /// <summary>Runs <paramref name="body"/>; a <see cref="NotImplementedException"/> from the stub skips the test.</summary>
    public static void Run(Action body) => ApiStub.Run(body);

    public static T Run<T>(Func<T> body) => ApiStub.Run(body);
}

internal static class Wire
{
    /// <summary>Client id used by every single-client helper document.</summary>
    public const uint Client = 1;

    public static Doc NewDoc(uint client = Client, bool gc = true) => new(new DocOptions { ClientId = client, Gc = gc });

    public static byte[] Encode(Doc doc, UpdateFormat format = UpdateFormat.V1, ReadOnlySpan<byte> targetSv = default) =>
        doc.EncodeStateAsUpdate(targetSv, format);

    public static byte[] StateVector(Doc doc)
    {
        var w = new ArrayBufferWriter<byte>();
        doc.EncodeStateVector(w);
        return w.WrittenSpan.ToArray();
    }

    public static byte[] Merge(UpdateFormat format, params byte[][] updates)
    {
        var w = new ArrayBufferWriter<byte>();
        var status = YUpdate.TryMerge(updates.Select(u => (ReadOnlyMemory<byte>)u).ToList(), w, format);
        Assert.True(status.Ok, $"TryMerge failed with {status.Error}");
        return w.WrittenSpan.ToArray();
    }

    public static byte[] Diff(byte[] update, byte[] stateVector, UpdateFormat format = UpdateFormat.V1)
    {
        var w = new ArrayBufferWriter<byte>();
        var status = YUpdate.TryDiff(update, stateVector, w, format);
        Assert.True(status.Ok, $"TryDiff failed with {status.Error}");
        return w.WrittenSpan.ToArray();
    }

    public static byte[] Convert(byte[] update, UpdateFormat from, UpdateFormat to)
    {
        var w = new ArrayBufferWriter<byte>();
        var status = YUpdate.TryConvert(update, from, to, w);
        Assert.True(status.Ok, $"TryConvert failed with {status.Error}");
        return w.WrittenSpan.ToArray();
    }

    public static byte[] StateVectorOfUpdate(byte[] update, UpdateFormat format = UpdateFormat.V1)
    {
        var w = new ArrayBufferWriter<byte>();
        var status = YUpdate.TryStateVectorFromUpdate(update, w, format);
        Assert.True(status.Ok, $"TryStateVectorFromUpdate failed with {status.Error}");
        return w.WrittenSpan.ToArray();
    }

    /// <summary>A V1 state vector with one client entry.</summary>
    public static byte[] Sv(uint client, long clock)
    {
        var b = new List<byte> { 1 };
        VarUint(b, client);
        VarUint(b, (ulong)clock);
        return b.ToArray();
    }

    public static void VarUint(List<byte> to, ulong value)
    {
        while (value > 127) { to.Add((byte)((value & 127) | 128)); value >>= 7; }
        to.Add((byte)value);
    }

    /// <summary>Applies <paramref name="update"/> to a fresh document without throwing and returns the result with the document.</summary>
    public static (ApplyResult Result, Doc Doc) TryApply(byte[] update, UpdateFormat format = UpdateFormat.V1)
    {
        var doc = new Doc();
        return (doc.TryApplyUpdate(update, null, format), doc);
    }

    public static bool IsEmpty(StateVector sv) => !sv.Clients.Any();

    /// <summary>
    /// The bytes of the single Any value that <c>map.set("k", value)</c> writes in a client-1 update: the update is
    /// <c>01 01 01 00 28 01 01 6d 01 6b 01 &lt;any&gt; 00</c> (one ContentAny struct with a root key and a parentSub, then an empty delete set).
    /// </summary>
    public static byte[] AnyPayloadOfMapSet(YValue value)
    {
        var doc = NewDoc();
        doc.GetMap("m").Set("k", value);
        return AnyPayloadOf(doc);
    }

    private static readonly byte[] MapSetPrefix = Hex.Bytes("010101002801016d016b01");

    /// <summary>The Any payload of the <c>m.k</c> struct of a client-1 document (see <see cref="AnyPayloadOfMapSet"/>).</summary>
    public static byte[] AnyPayloadOf(Doc doc)
    {
        var bytes = Encode(doc);
        Assert.True(bytes.AsSpan().StartsWith(MapSetPrefix), $"unexpected V1 layout {Hex.Of(bytes)}");
        return bytes.AsSpan(MapSetPrefix.Length, bytes.Length - MapSetPrefix.Length - 1).ToArray();
    }

    /// <summary>A V1 update that carries <paramref name="any"/> as the value of <c>m.k</c> (client 1, clock 0).</summary>
    public static byte[] MapSetUpdateWithAny(byte[] any) => MapSetPrefix.Concat(any).Append((byte)0).ToArray();

    /// <summary>Decodes <paramref name="any"/> through an applied update and returns the document holding it.</summary>
    public static Doc DocWithAny(byte[] any)
    {
        var doc = new Doc();
        doc.ApplyUpdate(MapSetUpdateWithAny(any));
        return doc;
    }

    /// <summary>Decode then re-encode: the canonical Any bytes of whatever <paramref name="any"/> decodes to.</summary>
    public static byte[] ReencodeAny(byte[] any) => AnyPayloadOf(DocWithAny(any));

    public static YValue ValueOfMapKey(Doc doc, string key = "k")
    {
        Assert.True(doc.GetMap("m").TryGet(key, out var entry), $"key {key} is absent");
        return entry.Value;
    }
}

/// <summary>A V2 update split into its flag, nine length-prefixed columns and the raw rest section (encoding.md 3.5).</summary>
internal sealed class V2Columns
{
    private V2Columns(byte[][] columns, byte[] rest) { Columns = columns; Rest = rest; }

    public byte[][] Columns { get; }
    public byte[] Rest { get; }
    public byte[] KeyClock => Columns[0];
    public byte[] Client => Columns[1];
    public byte[] LeftClock => Columns[2];
    public byte[] RightClock => Columns[3];
    public byte[] Info => Columns[4];
    public byte[] String => Columns[5];
    public byte[] ParentInfo => Columns[6];
    public byte[] TypeRef => Columns[7];
    public byte[] Len => Columns[8];

    public static V2Columns Split(byte[] update)
    {
        var pos = 0;
        ulong VarUint()
        {
            ulong value = 0;
            for (var shift = 0; ; shift += 7)
            {
                var b = update[pos++];
                value |= (ulong)(b & 127) << shift;
                if (b < 128) return value;
            }
        }

        Assert.Equal(0UL, VarUint());                       // feature flag
        var columns = new byte[9][];
        for (var i = 0; i < columns.Length; i++)
        {
            var len = (int)VarUint();
            columns[i] = update.AsSpan(pos, len).ToArray();
            pos += len;
        }
        return new V2Columns(columns, update.AsSpan(pos).ToArray());
    }
}
