using System.Collections.Generic;
using Xunit;

namespace YSharp.Tests.Codecs;

internal static class Malformed
{
    /// <summary>The strict-decoding contract of ADR 0005: a typed rejection, no exception from TryApplyUpdate, nothing applied.</summary>
    public static void AssertRejected(string hex, UpdateFormat format = UpdateFormat.V1) => AssertRejected(Hex.Bytes(hex), format);

    public static void AssertRejected(byte[] update, UpdateFormat format = UpdateFormat.V1)
    {
        var (result, doc) = Wire.TryApply(update, format);
        Assert.False(result.Ok, $"{Hex.Of(update)} was accepted");
        Assert.True(result.Error is YError.Malformed or YError.LimitExceeded, $"error was {result.Error}");
        Assert.True(Wire.IsEmpty(StateVector.Of(doc)), "a rejected update must leave the document untouched");
        Assert.Throws<YDecodeException>(() => new Doc().ApplyUpdate(update, null, format));
    }

    public static Doc AssertAccepted(string hex, UpdateFormat format = UpdateFormat.V1) => AssertAccepted(Hex.Bytes(hex), format);

    public static Doc AssertAccepted(byte[] update, UpdateFormat format = UpdateFormat.V1)
    {
        var (result, doc) = Wire.TryApply(update, format);
        Assert.True(result.Ok, $"{Hex.Of(update)} was rejected with {result.Error}");
        return doc;
    }

    /// <summary>A V2 update from hand-written columns (hex, empty by default; the string column defaults to its empty form) and rest section.</summary>
    public static byte[] V2(string rest, string info = "", string str = "00", string parentInfo = "", string client = "", string leftClock = "")
    {
        var cols = new[] { "", client, leftClock, "", info, str, parentInfo, "", "" };
        var bytes = new List<byte> { 0 };
        foreach (var c in cols)
        {
            var b = Hex.Bytes(c);
            bytes.Add((byte)b.Length);
            bytes.AddRange(b);
        }
        bytes.AddRange(Hex.Bytes(rest));
        return bytes.ToArray();
    }
}
