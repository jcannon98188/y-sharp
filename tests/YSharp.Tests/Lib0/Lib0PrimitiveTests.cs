using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using YSharp.Lib0;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Lib0;

/// <summary>
/// The lib0 encoder and decoder primitives (ENC-01..06, Q03, Q12, Q13, Q15), checked directly on the internal types. The expected
/// bytes are the lib0 0.2.99 oracle values listed in docs/research/encoding.md section 1.
/// </summary>
[Requires(Feature.Encoding)]
[Collection("RentedBuffers")]
public sealed class Lib0PrimitiveTests
{
    private static string WriteHex(Action<Lib0Writer> write)
    {
        using var w = new Lib0Writer();
        write(w);
        return Hex.Of(w.WrittenSpan);
    }

    public static IEnumerable<object[]> VarUintTable() => new[]
    {
        new object[] { 0UL, "00" }, new object[] { 127UL, "7f" }, new object[] { 128UL, "8001" }, new object[] { 300UL, "ac02" },
        new object[] { 16383UL, "ff7f" }, new object[] { 16384UL, "808001" }, new object[] { 1UL << 31, "8080808008" },
        new object[] { 4294967295UL, "ffffffff0f" }, new object[] { 1UL << 32, "8080808010" },
        new object[] { (1UL << 53) - 1, "ffffffffffffff0f" },
    };

    [Theory, Covers("ENC-02")]
    [MemberData(nameof(VarUintTable))]
    public void VarUint_RoundTripsAtTheByteBoundaries(ulong value, string hex)
    {
        Assert.Equal(hex, WriteHex(w => w.WriteVarUint(value)));
        var r = new Lib0Reader(Hex.Bytes(hex));
        Assert.True(r.TryReadVarUint(out var back));
        Assert.Equal(value, back);
        Assert.Equal(0, r.Remaining);
        Assert.Equal(YError.None, r.Error);
    }

    [Theory, Covers("ENC-02", "Q12")]
    [InlineData("80 00", 0UL)]
    [InlineData("81 00", 1UL)]
    [InlineData("80 80 80 80 80 80 80 80 00", 0UL)]
    public void VarUint_AcceptsOverlongButBoundedForms(string hex, ulong expected)
    {
        var r = new Lib0Reader(Hex.Bytes(hex));
        Assert.True(r.TryReadVarUint(out var v));
        Assert.Equal(expected, v);
    }

    [Theory, Covers("ENC-02", "Q12", "Q15")]
    [InlineData("")]                                                  // EOF before the first byte is an error, not 0
    [InlineData("80")]                                                // continuation then EOF
    [InlineData("ff ff ff")]
    [InlineData("80 80 80 80 80 80 80 10")]                           // 2^53
    [InlineData("ff ff ff ff ff ff ff ff 7f")]                        // far above 2^53
    [InlineData("80 80 80 80 80 80 80 80 80 80 00")]                  // 11 groups
    [InlineData("ff ff ff ff ff ff ff ff ff 01")]                     // 2^64-1
    public void VarUint_RejectsTruncationOverflowAndOverLongForms(string hex)
    {
        var r = new Lib0Reader(Hex.Bytes(hex));
        Assert.False(r.TryReadVarUint(out _));
        Assert.Equal(YError.Malformed, r.Error);
    }

    public static IEnumerable<object[]> VarIntTable() => new[]
    {
        new object[] { 0L, "00" }, new object[] { 1L, "01" }, new object[] { -1L, "41" }, new object[] { 63L, "3f" },
        new object[] { 64L, "8001" }, new object[] { -63L, "7f" }, new object[] { -64L, "c001" }, new object[] { 127L, "bf01" },
        new object[] { 8191L, "bf7f" }, new object[] { -8192L, "c08001" }, new object[] { 2147483647L, "bfffffff0f" },
        new object[] { -2147483647L, "ffffffff0f" }, new object[] { 1L << 32, "8080808020" },
    };

    [Theory, Covers("ENC-03")]
    [MemberData(nameof(VarIntTable))]
    public void VarInt_IsSignMagnitudeWithASixBitFirstGroup(long value, string hex)
    {
        Assert.Equal(hex, WriteHex(w => w.WriteVarInt(value)));
        var r = new Lib0Reader(Hex.Bytes(hex));
        Assert.True(r.TryReadVarInt(out var magnitude, out var negative));
        Assert.Equal(value, negative ? -(long)magnitude : (long)magnitude);
    }

    [Fact, Covers("ENC-03", "Q03", "Q13")]
    public void VarInt_NegativeZero_IsKeptApartFromZero()
    {
        Assert.Equal("40", WriteHex(w => w.WriteVarInt(0, negative: true)));
        Assert.Equal("00", WriteHex(w => w.WriteVarInt(0, negative: false)));
        var r = new Lib0Reader(Hex.Bytes("40 c0 00 00"));
        Assert.True(r.TryReadVarInt(out var m1, out var n1));
        Assert.True(r.TryReadVarInt(out var m2, out var n2));          // multi-byte -0 decodes to -0 as well
        Assert.True(r.TryReadVarInt(out var m3, out var n3));
        Assert.Equal((0UL, true, 0UL, true, 0UL, false), (m1, n1, m2, n2, m3, n3));
    }

    [Theory, Covers("ENC-03", "Q12", "Q15")]
    [InlineData("")]                                                  // JS reads 0 here; y-sharp rejects (ADR 0005)
    [InlineData("80")]
    [InlineData("c0 80")]
    [InlineData("80 80 80 80 80 80 80 80 20")]                        // magnitude above 2^53-1
    [InlineData("80 80 80 80 80 80 80 80 80 80 00")]
    public void VarInt_RejectsTruncationAndOverflow(string hex)
    {
        var r = new Lib0Reader(Hex.Bytes(hex));
        Assert.False(r.TryReadVarInt(out _, out _));
        Assert.Equal(YError.Malformed, r.Error);
    }

    [Fact, Covers("ENC-04", "ENC-06")]
    public void FixedWidthValues_AreBigEndian()
    {
        Assert.Equal("3fc00000", WriteHex(w => w.WriteFloat32(1.5f)));
        Assert.Equal("3fb999999999999a", WriteHex(w => w.WriteFloat64(0.1)));
        Assert.Equal("0000000000000005", WriteHex(w => w.WriteBigInt64(5)));
        Assert.Equal("8000000000000000", WriteHex(w => w.WriteBigInt64(long.MinValue)));
        Assert.Equal("ab", WriteHex(w => w.WriteUint8(0xab)));

        var r = new Lib0Reader(Hex.Bytes("3fc00000 3fb999999999999a 8000000000000000 ab"));
        Assert.True(r.TryReadFloat32(out var f));
        Assert.True(r.TryReadFloat64(out var d));
        Assert.True(r.TryReadBigInt64(out var l));
        Assert.True(r.TryReadUint8(out var b));
        Assert.Equal((1.5f, 0.1, long.MinValue, (byte)0xab), (f, d, l, b));
    }

    [Fact, Covers("ENC-04", "Q15")]
    public void FixedWidthReads_AtEof_AreMalformed()
    {
        var r = new Lib0Reader(Hex.Bytes("3f c0 00"));
        Assert.False(r.TryReadFloat32(out _));
        Assert.Equal(YError.Malformed, r.Error);
        Assert.False(new Lib0Reader(ReadOnlySpan<byte>.Empty).TryReadUint8(out _));
        Assert.False(new Lib0Reader(Hex.Bytes("00 00 00 00 00 00 00")).TryReadFloat64(out _));
        Assert.False(new Lib0Reader(Hex.Bytes("00 00 00 00 00 00 00")).TryReadBigInt64(out _));
    }

    [Fact, Covers("ENC-05")]
    public void VarString_IsLengthPrefixedUtf8()
    {
        Assert.Equal("00", WriteHex(w => w.WriteVarString("")));
        Assert.Equal("026162", WriteHex(w => w.WriteVarString("ab")));
        Assert.Equal("02c3a9", WriteHex(w => w.WriteVarString("é")));
        Assert.Equal("04f09f9880", WriteHex(w => w.WriteVarString("😀")));
        var r = new Lib0Reader(Hex.Bytes("04f09f9880"));
        Assert.True(r.TryReadVarString(out var s));
        Assert.Equal("😀", s);
    }

    [Fact, Covers("ENC-05")]
    public void VarString_LoneSurrogates_AreWrittenAsReplacementCharacters()
    {
        Assert.Equal("03efbfbd", WriteHex(w => w.WriteVarString("\ud800")));
        Assert.Equal("0561efbfbd62", WriteHex(w => w.WriteVarString("a\udc00b")));
    }

    [Fact, Covers("ENC-05")]
    public void VarString_KeepsABomAndAnEmbeddedNul()
    {
        var r = new Lib0Reader(Hex.Bytes("03efbbbf 03610062"));
        Assert.True(r.TryReadVarString(out var bom));
        Assert.True(r.TryReadVarString(out var nul));
        Assert.Equal("﻿", bom);
        Assert.Equal("a\0b", nul);
    }

    [Theory, Covers("ENC-05", "Q15")]
    [InlineData("01 ff")]               // invalid byte
    [InlineData("02 c3 28")]            // bad continuation
    [InlineData("02 c0 80")]            // overlong
    [InlineData("03 ed a0 80")]         // encoded surrogate
    [InlineData("04 f4 90 80 80")]      // above U+10FFFF
    [InlineData("05 61 62")]            // length past the end
    [InlineData("")]                    // EOF in the length
    public void VarString_RejectsInvalidUtf8AndTruncation(string hex)
    {
        var r = new Lib0Reader(Hex.Bytes(hex));
        Assert.False(r.TryReadVarString(out _));
        Assert.Equal(YError.Malformed, r.Error);
    }

    [Fact, Covers("ENC-05")]
    public void VarString_LimitIsAppliedToTheClaimBeforeAnythingIsRead()
    {
        // the claim (5) is above the limit (4) although only two bytes follow: LimitExceeded, not Malformed
        var over = new Lib0Reader(Hex.Bytes("05 61 62"), maxStringBytes: 4);
        Assert.False(over.TryReadVarString(out _));
        Assert.Equal(YError.LimitExceeded, over.Error);

        var equal = new Lib0Reader(Hex.Bytes("04 61 62 63 64"), maxStringBytes: 4);
        Assert.True(equal.TryReadVarString(out var s));
        Assert.Equal("abcd", s);
    }

    [Fact, Covers("ENC-05")]
    public void VarBytes_RoundTrip_AndAHugeClaimIsMalformedWithoutAllocating()
    {
        Assert.Equal("03010203", WriteHex(w => w.WriteVarBytes(new byte[] { 1, 2, 3 })));
        var r = new Lib0Reader(Hex.Bytes("03010203"));
        Assert.True(r.TryReadVarBytes(out var bytes));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes.ToArray());

        var huge = new Lib0Reader(Hex.Bytes("ff ff ff ff 0f 01"));
        Assert.False(huge.TryReadVarBytes(out _));
        Assert.Equal(YError.Malformed, huge.Error);
    }

    [Fact, Covers("ENC-01")]
    public void Writer_GrowsAcrossBuffersWithoutLosingBytes_AndResetsForReuse()
    {
        using var w = new Lib0Writer(16);
        var expected = new List<byte>();
        for (var i = 0; i < 5000; i++) { w.WriteVarUint((ulong)i * 977); Wire(expected, (ulong)i * 977); }
        Assert.Equal(expected.ToArray(), w.ToArray());
        w.Reset();
        Assert.Equal(0, w.Length);
        w.WriteUint8(7);
        Assert.Equal("07", Hex.Of(w.WrittenSpan));

        var sink = new ArrayBufferWriter<byte>();
        w.CopyTo(sink);
        Assert.Equal("07", Hex.Of(sink.WrittenSpan));
    }

    private static void Wire(List<byte> to, ulong v)
    {
        while (v > 127) { to.Add((byte)((v & 127) | 128)); v >>= 7; }
        to.Add((byte)v);
    }

    [Fact, Covers("ENC-01")]
    public void Writer_ReturnsItsBufferOnDispose_AndRejectsUseAfterwards()
    {
        var before = BufferPool.Outstanding;
        var w = new Lib0Writer();
        w.WriteVarUint(1);
        Assert.Equal(before + 1, BufferPool.Outstanding);
        w.Dispose();
        w.Dispose();
        Assert.Equal(before, BufferPool.Outstanding);
        Assert.Throws<ObjectDisposedException>(() => w.WriteUint8(1));
    }

    [Fact, Covers("ENC-01")]
    public void Writer_ReturnsEveryBufferItRentedWhileGrowing()
    {
        var before = BufferPool.Outstanding;
        using (var w = new Lib0Writer(16))
        {
            for (var i = 0; i < 100_000; i++) w.WriteUint8(1);
            Assert.Equal(before + 1, BufferPool.Outstanding);     // growth returns the old buffer
        }
        Assert.Equal(before, BufferPool.Outstanding);
    }

    [Fact, Covers("ENC-01")]
    public void Writer_ReturnsItsBufferWhenAWriteThrows()
    {
        var before = BufferPool.Outstanding;
        try
        {
            using var w = new Lib0Writer();
            w.WriteVarString("ok");
            throw new InvalidOperationException("an encode failed half way");
        }
        catch (InvalidOperationException) { }
        Assert.Equal(before, BufferPool.Outstanding);
    }
}
