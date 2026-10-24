using Xunit;
using YSharp.Fuzz.Builder;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;

namespace YSharp.Fuzz.Tests;

/// <summary>
/// The structure-aware builder must agree with the oracle byte for byte on valid input, or its hostile variations prove nothing.
/// These tests need no y-sharp implementation: the expected bytes are the Node-recorded ones in the fixtures and the notes.
/// </summary>
[LibraryIndependent]
public sealed class BuilderSelfTests
{
    private const string HiV1 = "010101000401017402686900";
    private const string HiV2 = "0000010100000104060374686901020101000001010000";

    [Fact]
    public void Builder_V1HiUpdate_MatchesTheOracleBytes() =>
        Assert.Equal(HiV1, Hex.Of(UpdateBuilder.V1(UpdateBuilder.TextInsert(1, "t", "hi"))));

    [Fact]
    public void Builder_V2HiUpdate_MatchesTheOracleBytes() =>
        Assert.Equal(HiV2, Hex.Of(UpdateBuilder.V2(UpdateBuilder.TextInsert(1, "t", "hi"))));

    [Fact]
    public void Builder_V1MapUpdate_MatchesTheEncodingNotes()
    {
        // encoding.md 3.6: m.set('k',{a:1}); m.set('s','v'); m.set('k',2) from client 1, the first struct already ContentDeleted
        var model = new UpdateModel(new[]
        {
            new ClientBlock(1, 0, new[]
            {
                Struct.InRoot("m", Content.DeletedOf(1), parentSub: "k"),
                Struct.InRoot("m", Content.AnyOf(1, 0x77, 0x01, 0x76), parentSub: "s"),
                Struct.InRoot("m", Content.AnyOf(1, 0x7d, 0x02), origin: new Id(1, 0), parentSub: "k"),
            }),
        }, new[] { new DeleteClient(1, new[] { new DeleteRange(0, 1) }) });
        Assert.Equal("01030100" + "2101016d016b01" + "2801016d0173" + "01770176" + "a8010001" + "7d02" + "0101010001", Hex.Of(UpdateBuilder.V1(model)));
    }

    [Fact]
    public void V2_Encoders_FollowTheDocumentedSamples()
    {
        Assert.Equal("01", Hex.Of(V2Encoders.Rle(new byte[] { 1, 1, 1 })));
        Assert.Equal("010002", Hex.Of(V2Encoders.Rle(new byte[] { 1, 2 })));
        Assert.Equal("000105", Hex.Of(V2Encoders.Rle(new byte[] { 0, 0, 5 })));
        Assert.Equal("4101", Hex.Of(V2Encoders.UintOptRle(new ulong[] { 1, 1, 1 })));
        Assert.Equal("4000", Hex.Of(V2Encoders.UintOptRle(new ulong[] { 0, 0 })));
        Assert.Equal("4001", Hex.Of(V2Encoders.UintOptRle(new ulong[] { 0, 0, 0 })));
        Assert.Equal("014200", Hex.Of(V2Encoders.UintOptRle(new ulong[] { 1, 2, 2 })));
        Assert.Equal("0301", Hex.Of(V2Encoders.IntDiffOptRle(new ulong[] { 1, 2, 3 })));
        Assert.Equal("0a00", Hex.Of(V2Encoders.IntDiffOptRle(new ulong[] { 5, 5 })));
        Assert.Equal("0101", Hex.Of(V2Encoders.IntDiffOptRle(new ulong[] { 0, 0, 0 })));
        Assert.Equal("0644", Hex.Of(V2Encoders.IntDiffOptRle(new ulong[] { 3, 1 })));
        Assert.Equal("03616263" + "0102", Hex.Of(V2Encoders.Strings(new[] { "a", "bc" })));
        Assert.Equal("0000", Hex.Of(V2Encoders.Strings(new[] { "" })));
    }

    [Fact]
    public void Builder_DeleteSet_UsesTheLayoutsOfTheEncodingNotes()
    {
        // encoding.md 4.3: client 1 ranges (2,2) and (8,1): V1 ds 01 01 02 02 02 08 01, V2 ds 01 01 02 02 01 04 00
        var model = UpdateModel.Empty.WithDeletes(new DeleteClient(1, new[] { new DeleteRange(2, 2), new DeleteRange(8, 1) }));
        Assert.EndsWith("01010202" + "0208" + "01", Hex.Of(UpdateBuilder.V1(model)));
        Assert.EndsWith("01010202" + "0104" + "00", Hex.Of(UpdateBuilder.V2(model)));
    }

    [Fact]
    public void Builder_NestedTypesV1_AgreesWithTheModelWriter() =>
        Assert.Equal(Hex.Of(UpdateBuilder.V1(UpdateBuilder.NestedTypes(1, 300))), Hex.Of(UpdateBuilder.NestedTypesV1(1, 300)));

    [Fact]
    public void ByteSink_VarUint_WritesTheTenByteForms()
    {
        Assert.Equal("ffffffffffffffffff01", Hex.Of(new ByteSink().VarUint(ulong.MaxValue).ToArray()));
        Assert.Equal("80808080808080808001", Hex.Of(new ByteSink().VarUint(1UL << 63).ToArray()));
    }

    [Fact]
    public void Builder_Frames_MatchTheProtocolLayouts()
    {
        Assert.Equal("000105", Hex.Of(UpdateBuilder.SyncMessage(0, new byte[] { 5 })));
        Assert.Equal("010703027b7d", Hex.Of(UpdateBuilder.AwarenessUpdate((7, 3, "{}"))));
    }
}
