using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Rng;
using YSharp.TestKit.Sim;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Codecs;

/// <summary>
/// Ports of the upstream encoding, compatibility and updates tests (ledger rows of spec 5). Each keeps the JS test's steps and
/// assertions; the places where the public API of ADR 0008 has no equivalent are noted on the test. The three V1 documents of the
/// compatibility tests are read from the pinned <c>yjs/tests/compatibility.tests.js</c> rather than copied, so they cannot drift.
/// </summary>
[Requires(Feature.V1)]
public sealed class UpstreamPortTests
{
    private static string Json(YValue v)
    {
        var w = new ArrayBufferWriter<byte>();
        var status = v.TryWriteJson(w);
        Assert.True(status.Ok, status.Error.ToString());
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    private static YValue ParseJson(string text)
    {
        var status = YValue.TryParseJson(Encoding.UTF8.GetBytes(text), null, out var v);
        Assert.True(status.Ok, $"{status.Error}: {text[..Math.Min(80, text.Length)]}");
        return v;
    }

    /// <summary>The delta of a text as the JSON the JS tests compare: <c>[{insert, attributes?}, ...]</c>.</summary>
    private static string DeltaJson(YText text)
    {
        var items = new List<YValue>();
        foreach (var op in text.ToDelta())
        {
            Assert.Equal(DeltaKind.Insert, op.Kind);
            var members = new List<KeyValuePair<string, YValue>>
            {
                new("insert", op.Text != null ? (YValue)op.Text : op.Embed.Value),
            };
            if (op.Attributes.Kind != YValueKind.Undefined) members.Add(new("attributes", op.Attributes));
            items.Add(YValue.Object(members.ToArray()));
        }
        return Json(YValue.Array(items.ToArray()));
    }

    // ------------------------------------------------------------------ encoding.tests.js

    [Fact, PortOf("encoding.tests.js:testStructReferences", "ENC-22", "ENC-12")]
    public void StructRefs_EveryContentRefSlot_DecodesAndReencodesUnchanged()
    {
        // JS asserts the contentRefs table: 11 slots, ref 0 GC, 1 deleted, 2 JSON, 3 binary, 4 string, 5 embed, 6 format, 7 type,
        // 8 any, 9 doc, 10 reserved for Skip. The table is internal, so this checks each slot through the wire: every ref decodes
        // to the content that the oracle re-encodes unchanged, and the first free slot (11) is rejected.
        var cases = new (string Name, string Update, string StateVector, string Reencoded)[]
        {
            ("0 gc", "01 01 01 00 00 03 00", "010103", "0101010000030101010003"),
            ("1 deleted", "01 01 01 00 01 01 01 74 03 00", "010103", "0101010001010174030101010003"),
            ("2 json", "01 01 01 00 02 01 01 61 02 01 31 01 32 00", "010102", "0101010002010161020131013200"),
            ("3 binary", "01 01 01 00 03 01 01 74 02 01 02 00", "010101", "010101000301017402010200"),
            ("4 string", "01 01 01 00 04 01 01 74 02 68 69 00", "010102", "010101000401017402686900"),
            ("5 embed", "01 01 01 00 05 01 01 74 02 7b 7d 00", "010101", "0101010005010174027b7d00"),
            ("6 format", "01 01 01 00 06 01 01 74 01 62 04 74 72 75 65 00", "010101", "01010100060101740162047472756500"),
            ("7 type", "01 01 01 00 07 01 01 61 00 00", "010101", "01010100070101610000"),
            ("8 any", "01 01 01 00 08 01 01 74 01 7d 01 00", "010101", "0101010008010174017d0100"),
            ("9 doc", "01 01 01 00 09 01 01 61 01 67 76 00 00", "010101", "01010100090101610167760000"),
            ("10 skip", "01 01 01 00 0a 02 00", "00", "0000"),
        };
        Assert.Equal(11, cases.Length);
        foreach (var c in cases)
        {
            var doc = Malformed.AssertAccepted(c.Update);
            Assert.True(c.StateVector == Hex.Of(Wire.StateVector(doc)), $"{c.Name}: state vector {Hex.Of(Wire.StateVector(doc))}");
            Assert.True(c.Reencoded == Hex.Of(doc.EncodeStateAsUpdate()), $"{c.Name}: re-encoded as {Hex.Of(doc.EncodeStateAsUpdate())}");
        }
        Malformed.AssertRejected("01 01 01 00 0b 01 01 74 00");
    }

    [Fact, PortOf("encoding.tests.js:testPermanentUserData", "ENC-21")]
    [Requires(Feature.Text, Feature.UserData)]
    public void PermanentUserData_TwoUsersWithDeletions_RoundTripThroughUpdates()
    {
        // JS waits 10 ms so the setTimeout(0) work of PermanentUserData runs; the options seam flushes it synchronously.
        var options = new PermanentUserDataOptions { Defer = flush => flush() };
        var ydoc1 = new Doc();
        var ydoc2 = new Doc();
        var pd1 = new PermanentUserData(ydoc1, options);
        var pd2 = new PermanentUserData(ydoc2, options);
        pd1.SetUserMapping(ydoc1, ydoc1.ClientId, "user a");
        pd2.SetUserMapping(ydoc2, ydoc2.ClientId, "user b");
        ydoc1.GetText().Insert(0, "xhi");
        ydoc1.GetText().Delete(0, 1);
        ydoc2.GetText().Insert(0, "hxxi");
        ydoc2.GetText().Delete(1, 2);
        ydoc2.ApplyUpdate(ydoc1.EncodeStateAsUpdate());
        ydoc1.ApplyUpdate(ydoc2.EncodeStateAsUpdate());

        // now sync a third doc with the same name as doc1 and then create PermanentUserData
        var ydoc3 = new Doc();
        ydoc3.ApplyUpdate(ydoc1.EncodeStateAsUpdate());
        var pd3 = new PermanentUserData(ydoc3, options);
        pd3.SetUserMapping(ydoc3, ydoc3.ClientId, "user a");
        Assert.Equal("user a", pd3.GetUserByClientId(ydoc3.ClientId));
    }

    [Fact, PortOf("encoding.tests.js:testDiffStateVectorOfUpdateIsEmpty", "ENC-09", "ENC-24")]
    [Requires(Feature.Text, Feature.Events)]
    public void StateVectorOfUpdate_UpdateWithMissingPredecessors_IsEmpty()
    {
        var ydoc = new Doc();
        byte[]? sv = null;
        ydoc.GetText().Insert(0, "a");
        ydoc.Update += (update, _, _, _) => sv = Wire.StateVectorOfUpdate(update.ToArray());
        // should produce an update with an empty state vector (because previous ops are missing)
        ydoc.GetText().Insert(0, "a");
        Assert.True(sv is { Length: 1 } && sv[0] == 0);
    }

    [Fact, PortOf("encoding.tests.js:testDiffStateVectorOfUpdateIgnoresSkips", "ENC-09", "ENC-24", "ENC-81")]
    [Requires(Feature.Text, Feature.Events)]
    public void StateVectorOfUpdate_MergedUpdateWithSkips_IgnoresTheSkippedRange()
    {
        var ydoc = new Doc();
        var updates = new List<byte[]>();
        ydoc.Update += (update, _, _, _) => updates.Add(update.ToArray());
        ydoc.GetText().Insert(0, "a");
        ydoc.GetText().Insert(0, "b");
        ydoc.GetText().Insert(0, "c");
        var update13 = Wire.Merge(UpdateFormat.V1, updates[0], updates[2]);
        var state = StateVector.Decode(Wire.StateVectorOfUpdate(update13));
        Assert.Equal(1L, state[ydoc.ClientId]);
        Assert.Single(state.Clients);
    }

    // ------------------------------------------------------------------ compatibility.tests.js

    private static string CompatBlock(string testName)
    {
        var path = Path.Combine(RepoRoot.Find(), "yjs", "tests", "compatibility.tests.js");
        var source = File.ReadAllText(path);
        var start = source.IndexOf($"export const {testName}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{testName} is not in {path}");
        var end = source.IndexOf("\nexport const ", start + 1, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static Doc LoadCompatDoc(string block)
    {
        var base64 = Regex.Match(block, @"const oldDoc = '([A-Za-z0-9+/=]+)'").Groups[1].Value;
        Assert.NotEmpty(base64);
        var doc = new Doc();
        doc.ApplyUpdate(Convert.FromBase64String(base64));
        return doc;
    }

    [Fact, PortOf("compatibility.tests.js:testArrayCompatibilityV1", "ENC-21", "ENC-11")]
    [Requires(Feature.Array, Feature.Map)]
    public void Decode_OldV1ArrayDocument_YieldsTheStoredArray()
    {
        var block = CompatBlock("testArrayCompatibilityV1");
        var expected = Regex.Match(block, @"JSON\.parse\('(.+)'\)").Groups[1].Value;
        var doc = LoadCompatDoc(block);
        Assert.Equal(Json(ParseJson(expected)), Json(doc.GetArray("array").ToJson()));
    }

    [Fact, PortOf("compatibility.tests.js:testMapDecodingCompatibilityV1", "ENC-21", "ENC-11")]
    [Requires(Feature.Map)]
    public void Decode_OldV1MapDocument_YieldsTheStoredMap()
    {
        var block = CompatBlock("testMapDecodingCompatibilityV1");
        var expected = Regex.Match(block, @"\(\{(.+)\}\)\r?\n").Groups[1].Value;
        var doc = LoadCompatDoc(block);
        Assert.Equal(Json(ParseJson("{" + expected + "}")), Json(doc.GetMap("map").ToJson()));
    }

    [Fact, PortOf("compatibility.tests.js:testTextDecodingCompatibilityV1", "ENC-21", "ENC-11", "ENC-12")]
    [Requires(Feature.Text, Feature.Array, Feature.V2)]
    public void Decode_OldV1TextDocument_YieldsTheStoredDelta()
    {
        var block = CompatBlock("testTextDecodingCompatibilityV1");
        var expected = Regex.Match(block, @"const oldVal = (\[.+\])\r?\n").Groups[1].Value;
        var doc = LoadCompatDoc(block);
        Assert.Equal(Json(ParseJson(expected)), DeltaJson(doc.GetText("text")));
    }

    // ------------------------------------------------------------------ updates.tests.js

    /// <summary>The three encoders of the JS test: V1 functions, V2 functions, and "merge by applying to a Doc" (V2 bytes).</summary>
    private sealed record Enc(
        string Description, UpdateFormat Format, bool UpdateEventIsV2,
        Func<byte[][], byte[]> MergeUpdates, Func<byte[], byte[], byte[]> DiffUpdate)
    {
        public byte[] EncodeStateAsUpdate(Doc doc) => doc.EncodeStateAsUpdate(default, Format);
        public void ApplyUpdate(Doc doc, byte[] update) => doc.ApplyUpdate(update, null, Format);
        public byte[] StateVectorFromUpdate(byte[] update) => Wire.StateVectorOfUpdate(update, Format);
    }

    private static Enc[] Encoders() => new[]
    {
        new Enc("V1", UpdateFormat.V1, false, u => Wire.Merge(UpdateFormat.V1, u), (u, sv) => Wire.Diff(u, sv, UpdateFormat.V1)),
        new Enc("V2", UpdateFormat.V2, true, u => Wire.Merge(UpdateFormat.V2, u), (u, sv) => Wire.Diff(u, sv, UpdateFormat.V2)),
        new Enc("Merge via Y.Doc", UpdateFormat.V2, true,
            updates =>
            {
                var ydoc = new Doc(new DocOptions { Gc = false });
                foreach (var update in updates) ydoc.ApplyUpdate(update, null, UpdateFormat.V2);
                return ydoc.EncodeStateAsUpdate(default, UpdateFormat.V2);
            },
            (update, sv) =>
            {
                var ydoc = new Doc(new DocOptions { Gc = false });
                ydoc.ApplyUpdate(update, null, UpdateFormat.V2);
                return ydoc.EncodeStateAsUpdate(sv, UpdateFormat.V2);
            }),
    };

    private static Doc FromUpdates(IEnumerable<TestYInstance> users, Enc enc)
    {
        var updates = users.Select(u => enc.EncodeStateAsUpdate(u.Doc)).ToArray();
        var ydoc = new Doc();
        enc.ApplyUpdate(ydoc, enc.MergeUpdates(updates));
        return ydoc;
    }

    private static string[] ArrayJson(YArray array) => array.ToArray().Select(e => Json(e.Value)).ToArray();

    [Fact, PortOf("updates.tests.js:testMergeUpdates", "ENC-23")]
    [Requires(Feature.Array, Feature.Sync)]
    public void MergeUpdates_ConcurrentArrayInserts_EqualsApplyingBothUpdates()
    {
        var sim = SimHarness.Init(Prng.Create(1), users: 3);
        var array0 = sim.Users[0].Doc.GetArray("array");
        var array1 = sim.Users[1].Doc.GetArray("array");

        array0.Insert(0, new YEntry[] { 1 });
        array1.Insert(0, new YEntry[] { 2 });

        SimHarness.Compare(sim.Users);
        foreach (var enc in Encoders())
        {
            var merged = FromUpdates(sim.Users, enc);
            Assert.Equal(ArrayJson(array0), ArrayJson(merged.GetArray("array")));
        }
    }

    [Fact, PortOf("updates.tests.js:testKeyEncoding", "ENC-76", "ENC-14")]
    [Requires(Feature.Text, Feature.Array, Feature.V2, Feature.Events)]
    [Requires(Feature.Sync)]
    public void KeyEncoding_FormattedText_RoundTripsThroughEveryEncoder()
    {
        var sim = SimHarness.Init(Prng.Create(1), users: 2);
        var text0 = sim.Users[0].Doc.GetText("text");
        var text1 = sim.Users[1].Doc.GetText("text");
        var italic = YValue.Object(new[] { new KeyValuePair<string, YValue>("italic", true) });

        text0.Insert(0, "a", italic);
        text0.Insert(0, "b");
        text0.Insert(0, "c", italic);

        var update = sim.Users[0].Doc.EncodeStateAsUpdate(default, UpdateFormat.V2);
        sim.Users[1].Doc.ApplyUpdate(update, null, UpdateFormat.V2);

        Assert.Equal("[{\"insert\":\"c\",\"attributes\":{\"italic\":true}},{\"insert\":\"b\"},{\"insert\":\"a\",\"attributes\":{\"italic\":true}}]",
            DeltaJson(text1));

        SimHarness.Compare(sim.Users);
    }

    /// <summary>
    /// <c>checkUpdateCases</c>. Two parts of the JS helper have no public equivalent and are replaced by their public consequence:
    /// <c>parseUpdateMeta</c> (compared through <c>TryStateVectorFromUpdate</c>) and the byte comparison against a delete set rebuilt
    /// with <c>readDeleteSet</c>/<c>writeDeleteSet</c> (internals; the diffed update is applied to a document at the target state
    /// instead, and must reach the same array).
    /// </summary>
    private static void CheckUpdateCases(Doc ydoc, List<byte[]> updates, Enc enc)
    {
        var cases = new List<byte[]>
        {
            enc.MergeUpdates(updates.ToArray()),                                                               // case 1: merge everything
            enc.MergeUpdates(new[] { enc.MergeUpdates(updates.Skip(2).ToArray()), enc.MergeUpdates(updates.Take(2).ToArray()) }),   // case 2
            enc.MergeUpdates(new[] { enc.MergeUpdates(updates.Skip(2).ToArray()), enc.MergeUpdates(updates.Skip(1).Take(2).ToArray()), updates[0] }),   // case 3
            enc.MergeUpdates(new[]                                                                             // case 4: separated updates with skips
            {
                enc.MergeUpdates(new[] { updates[0], updates[2] }),
                enc.MergeUpdates(new[] { updates[1], updates[3] }),
                enc.MergeUpdates(updates.Skip(4).ToArray()),
            }),
        };
        cases.Add(enc.MergeUpdates(cases.ToArray()));                                                          // case 5: many duplicates

        foreach (var mergedUpdates in cases)
        {
            var merged = new Doc(new DocOptions { Gc = false });
            enc.ApplyUpdate(merged, mergedUpdates);
            Assert.Equal(ArrayJson(ydoc.GetArray()), ArrayJson(merged.GetArray()));
            Assert.Equal(Hex.Of(Wire.StateVector(merged)), Hex.Of(enc.StateVectorFromUpdate(mergedUpdates)));

            if (enc.UpdateEventIsV2)
            {
                for (var j = 1; j < updates.Count; j++)
                {
                    var partMerged = enc.MergeUpdates(updates.Skip(j).ToArray());
                    var targetSv = Wire.StateVectorOfUpdate(Wire.Merge(UpdateFormat.V2, updates.Take(j).ToArray()), UpdateFormat.V2);
                    var diffed = enc.DiffUpdate(mergedUpdates, targetSv);
                    Assert.Equal(Hex.Of(enc.StateVectorFromUpdate(partMerged)), Hex.Of(enc.StateVectorFromUpdate(diffed)));

                    var target = new Doc(new DocOptions { Gc = false });
                    enc.ApplyUpdate(target, Wire.Merge(enc.Format, updates.Take(j).ToArray()));
                    enc.ApplyUpdate(target, diffed);
                    Assert.Equal(ArrayJson(ydoc.GetArray()), ArrayJson(target.GetArray()));
                }
            }
        }
    }

    private static void RecordUpdates(Doc ydoc, Enc enc, List<byte[]> updates)
    {
        if (enc.UpdateEventIsV2) ydoc.UpdateV2 += (u, _, _, _) => updates.Add(u.ToArray());
        else ydoc.Update += (u, _, _, _) => updates.Add(u.ToArray());
    }

    [Fact, PortOf("updates.tests.js:testMergeUpdates1", "ENC-23", "ENC-24", "ENC-81")]
    [Requires(Feature.Array, Feature.Events)]
    public void MergeUpdates_SequentialArrayInserts_MatchesTheMergedDoc()
    {
        foreach (var enc in Encoders())
        {
            var ydoc = new Doc(new DocOptions { Gc = false });
            var updates = new List<byte[]>();
            RecordUpdates(ydoc, enc, updates);

            var array = ydoc.GetArray();
            array.Insert(0, new YEntry[] { 1 });
            array.Insert(0, new YEntry[] { 2 });
            array.Insert(0, new YEntry[] { 3 });
            array.Insert(0, new YEntry[] { 4 });

            CheckUpdateCases(ydoc, updates, enc);
        }
    }

    [Fact, PortOf("updates.tests.js:testMergeUpdates2", "ENC-23", "ENC-24", "ENC-81")]
    [Requires(Feature.Array, Feature.Events)]
    public void MergeUpdates_InsertThenDelete_MatchesTheMergedDoc()
    {
        foreach (var enc in Encoders())
        {
            var ydoc = new Doc(new DocOptions { Gc = false });
            var updates = new List<byte[]>();
            RecordUpdates(ydoc, enc, updates);

            var array = ydoc.GetArray();
            array.Insert(0, new YEntry[] { 1, 2 });
            array.Delete(1, 1);
            array.Insert(0, new YEntry[] { 3, 4 });
            array.Delete(1, 2);

            CheckUpdateCases(ydoc, updates, enc);
        }
    }

    [Fact, PortOf("updates.tests.js:testMergePendingUpdates", "ENC-84", "ENC-21", "ENC-23")]
    [Requires(Feature.Text, Feature.Events)]
    public void MergeUpdates_PendingOutOfOrderUpdates_AreMergedAndApplyCleanly()
    {
        var yDoc = new Doc();
        var serverUpdates = new List<byte[]>();
        yDoc.Update += (update, _, _, _) => serverUpdates.Add(update.ToArray());
        var yText = yDoc.GetText("textBlock");
        // JS applies the delta [{insert: c}] five times; a delta with no retain inserts at position 0, as Insert(0, c) does
        foreach (var c in new[] { "r", "o", "n", "e", "n" }) yText.Insert(0, c);

        var yDoc1 = new Doc();
        yDoc1.ApplyUpdate(serverUpdates[0]);
        var update1 = yDoc1.EncodeStateAsUpdate();

        var yDoc2 = new Doc();
        yDoc2.ApplyUpdate(update1);
        yDoc2.ApplyUpdate(serverUpdates[1]);
        var update2 = yDoc2.EncodeStateAsUpdate();

        var yDoc3 = new Doc();
        yDoc3.ApplyUpdate(update2);
        yDoc3.ApplyUpdate(serverUpdates[3]);
        var update3 = yDoc3.EncodeStateAsUpdate();

        var yDoc4 = new Doc();
        yDoc4.ApplyUpdate(update3);
        yDoc4.ApplyUpdate(serverUpdates[2]);
        var update4 = yDoc4.EncodeStateAsUpdate();

        var yDoc5 = new Doc();
        yDoc5.ApplyUpdate(update4);
        yDoc5.ApplyUpdate(serverUpdates[4]);
        _ = yDoc5.EncodeStateAsUpdate();

        Assert.Equal("nenor", yDoc5.GetText("textBlock").ToString());
    }
}
