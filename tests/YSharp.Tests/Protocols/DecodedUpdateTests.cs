using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Values;
using YSharp.Tests.Codecs;

namespace YSharp.Tests.Protocols;

/// <summary>
/// A decoded update taken by the paths that otherwise take its bytes (TASK-14): a server decodes a peer's update once, then prepares it on the
/// relay, applies it to the hot document and writes the stored record from that decode. Each path must give what the bytes give.
/// </summary>
[Requires(Feature.UpdateFunctions, Feature.V2, Feature.V2Functions, Feature.Text, Feature.Map)]
public sealed class DecodedUpdateTests
{
    private static readonly string[] Fixtures = { "codecs/merge-diff-convert.json", "encoding/merge-diff-convert-edges.json" };

    // hand-made V1 updates: client 1 in two blocks (a document keeps the later one), two GC runs that a document joins, and a Skip between
    private static readonly string[] Shapes =
    {
        "02 01 01 00 04 01 01 74 01 61 01 01 01 84 01 00 01 62 00",
        "01 02 01 00 00 01 00 02 00",
        "01 03 01 00 04 01 01 74 01 61 0a 03 c4 01 00 01 01 01 7a 00",
        "02 01 01 00 00 02 01 01 01 00 01 00",
        "00 02 00 05 00 00 00 00 00 00 00 00 00 00 00 01 00 01 00 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00",
    };

    private static DecodedUpdate Decode(byte[] update, UpdateFormat format = UpdateFormat.V1, YLimits? limits = null)
    {
        Assert.True(YUpdate.TryDecode(update, out var decoded, format, limits).Ok);
        return decoded;
    }

    /// <summary>The V1 updates to check: every input and result of the oracle's merge and diff steps, the hand-made shapes, and edits.</summary>
    public static IEnumerable<object[]> Cases()
    {
        var all = new List<(string Name, byte[] Bytes)>();
        foreach (var f in Fixtures)
        {
            var fx = FixtureReplay.Load(f);
            foreach (var s in fx.Script.GetProperty("steps").EnumerateArray())
            {
                if (s.TryGetProperty("encoding", out _)) continue;
                var kind = s.GetProperty("step").GetString();
                if (kind == "mergeUpdates")
                {
                    var i = 0;
                    foreach (var u in s.GetProperty("updates").EnumerateArray()) all.Add(($"{f} {s.GetProperty("name").GetString()} [{i++}]", TaggedValue.Read(u).Bytes));
                }
                else if (kind == "diffUpdate") all.Add(($"{f} {s.GetProperty("name").GetString()}", TaggedValue.Read(s.GetProperty("update")).Bytes));
            }
        }

        for (var i = 0; i < Shapes.Length; i++) all.Add(($"shape {i}", Hex.Bytes(Shapes[i])));
        foreach (var (u, i) in Edits(5, 40).Select((u, i) => (u, i))) all.Add(($"edit {i}", u));
        return all.Where(c => YUpdate.TryDecode(c.Bytes, out _).Ok).Select(c => new object[] { c.Name, Hex.Of(c.Bytes) });
    }

    /// <summary>Two clients editing a text and a map, each update as a V1 update, plus their merge with every third update left out (Skips).</summary>
    private static List<byte[]> Edits(int seed, int count)
    {
        var rng = new Random(seed);
        var docs = new[] { new Doc(new DocOptions { ClientId = 1 }), new Doc(new DocOptions { ClientId = 2 }) };
        var updates = new List<byte[]>();
        foreach (var d in docs) d.Update += (u, _, _, _) => updates.Add(u.ToArray());
        for (var i = 0; i < count; i++)
        {
            var doc = docs[rng.Next(2)];
            var text = doc.GetText("t");
            if (text.Length > 0 && rng.Next(3) == 0) text.Delete(rng.Next(text.Length), 1);
            else if (rng.Next(4) == 0) doc.GetMap("m").Set($"k{rng.Next(3)}", (YValue)i);
            else text.Insert(rng.Next(text.Length + 1), new string((char)('a' + rng.Next(26)), rng.Next(1, 4)));
            if (rng.Next(3) == 0)
            {
                var other = docs[0] == doc ? docs[1] : docs[0];
                { var sv = new ArrayBufferWriter<byte>(); other.EncodeStateVector(sv); other.ApplyUpdate(doc.EncodeStateAsUpdate(sv.WrittenSpan)); }
            }
        }

        var merged = new ArrayBufferWriter<byte>();
        Assert.True(YUpdate.TryMerge(updates.Where((_, i) => i % 3 != 1).Select(u => (ReadOnlyMemory<byte>)u).ToList(), merged).Ok);
        updates.Add(merged.WrittenSpan.ToArray());
        return updates;
    }

    [Theory, Covers("SRV-2")]
    [MemberData(nameof(Cases))]
    public void Encoding_WritesWhatConvertingTheBytesWrites(string name, string hex)
    {
        var update = Hex.Bytes(hex);
        var decoded = Decode(update);
        foreach (var format in new[] { UpdateFormat.V1, UpdateFormat.V2 })
        {
            var converted = new ArrayBufferWriter<byte>();
            Assert.True(YUpdate.TryConvert(update, UpdateFormat.V1, format, converted).Ok);
            var encoded = new ArrayBufferWriter<byte>();
            decoded.Encode(encoded, format);
            HexAssert.Equal(converted.WrittenSpan, encoded.WrittenSpan, $"{name} {format}");

            // and decoding the V2 form gives the same again
            var again = new ArrayBufferWriter<byte>();
            Decode(encoded.WrittenSpan.ToArray(), format).Encode(again, UpdateFormat.V1);
            HexAssert.Equal(Converted(update), again.WrittenSpan, $"{name} {format} round trip");
        }
    }

    private static byte[] Converted(byte[] update)
    {
        var v1 = new ArrayBufferWriter<byte>();
        Assert.True(YUpdate.TryConvert(update, UpdateFormat.V1, UpdateFormat.V1, v1).Ok);
        return v1.WrittenSpan.ToArray();
    }

    [Theory, Covers("SRV-2")]
    [MemberData(nameof(Cases))]
    public void ApplyingIt_GivesWhatApplyingTheBytesGives(string name, string hex)
    {
        var update = Hex.Bytes(hex);
        var bytes = new Doc(new DocOptions { ClientId = 99 });
        var decoded = new Doc(new DocOptions { ClientId = 99 });
        var a = bytes.TryApplyUpdate(update);
        var b = decoded.TryApplyUpdate(Decode(update));
        Assert.Equal((a.Error, a.RejectedStructs), (b.Error, b.RejectedStructs));
        HexAssert.Equal(bytes.EncodeStateAsUpdate(), decoded.EncodeStateAsUpdate(), name);
        Assert.Equal(bytes.GetStateVector().Clients.OrderBy(c => c.Key), decoded.GetStateVector().Clients.OrderBy(c => c.Key));
    }

    [Theory, Covers("SRV-2")]
    [InlineData(3)]
    [InlineData(17)]
    public void ApplyingEditsInAnyOrder_GivesWhatTheBytesGive_UpdateByUpdate(int seed)
    {
        // out of order, so updates wait as pending and are retried: the pending state must match too
        var rng = new Random(seed);
        var updates = Edits(seed, 60).OrderBy(_ => rng.Next()).ToList();
        var bytes = new Doc(new DocOptions { ClientId = 99 });
        var decoded = new Doc(new DocOptions { ClientId = 99 });
        foreach (var u in updates)
        {
            var a = bytes.TryApplyUpdate(u);
            var b = decoded.TryApplyUpdate(Decode(u));
            Assert.Equal((a.Error, a.RejectedStructs), (b.Error, b.RejectedStructs));
            HexAssert.Equal(bytes.EncodeStateAsUpdate(), decoded.EncodeStateAsUpdate(), "after an update");
        }

        Assert.Equal(bytes.GetText("t").ToString(), decoded.GetText("t").ToString());
    }

    [Theory, Covers("SRV-2")]
    [MemberData(nameof(Cases))]
    public void PreparingIt_GivesWhatPreparingTheBytesGives(string name, string hex)
    {
        var update = Hex.Bytes(hex);
        var bytes = new YRelay();
        var decoded = new YRelay();
        var a = bytes.TryPrepareUpdate(update, out var pa);
        var b = decoded.TryPrepareUpdate(Decode(update), out var pb);
        Assert.Equal(a.Error, b.Error);
        if (!a.Ok) return;
        Assert.Equal(pa!.StateVector.Clients.OrderBy(c => c.Key), pb!.StateVector.Clients.OrderBy(c => c.Key));
        bytes.Commit(pa);
        decoded.Commit(pb);
        foreach (var format in new[] { UpdateFormat.V1, UpdateFormat.V2 })
            HexAssert.Equal(bytes.EncodeStateAsUpdate(default, format), decoded.EncodeStateAsUpdate(default, format), $"{name} {format}");
    }

    [Fact, Covers("SRV-2", "ERR-05")]
    public void AnUpdateDecodedUnderLooserLimits_IsRefused_AndChangesNothing()
    {
        var doc = new Doc(new DocOptions { ClientId = 1 });
        doc.GetText("t").Insert(0, "abcdefgh");
        var update = doc.EncodeStateAsUpdate();
        var tight = YLimits.Default with { MaxStringBytes = 4 };
        var loose = Decode(update);

        var relay = new YRelay(tight);
        Assert.Equal(YError.LimitExceeded, relay.TryPrepareUpdate(loose, out var prepared).Error);
        Assert.Null(prepared);
        Assert.True(relay.IsEmpty);
        var target = new Doc(new DocOptions { Limits = tight });
        Assert.Equal(YError.LimitExceeded, target.TryApplyUpdate(loose).Error);
        HexAssert.Equal(new byte[] { 0, 0 }, target.EncodeStateAsUpdate(), "refused");

        // decoded under limits as tight as theirs, or under their own, it is checked the same way the bytes are
        Assert.Equal(YError.LimitExceeded, relay.TryPrepareUpdate(update, out _).Error);
        Assert.False(YUpdate.TryDecode(update, out _, UpdateFormat.V1, tight).Ok);
        var small = YLimits.Default with { MaxStructsPerUpdate = 0 };
        Assert.Equal(YError.LimitExceeded, new YRelay(small).TryPrepareUpdate(Decode(update, UpdateFormat.V1, small with { MaxStructsPerUpdate = 100 }), out _).Error);
        Assert.Equal(YError.LimitExceeded, new Doc(new DocOptions { Limits = YLimits.Default with { MaxUpdateBytes = 4 } }).TryApplyUpdate(loose).Error);
    }
}
