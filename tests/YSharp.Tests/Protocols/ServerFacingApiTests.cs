using System;
using System.Buffers;
using System.Linq;
using Xunit;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Protocols;

/// <summary>
/// The core API the server tier reaches through (ADR 0024 SRV-2; TASK-5.6): sync messages written and framed with no <see cref="Doc"/>, and a
/// read-only decoded view of an update for the validation hook.
/// </summary>
[Requires(Feature.Sync, Feature.UpdateFunctions, Feature.V2, Feature.V2Functions, Feature.Text, Feature.TextFormat, Feature.Map, Feature.Array)]
public sealed class ServerFacingApiTests
{
    private static byte[] Sv(Doc doc) => SyncSupport.Write(w => doc.EncodeStateVector(w));

    [Fact, Covers("SRV-2")]
    public void TheDocFreeWriters_WriteWhatTheDocWritersWrite()
    {
        var doc = SyncSupport.DocWith(7, "hello");
        var sv = Sv(doc);
        var update = doc.EncodeStateAsUpdate();
        Assert.Equal(SyncSupport.Step1(doc), SyncSupport.Write(w => YSync.WriteStep1(w, sv)));
        Assert.Equal(SyncSupport.Step2(doc), SyncSupport.Write(w => YSync.WriteStep2(w, update)));
    }

    [Fact, Covers("SRV-2")]
    public void AFrame_ReadsTheTypeAndPayload_AndAdvancesPastIt()
    {
        var doc = SyncSupport.DocWith(7, "hello");
        var update = doc.EncodeStateAsUpdate();
        var both = SyncSupport.Step1(doc).Concat(SyncSupport.Update(update)).ToArray();
        ReadOnlySpan<byte> input = both;

        Assert.True(YSync.TryReadFrame(ref input, out var type, out var payload).Ok);
        Assert.Equal(SyncMessageType.Step1, type);
        Assert.Equal(Sv(doc), payload.ToArray());
        Assert.True(YSync.TryReadFrame(ref input, out type, out payload).Ok);
        Assert.Equal(SyncMessageType.Update, type);
        Assert.Equal(update, payload.ToArray());
        Assert.True(input.IsEmpty);
    }

    [Theory, Covers("SRV-2")]
    [InlineData(new byte[0], YError.Malformed)]
    [InlineData(new byte[] { 3, 0 }, YError.UnknownMessage)]
    [InlineData(new byte[] { 0x80 }, YError.Malformed)]
    [InlineData(new byte[] { 2, 5, 1, 2 }, YError.Malformed)]
    public void ABadFrame_IsAnErrorResult(byte[] message, YError expected)
    {
        ReadOnlySpan<byte> input = message;
        var status = YSync.TryReadFrame(ref input, out _, out var payload);
        Assert.False(status.Ok);
        Assert.Equal(expected, status.Error);
        Assert.True(payload.IsEmpty);
    }

    [Theory, Covers("SRV-2")]
    [InlineData(UpdateFormat.V1)]
    [InlineData(UpdateFormat.V2)]
    public void ADecodedUpdate_ListsItsStructsAndDeletions(UpdateFormat format)
    {
        var doc = new Doc(new DocOptions { ClientId = 9 });
        var text = doc.GetText("text");
        text.Insert(0, "abc");
        text.Delete(1, 1);
        text.Format(0, 1, YValue.Object(new[] { new System.Collections.Generic.KeyValuePair<string, YValue>("bold", true) }));
        doc.GetMap("map").Set("k", (YValue)42);
        doc.GetArray("array").Insert(0, new YEntry[] { new YMap() });
        var update = doc.EncodeStateAsUpdate(default, format);

        Assert.True(YUpdate.TryDecode(update, out var decoded, format).Ok);
        Assert.False(decoded.IsEmpty);
        Assert.All(decoded.Structs, s => Assert.Equal(9ul, s.Id.Client));
        Assert.Contains(decoded.Structs, s => s.Content == UpdateContentKind.String && s.ParentRoot == "text" && s.Text == "a");
        Assert.Contains(decoded.Structs, s => s.Content == UpdateContentKind.Deleted && s.Length == 1);
        Assert.Contains(decoded.Structs, s => s.Content == UpdateContentKind.Format && s.Key == "bold" && s.Values.Span[0].Equals((YValue)true));
        Assert.Contains(decoded.Structs, s => s.Content == UpdateContentKind.Any && s.ParentRoot == "map" && s.ParentSub == "k");
        Assert.Contains(decoded.Structs, s => s.Content == UpdateContentKind.Type && s.ParentRoot == "array" && s.TypeName == "YMap");
        Assert.Equal(new UpdateDeleteRange(9, 1, 1), Assert.Single(decoded.Deletes));
        Assert.Equal(decoded.Structs.Sum(s => s.Length), doc.GetStateVector()[9]);
    }

    [Fact, Covers("SRV-2")]
    public void DecodingAnEmptyOrBadUpdate_GivesAnEmptyViewOrAnError()
    {
        Assert.True(YUpdate.TryDecode(new byte[] { 0, 0 }, out var empty).Ok);
        Assert.True(empty.IsEmpty);
        Assert.False(YUpdate.TryDecode(new byte[] { 1, 1, 9 }, out _).Ok);
        Assert.Equal(YError.LimitExceeded, YUpdate.TryDecode(SyncSupport.DocWith(1, "abcdef").EncodeStateAsUpdate(), out _, UpdateFormat.V1, YLimits.Default with { MaxUpdateBytes = 4 }).Error);
    }
}
