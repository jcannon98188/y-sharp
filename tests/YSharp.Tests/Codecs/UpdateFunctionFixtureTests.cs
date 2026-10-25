using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Values;

namespace YSharp.Tests.Codecs;

/// <summary>
/// The V1 update functions on the oracle's recorded inputs and results, with no document or shared type: every V1 <c>mergeUpdates</c> and
/// <c>diffUpdate</c> step of the codec fixtures carries its input bytes, and its result must match byte for byte. State vectors from updates
/// are checked against the pinned Yjs (<c>encodeStateVectorFromUpdate</c>, recorded in each case).
/// </summary>
[Requires(Feature.UpdateFunctions)]
public sealed class UpdateFunctionFixtureTests
{
    private static readonly string[] Fixtures = { "codecs/merge-diff-convert.json", "encoding/merge-diff-convert-edges.json" };

    public static IEnumerable<object[]> V1Steps() =>
        from f in Fixtures
        from s in FixtureReplay.Load(f).Script.GetProperty("steps").EnumerateArray()
        let kind = s.GetProperty("step").GetString()
        where kind is "mergeUpdates" or "diffUpdate" && !s.TryGetProperty("encoding", out _)
        select new object[] { f, s.GetProperty("name").GetString()! };

    private static byte[] BytesOf(JsonElement e) => TaggedValue.Read(e).Bytes;

    [Theory, Covers("ENC-23", "ENC-24", "ENC-81", "ENC-82", "ENC-83")]
    [MemberData(nameof(V1Steps))]
    public void V1Step_GivesTheOraclesBytes(string fixture, string name)
    {
        var fx = FixtureReplay.Load(fixture);
        var step = fx.Script.GetProperty("steps").EnumerateArray().Single(s => s.TryGetProperty("name", out var n) && n.GetString() == name);
        var expected = BytesOf(fx.Observations.GetProperty("results").GetProperty("value").GetProperty(name).GetProperty("bytes"));
        var output = new ArrayBufferWriter<byte>();
        YStatus status;
        if (step.GetProperty("step").GetString() == "mergeUpdates")
            status = YUpdate.TryMerge(step.GetProperty("updates").EnumerateArray().Select(u => (ReadOnlyMemory<byte>)BytesOf(u)).ToList(), output);
        else
            status = YUpdate.TryDiff(BytesOf(step.GetProperty("update")), BytesOf(step.GetProperty("sv")), output);
        Assert.True(status.Ok, $"{fixture} {name}: {status.Error}");
        HexAssert.Equal(expected, output.WrittenSpan, $"{fixture} {name}");
    }

    [Theory, Covers("ENC-21")]
    // oracle (Yjs 13.6.33, Y.encodeStateVectorFromUpdate): a client must start at clock 0; a Skip stops the count; clients by update order
    [InlineData("0101010004010174016100", "010101")]
    [InlineData("01010104c401000101017a00", "00")]
    [InlineData("010301000401017401610a03c401000101017a00", "010101")]
    [InlineData("020102008401010563f09f98800101000401017402616200", "0202030102")]
    [InlineData("0000", "00")]
    public void StateVectorFromUpdate_GivesTheOraclesBytes(string update, string expected)
    {
        var output = new ArrayBufferWriter<byte>();
        Assert.True(YUpdate.TryStateVectorFromUpdate(Hex.Bytes(update), output).Ok);
        Assert.Equal(expected, Hex.Of(output.WrittenSpan.ToArray()));
    }

    [Fact, Covers("ENC-23", "ERR-05")]
    public void AMalformedInput_FailsTheWholeMerge_AndWritesNothing()
    {
        var output = new ArrayBufferWriter<byte>();
        var status = YUpdate.TryMerge(new ReadOnlyMemory<byte>[] { Hex.Bytes("0101010004010174016100"), Hex.Bytes("01 01 01") }, output);
        Assert.Equal(YError.Malformed, status.Error);
        Assert.Equal(0, output.WrittenCount);
        Assert.Equal(YError.LimitExceeded, YUpdate.TryMerge(new ReadOnlyMemory<byte>[] { Hex.Bytes("0000"), Hex.Bytes("0000") }, output,
            limits: new YLimits { MaxUpdateBytes = 1 }).Error);
        Assert.Equal(0, output.WrittenCount);
    }

    [Fact, Covers("ENC-85")]
    public void ASkipWithFlagBits_IsASkipWhenApplied_ButMalformedForTheFunctions()
    {
        var skip = Hex.Bytes("01 01 01 00 8a 02 00");
        Assert.True(new Doc().TryApplyUpdate(skip).Ok);
        var output = new ArrayBufferWriter<byte>();
        Assert.Equal(YError.Malformed, YUpdate.TryStateVectorFromUpdate(skip, output).Error);
        Assert.Equal(YError.Malformed, YUpdate.TryDiff(skip, Hex.Bytes("00"), output).Error);
        Assert.True(YUpdate.TryValidate(skip).Ok);
        Assert.Equal(YError.Malformed, YUpdate.TryValidate(Hex.Bytes("01 01 01")).Error);
    }
}
