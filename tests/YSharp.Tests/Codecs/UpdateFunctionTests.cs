using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Assertions;

namespace YSharp.Tests.Codecs;

/// <summary>
/// The stateless update functions (<see cref="YUpdate"/>): merge, diff, state vector from an update and format conversion
/// (ENC-23 to ENC-25). The oracle's byte-for-byte results for larger inputs are replayed by <see cref="WireFixtureTests"/>.
/// </summary>
public sealed class UpdateFunctionTests
{
    private const string HiV1 = "010101000401017402686900";
    private const string HiV2 = "0000010100000104060374686901020101000001010000";

    private static readonly byte[] U0 = Hex.Bytes("0101010004010174016100");
    private static readonly byte[] U1 = Hex.Bytes("01010101840100016200");
    private static readonly byte[] U2 = Hex.Bytes("01010102840101016300");

    /// <summary>Two clients, text, a map, an array and deletes: enough variety to exercise every struct kind in the functions.</summary>
    private static (Doc Doc, List<byte[]> Updates) Busy(UpdateFormat format)
    {
        var a = Wire.NewDoc(1);
        var b = Wire.NewDoc(2);
        var updates = new List<byte[]>();
        void Record(ReadOnlySpan<byte> u) => updates.Add(u.ToArray());
        if (format == UpdateFormat.V1) { a.Update += (u, _, _, _) => Record(u); b.Update += (u, _, _, _) => Record(u); }
        else { a.UpdateV2 += (u, _, _, _) => Record(u); b.UpdateV2 += (u, _, _, _) => Record(u); }

        a.GetText("t").Insert(0, "hello");
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        b.GetText("t").Insert(5, " world");
        a.GetMap("m").Set("k", "v");
        a.ApplyUpdate(b.EncodeStateAsUpdate());
        a.GetText("t").Delete(1, 2);
        b.GetArray("a").Insert(0, new YEntry[] { 1, 2, 3 });
        b.GetArray("a").Delete(1, 1);
        a.ApplyUpdate(b.EncodeStateAsUpdate());
        return (a, updates);
    }

    [Theory, Covers("ENC-23")]
    [InlineData(UpdateFormat.V1)]
    [InlineData(UpdateFormat.V2)]
    public void Enc23_Merge_OfAllUpdates_AppliesToTheSameStateInAnyOrder(UpdateFormat format) => Stub.Run(() =>
    {
        var (doc, updates) = Busy(format);
        var reversed = Enumerable.Reverse(updates).ToArray();
        var doubled = updates.Concat(updates).ToArray();
        foreach (var set in new[] { updates.ToArray(), reversed, doubled })
        {
            var merged = Wire.Merge(format, set);
            var peer = new Doc();
            peer.ApplyUpdate(merged, null, format);
            Assert.Equal(doc.GetText("t").ToString(), peer.GetText("t").ToString());
            Assert.Equal(Hex.Of(Wire.StateVector(doc)), Hex.Of(Wire.StateVector(peer)));
            Assert.Equal(Hex.Of(Wire.StateVector(doc)), Hex.Of(Wire.StateVectorOfUpdate(merged, format)));
        }
    });

    [Fact, Covers("ENC-23", "ENC-24")]
    public void Enc23_Merge_RejectsMalformedInput_AndWritesNothing() => Stub.Run(() =>
    {
        var output = new ArrayBufferWriter<byte>();
        var truncated = new ReadOnlyMemory<byte>(Hex.Bytes(HiV1)[..8]);
        var status = YUpdate.TryMerge(new[] { truncated, (ReadOnlyMemory<byte>)U0 }, output);
        Assert.False(status.Ok);
        Assert.Equal(YError.Malformed, status.Error);
        Assert.False(YUpdate.TryMerge(new[] { (ReadOnlyMemory<byte>)Hex.Bytes(HiV1), Hex.Bytes(HiV1) }, new ArrayBufferWriter<byte>(), UpdateFormat.V2).Ok);
    });

    [Fact, Covers("ENC-24")]
    public void Enc24_Diff_KeepsOnlyWhatTheStateVectorLacks() => Stub.Run(() =>
    {
        var all = Wire.Merge(UpdateFormat.V1, U0, U1, U2);
        var diff = Wire.Diff(all, Hex.Bytes("010101"));                  // the target already has client 1 up to clock 1
        Assert.Equal("010201018401000162840101016300", Hex.Of(diff));
        var peer = new Doc();
        peer.ApplyUpdate(U0);
        peer.ApplyUpdate(diff);
        Assert.Equal("abc", peer.GetText("t").ToString());

        // a target that is ahead of, or equal to, the update leaves nothing but the empty update 0000
        var nothing = Wire.Diff(all, Hex.Bytes("010103"));
        Assert.True(Wire.TryApply(nothing).Result.Ok);
        Assert.True(Wire.IsEmpty(StateVector.Of(Wire.TryApply(nothing).Doc)));
        // an empty state vector asks for everything
        Assert.Equal("abc", Wire.TryApply(Wire.Diff(all, Hex.Bytes("00"))).Doc.GetText("t").ToString());
    });

    [Fact, Covers("ENC-24")]
    public void Enc24_Diff_RejectsAMalformedStateVector() => Stub.Run(() =>
    {
        var status = YUpdate.TryDiff(U0, Hex.Bytes("02 01"), new ArrayBufferWriter<byte>());
        Assert.False(status.Ok);
        Assert.Equal(YError.Malformed, status.Error);
    });

    [Fact, Covers("ENC-25")]
    public void Enc25_Convert_GivesTheNativeBytesOfTheOtherFormat() => Stub.Run(() =>
    {
        Assert.Equal(HiV2, Hex.Of(Wire.Convert(Hex.Bytes(HiV1), UpdateFormat.V1, UpdateFormat.V2)));
        Assert.Equal(HiV1, Hex.Of(Wire.Convert(Hex.Bytes(HiV2), UpdateFormat.V2, UpdateFormat.V1)));
        var (doc, _) = Busy(UpdateFormat.V1);
        var v1 = Wire.Encode(doc);
        var v2 = Wire.Encode(doc, UpdateFormat.V2);
        Assert.Equal(Hex.Of(v2), Hex.Of(Wire.Convert(v1, UpdateFormat.V1, UpdateFormat.V2)));
        Assert.Equal(Hex.Of(v1), Hex.Of(Wire.Convert(v2, UpdateFormat.V2, UpdateFormat.V1)));
        Assert.Equal(Hex.Of(v1), Hex.Of(Wire.Convert(Wire.Convert(v1, UpdateFormat.V1, UpdateFormat.V2), UpdateFormat.V2, UpdateFormat.V1)));
    });

    [Fact, Covers("ENC-25")]
    public void Enc25_Convert_RejectsTheWrongFormat_AndTruncatedInput() => Stub.Run(() =>
    {
        foreach (var (bytes, from, to) in new[]
        {
            (Hex.Bytes(HiV1), UpdateFormat.V2, UpdateFormat.V1),
            (Hex.Bytes(HiV1)[..6], UpdateFormat.V1, UpdateFormat.V2),
            (Array.Empty<byte>(), UpdateFormat.V1, UpdateFormat.V2),
            (Hex.Bytes(HiV2)[..10], UpdateFormat.V2, UpdateFormat.V1),
        })
        {
            var output = new ArrayBufferWriter<byte>();
            var status = YUpdate.TryConvert(bytes, from, to, output);
            Assert.False(status.Ok);
            Assert.Equal(YError.Malformed, status.Error);
        }
    });

    [Fact, Covers("ENC-27", "ENC-84")]
    public void Enc27_EveryEmissionSource_ProducesUpdatesThatApplyToTheSameState() => Stub.Run(() =>
    {
        var (doc, events) = Busy(UpdateFormat.V1);
        var fromState = Wire.Encode(doc);
        var fromMerge = Wire.Merge(UpdateFormat.V1, events.ToArray());
        var fromDiff = Wire.Diff(fromState, Hex.Bytes("00"));
        foreach (var update in new[] { fromState, fromMerge, fromDiff, Wire.Convert(Wire.Convert(fromState, UpdateFormat.V1, UpdateFormat.V2), UpdateFormat.V2, UpdateFormat.V1) })
        {
            var peer = new Doc();
            peer.ApplyUpdate(update);
            Assert.Equal(doc.GetText("t").ToString(), peer.GetText("t").ToString());
            Assert.Equal(Hex.Of(Wire.StateVector(doc)), Hex.Of(Wire.StateVector(peer)));
        }
    });
}
