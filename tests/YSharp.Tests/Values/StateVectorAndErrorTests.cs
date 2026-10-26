using System;
using System.Buffers;
using System.Linq;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Values;

/// <summary>StateVector, ItemId and the error family (ADR 0005, 0008). None of it needs a document.</summary>
[Requires(Feature.Values)]
public sealed class StateVectorAndErrorTests
{
    private static string Encoded(StateVector sv)
    {
        var w = new ArrayBufferWriter<byte>();
        sv.Encode(w);
        return Hex.Of(w.WrittenSpan);
    }

    [Fact, Covers("ENC-09")]
    public void Decode_ListsClientsDescending_AndUnknownClientsAreAtClockZero()
    {
        var sv = StateVector.Decode(Hex.Bytes("02ac02030502"));
        Assert.Equal(new[] { (300UL, 3L), (5UL, 2L) }, sv.Clients.Select(c => (c.Key, c.Value)).ToArray());
        Assert.Equal(3L, sv[300]);
        Assert.Equal(2L, sv[5]);
        Assert.Equal(0L, sv[999]);
        Assert.Equal("02ac02030502", Encoded(sv));
    }

    [Fact, Covers("ENC-09")]
    public void Empty_IsASingleZeroByte()
    {
        var sv = StateVector.Decode(Hex.Bytes("00"));
        Assert.Empty(sv.Clients);
        Assert.Equal("00", Encoded(sv));
    }

    [Fact, Covers("ENC-09")]
    public void Encode_SortsUnorderedInput_AndTheLastDuplicateWins()
    {
        var sv = StateVector.Decode(Hex.Bytes("03 05 01 07 02 05 09"));
        Assert.Equal(new[] { (7UL, 2L), (5UL, 9L) }, sv.Clients.Select(c => (c.Key, c.Value)).ToArray());
        Assert.Equal("02070205" + "09", Encoded(sv));
    }

    [Fact, Covers("ENC-02")]
    public void Decode_AcceptsOverlongForms_AndTrailingBytes()
    {
        Assert.Equal(1L, StateVector.Decode(Hex.Bytes("01 01 81 00"))[1]);
        Assert.Equal(0L, StateVector.Decode(Hex.Bytes("01 01 80 00"))[1]);
        Assert.Equal(2L, StateVector.Decode(Hex.Bytes("01 01 02 ff ff"))[1]);
    }

    [Fact, Covers("ENC-37", "Q12")]
    public void Decode_AcceptsIdsAndClocksUpTo2Pow53Minus1()
    {
        const ulong max = (1UL << 53) - 1;
        var sv = StateVector.Decode(Hex.Bytes("01 ff ff ff ff ff ff ff 0f ff ff ff ff ff ff ff 0f"));
        Assert.Equal((long)max, sv[max]);
        Assert.Equal("01ffffffffffffff0fffffffffffffff0f", Encoded(sv));
    }

    [Theory, Covers("ENC-02", "Q12")]
    [InlineData("01 01 80 80 80 80 80 80 80 10")]                       // clock 2^53
    [InlineData("01 80 80 80 80 80 80 80 10 01")]                       // client 2^53
    [InlineData("01 01 ff ff ff ff ff ff ff ff 7f")]                    // a ten-byte value above 2^53
    [InlineData("01 01 80 80 80 80 80 80 80 80 80 80 00")]              // eleven groups
    [InlineData("")]                                                    // EOF
    [InlineData("01")]                                                  // the entry is missing
    [InlineData("01 01")]                                               // the clock is missing
    [InlineData("02 01 01")]                                            // the second entry is missing
    [InlineData("ff ff ff ff 0f 01 01")]                                // a count that cannot fit the bytes left
    public void Decode_Malformed_ThrowsYDecodeException_AndTryDecodeReturnsTheCode(string hex)
    {
        var bytes = Hex.Bytes(hex);
        var thrown = Assert.Throws<YDecodeException>(() => StateVector.Decode(bytes));
        Assert.Equal(YError.Malformed, thrown.Error);
        var status = StateVector.TryDecode(bytes, out var sv);
        Assert.False(status.Ok);
        Assert.Equal(YError.Malformed, status.Error);
        Assert.Null(sv);
    }

    [Fact]
    public void TryDecode_OnSuccess_ReturnsOk()
    {
        var status = StateVector.TryDecode(Hex.Bytes("01 01 05"), out var sv);
        Assert.True(status.Ok);
        Assert.Equal(YError.None, status.Error);
        Assert.Equal(5L, sv![1]);
    }

    [Fact]
    public void Decode_AHugeCountClaim_AllocatesNothingLikeIt()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var status = StateVector.TryDecode(Hex.Bytes("ff ff ff ff 0f 01 01"), out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(status.Ok);
        Assert.True(allocated < 64 * 1024, $"{allocated} bytes allocated for a 7-byte input");
    }

    // ------------------------------------------------------------------ ids

    [Fact]
    public void ItemId_IsAValue_WithAUlongClientAndALongClock()
    {
        var id = new ItemId(1UL << 52, (1L << 53) - 1);
        Assert.Equal(id, new ItemId(1UL << 52, (1L << 53) - 1));
        Assert.NotEqual(id, new ItemId(1UL << 52, 0));
        Assert.Equal(1UL << 52, id.Client);
    }

    // ------------------------------------------------------------------ errors and limits

    [Fact, Covers("ERR-01")]
    public void YStatus_DefaultsToOk_AndCarriesOnlyACode()
    {
        Assert.True(default(YStatus).Ok);
        Assert.Equal(YError.None, default(YStatus).Error);
        var failed = new YStatus(YError.LimitExceeded);
        Assert.False(failed.Ok);
        Assert.Equal(YError.LimitExceeded, failed.Error);
    }

    [Fact, Covers("ERR-01")]
    public void YDecodeException_CarriesTheCode_AndIsAYException()
    {
        var e = new YDecodeException(YError.LimitExceeded);
        Assert.Equal(YError.LimitExceeded, e.Error);
        Assert.IsAssignableFrom<YException>(e);
        Assert.False(string.IsNullOrEmpty(e.Message));
    }

    [Fact]
    public void EveryProgrammerErrorType_IsAYException_ButNotADecodeError()
    {
        Exception[] all =
        {
            new YConcurrencyException("c"), new YLifetimeException("l"), new YInternalException("i"), new YTypeConflictException("t"),
            new YInternalException("i", new InvalidOperationException()),
        };
        foreach (var e in all)
        {
            Assert.IsAssignableFrom<YException>(e);
            Assert.IsNotType<YDecodeException>(e);
            Assert.False(string.IsNullOrEmpty(e.Message));
        }
        Assert.IsType<InvalidOperationException>(all[^1].InnerException);
    }

    [Fact]
    public void YLimits_Defaults_AreTheSafeValuesOfAdr0005()
    {
        var d = YLimits.Default;
        Assert.Equal(64 * 1024 * 1024, d.MaxUpdateBytes);
        Assert.Equal(128, d.MaxAnyDepth);
        Assert.Equal(10_000_000, d.MaxStructsPerUpdate);
        Assert.Equal(64 * 1024 * 1024, d.MaxPendingBytes);
        Assert.Equal(64 * 1024 * 1024, d.MaxStringBytes);
        Assert.Same(YLimits.Default, YLimits.Default);
        var tight = d with { MaxAnyDepth = 4 };
        Assert.Equal(4, tight.MaxAnyDepth);
        Assert.Equal(128, d.MaxAnyDepth);
    }

    [Fact]
    public void YLimits_NegativeValues_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new YLimits { MaxUpdateBytes = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new YLimits { MaxAnyDepth = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new YLimits { MaxStructsPerUpdate = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new YLimits { MaxPendingBytes = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new YLimits { MaxStringBytes = -1 });
        Assert.Equal(0, new YLimits { MaxAnyDepth = 0 }.MaxAnyDepth);       // zero is a valid (if strict) limit
    }
}
