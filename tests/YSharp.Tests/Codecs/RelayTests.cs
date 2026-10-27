using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Relay;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Values;

namespace YSharp.Tests.Codecs;

/// <summary>
/// The relay tier (<see cref="YRelay"/>, ADR 0024 SRV-1, TASK-5.2): a document held as its merged update. Each fold must be Yjs
/// <c>mergeUpdates([state, update])</c>, each answer Yjs <c>diffUpdate</c> of the merged state, and a document built from the relay must equal one
/// built from every input. The oracle comparison of the same folds is <c>RelayDifferentialTests</c> in the interop project.
/// </summary>
[Requires(Feature.UpdateFunctions, Feature.V2, Feature.V2Functions, Feature.Text, Feature.TextFormat, Feature.Array, Feature.Map, Feature.Events)]
public sealed class RelayTests
{
    private static readonly byte[] U0 = Hex.Bytes("0101010004010174016100");   // client 1, clock 0: "a" in root text t
    private static readonly byte[] U1 = Hex.Bytes("01010101840100016200");     // clock 1: "b" after 1:0
    private static readonly byte[] U2 = Hex.Bytes("01010102840101016300");     // clock 2: "c" after 1:1

    public static IEnumerable<object[]> Seeds() =>
        from format in new[] { UpdateFormat.V1, UpdateFormat.V2 }
        from seed in new[] { 1, 2, 3, 7, 42 }
        select new object[] { format, seed };

    private static byte[] Encode(YRelay relay, UpdateFormat format, byte[]? sv = null) => relay.EncodeStateAsUpdate(sv, format);

    private static byte[] StateVector(Doc doc)
    {
        var w = new ArrayBufferWriter<byte>();
        doc.EncodeStateVector(w);
        return w.WrittenSpan.ToArray();
    }

    private static byte[] StateVector(YRelay relay)
    {
        var w = new ArrayBufferWriter<byte>();
        relay.EncodeStateVector(w);
        return w.WrittenSpan.ToArray();
    }

    private static Doc DocOf(IEnumerable<byte[]> updates, UpdateFormat format)
    {
        var doc = new Doc();
        foreach (var u in updates) doc.ApplyUpdate(u, null, format);
        return doc;
    }

    private static void AssertSameDoc(Doc expected, Doc actual, string label)
    {
        foreach (var f in new[] { UpdateFormat.V1, UpdateFormat.V2 })
            HexAssert.Equal(expected.EncodeStateAsUpdate(default, f), actual.EncodeStateAsUpdate(default, f), $"{label} ({f})");
        HexAssert.Equal(StateVector(expected), StateVector(actual), label + " state vector");
    }

    /// <summary>The inputs in a seeded order with about a fifth repeated: what a relay sees when clients resend and reconnect.</summary>
    private static List<byte[]> Shuffled(List<byte[]> updates, int seed)
    {
        var rng = new Random(seed * 31 + 5);
        var list = updates.Concat(updates.Where(_ => rng.Next(5) == 0)).ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    private static readonly string[] Fixtures = { "codecs/merge-diff-convert.json", "encoding/merge-diff-convert-edges.json" };

    public static IEnumerable<object[]> FixtureSteps() =>
        from f in Fixtures
        from s in FixtureReplay.Load(f).Script.GetProperty("steps").EnumerateArray()
        where s.GetProperty("step").GetString() is "mergeUpdates" or "diffUpdate"
        select new object[] { f, s.GetProperty("name").GetString()! };

    private static byte[] BytesOf(JsonElement e) => TaggedValue.Read(e).Bytes;

    /// <summary>
    /// The oracle's recorded merges and diffs, in V1 and V2: a relay that folds a merge's inputs one at a time holds the oracle's merged update
    /// (as a document), and a relay holding a diff's update answers the oracle's diff for that state vector, byte for byte.
    /// </summary>
    [Theory, Covers("RLY-2", "RLY-3")]
    [MemberData(nameof(FixtureSteps))]
    public void OnTheOracleFixtures_TheRelayGivesTheOraclesMergesAndDiffs(string fixture, string name)
    {
        var fx = FixtureReplay.Load(fixture);
        var step = fx.Script.GetProperty("steps").EnumerateArray().Single(s => s.TryGetProperty("name", out var n) && n.GetString() == name);
        var format = step.TryGetProperty("encoding", out var e) && e.GetString() == "v2" ? UpdateFormat.V2 : UpdateFormat.V1;
        var expected = BytesOf(fx.Observations.GetProperty("results").GetProperty("value").GetProperty(name).GetProperty("bytes"));
        var relay = new YRelay();
        if (step.GetProperty("step").GetString() == "mergeUpdates")
        {
            var inputs = step.GetProperty("updates").EnumerateArray().Select(BytesOf).ToList();
            foreach (var u in inputs) Assert.True(relay.TryApplyUpdate(u, format).Ok, $"{fixture} {name}");
            AssertSameDoc(DocOf(new[] { expected }, format), DocOf(new[] { Encode(relay, format) }, format), $"{fixture} {name}");
            AssertSameDoc(DocOf(inputs, format), DocOf(new[] { Encode(relay, format) }, format), $"{fixture} {name} against the inputs");
        }
        else
        {
            Assert.True(relay.TryApplyUpdate(BytesOf(step.GetProperty("update")), format).Ok, $"{fixture} {name}");
            HexAssert.Equal(expected, Encode(relay, format, BytesOf(step.GetProperty("sv"))), $"{fixture} {name}");
        }
    }

    [Theory, Covers("RLY-1", "RLY-5")]
    [InlineData(UpdateFormat.V1)]
    [InlineData(UpdateFormat.V2)]
    public void EmptyRelay_EncodesAnEmptyDocument(UpdateFormat format)
    {
        var relay = new YRelay();
        Assert.True(relay.IsEmpty);
        Assert.Equal("00", Hex.Of(StateVector(relay)));
        HexAssert.Equal(new Doc().EncodeStateAsUpdate(default, format), Encode(relay, format));
        HexAssert.Equal(new Doc().EncodeStateAsUpdate(Hex.Bytes("00"), format), Encode(relay, format, Hex.Bytes("00")));

        // an update with nothing in it is accepted and leaves the relay empty
        Assert.True(relay.TryApplyUpdate(new Doc().EncodeStateAsUpdate(default, format), format).Ok);
        Assert.True(relay.IsEmpty);
    }

    [Theory, Covers("RLY-2")]
    [MemberData(nameof(Seeds))]
    public void EveryFold_IsMergeUpdatesOfTheStateAndTheUpdate(UpdateFormat format, int seed)
    {
        var relay = new YRelay();
        var state = Encode(relay, format);
        foreach (var update in Shuffled(RelayWorkload.Updates(seed, 120, format), seed))
        {
            var expected = new ArrayBufferWriter<byte>();
            Assert.True(YUpdate.TryMerge(new ReadOnlyMemory<byte>[] { state, update }, expected, format).Ok);
            Assert.True(relay.TryApplyUpdate(update, format).Ok);
            state = Encode(relay, format);
            HexAssert.Equal(expected.WrittenSpan, state, $"seed {seed}");
        }
    }

    [Theory, Covers("RLY-2", "RLY-5")]
    [MemberData(nameof(Seeds))]
    public void ADocBuiltFromTheRelay_EqualsOneBuiltFromEveryInput(UpdateFormat format, int seed)
    {
        var updates = RelayWorkload.Updates(seed, 120, format);
        var truth = DocOf(updates, format);
        var relay = new YRelay();
        foreach (var u in Shuffled(updates, seed)) Assert.True(relay.TryApplyUpdate(u, format).Ok);

        foreach (var f in new[] { UpdateFormat.V1, UpdateFormat.V2 })
            AssertSameDoc(truth, DocOf(new[] { Encode(relay, f) }, f), $"seed {seed}, relay written as {f}");
        HexAssert.Equal(StateVector(truth), StateVector(relay), $"seed {seed} relay state vector");
    }

    [Theory, Covers("RLY-3")]
    [MemberData(nameof(Seeds))]
    public void TheDiffForAClient_IsDiffUpdateOfTheState_AndCatchesTheClientUp(UpdateFormat format, int seed)
    {
        var updates = RelayWorkload.Updates(seed, 120, format);
        var truth = DocOf(updates, format);
        var relay = new YRelay();
        foreach (var u in updates) Assert.True(relay.TryApplyUpdate(u, format).Ok);
        var state = Encode(relay, format);

        var rng = new Random(seed);
        for (var round = 0; round < 4; round++)
        {
            // a client that holds a random subset of the updates (possibly with gaps it keeps pending)
            var client = DocOf(updates.Where(_ => rng.Next(3) != 0).ToList(), format);
            var sv = StateVector(client);
            var diff = Encode(relay, format, sv);

            var expected = new ArrayBufferWriter<byte>();
            Assert.True(YUpdate.TryDiff(state, sv, expected, format).Ok);
            HexAssert.Equal(expected.WrittenSpan, diff, $"seed {seed} round {round}");

            client.ApplyUpdate(diff, null, format);
            AssertSameDoc(truth, client, $"seed {seed} round {round}");
        }
    }

    [Fact, Covers("RLY-2", "RLY-5")]
    public void AGap_BecomesASkip_AndIsFilledWhenTheMissingUpdateArrives()
    {
        var relay = new YRelay();
        Assert.True(relay.TryApplyUpdate(U2).Ok);
        Assert.True(relay.TryApplyUpdate(U0).Ok);
        Assert.Equal("010101", Hex.Of(StateVector(relay)));   // client 1 up to clock 1: clock 2 waits behind the gap

        var expected = new ArrayBufferWriter<byte>();
        Assert.True(YUpdate.TryMerge(new ReadOnlyMemory<byte>[] { U0, U2 }, expected).Ok);
        HexAssert.Equal(expected.WrittenSpan, Encode(relay, UpdateFormat.V1));
        Assert.Contains("0a01", Hex.Of(Encode(relay, UpdateFormat.V1)));   // a Skip of length 1 at clock 1

        var gapped = DocOf(new[] { U0, U2 }, UpdateFormat.V1);
        AssertSameDoc(gapped, DocOf(new[] { Encode(relay, UpdateFormat.V1) }, UpdateFormat.V1), "with the gap");

        Assert.True(relay.TryApplyUpdate(U1).Ok);
        Assert.Equal("010103", Hex.Of(StateVector(relay)));
        Assert.DoesNotContain("0a01", Hex.Of(Encode(relay, UpdateFormat.V1)));
        var whole = DocOf(new[] { U0, U1, U2 }, UpdateFormat.V1);
        Assert.Equal("abc", whole.GetText("t").ToString());
        AssertSameDoc(whole, DocOf(new[] { Encode(relay, UpdateFormat.V1) }, UpdateFormat.V1), "gap filled");
    }

    [Fact, Covers("RLY-2")]
    public void OutOfOrderAndDuplicateUpdates_GiveTheSameState()
    {
        var inOrder = new YRelay();
        foreach (var u in new[] { U0, U1, U2 }) Assert.True(inOrder.TryApplyUpdate(u).Ok);
        var shuffled = new YRelay();
        foreach (var u in new[] { U2, U2, U1, U0, U1, U0 }) Assert.True(shuffled.TryApplyUpdate(u).Ok);
        HexAssert.Equal(Encode(inOrder, UpdateFormat.V1), Encode(shuffled, UpdateFormat.V1));
        HexAssert.Equal(Encode(inOrder, UpdateFormat.V2), Encode(shuffled, UpdateFormat.V2));
    }

    [Fact, Covers("RLY-1")]
    public void TheRelay_AcceptsV1AndV2Inputs_AndWritesEither()
    {
        var updatesV1 = RelayWorkload.Updates(11, 60, UpdateFormat.V1);
        var updatesV2 = RelayWorkload.Updates(11, 60, UpdateFormat.V2);
        Assert.Equal(updatesV1.Count, updatesV2.Count);
        var fromV1 = new YRelay();
        foreach (var u in updatesV1) Assert.True(fromV1.TryApplyUpdate(u, UpdateFormat.V1).Ok);
        var mixed = new YRelay();
        for (var i = 0; i < updatesV1.Count; i++)
            Assert.True(i % 2 == 0 ? mixed.TryApplyUpdate(updatesV1[i], UpdateFormat.V1).Ok : mixed.TryApplyUpdate(updatesV2[i], UpdateFormat.V2).Ok);
        HexAssert.Equal(Encode(fromV1, UpdateFormat.V1), Encode(mixed, UpdateFormat.V1));
        HexAssert.Equal(Encode(fromV1, UpdateFormat.V2), Encode(mixed, UpdateFormat.V2));
    }

    [Fact, Covers("RLY-4")]
    public void AnInvalidOrOverLimitUpdate_ReturnsItsError_AndChangesNothing()
    {
        var relay = new YRelay(new YLimits { MaxUpdateBytes = 16 });
        Assert.True(relay.TryApplyUpdate(U0).Ok);
        var before = Encode(relay, UpdateFormat.V1);
        var sv = StateVector(relay);

        Assert.Equal(YError.Malformed, relay.TryApplyUpdate(U1.AsSpan(0, 6)).Error);
        Assert.Equal(YError.Malformed, relay.TryApplyUpdate(Hex.Bytes("01 01 01 00 8a 02 00")).Error);   // a Skip with flag bits (ENC-85)
        Assert.Equal(YError.Malformed, relay.TryApplyUpdate(Hex.Bytes("00 01 00 01 00 00")).Error);      // a delete range of length 0 (ENC-51)
        Assert.Equal(YError.LimitExceeded, relay.TryApplyUpdate(new byte[17]).Error);
        Assert.False(relay.TryApplyUpdate(U1, UpdateFormat.V2).Ok);   // V1 bytes read as V2

        HexAssert.Equal(before, Encode(relay, UpdateFormat.V1));
        HexAssert.Equal(sv, StateVector(relay));
    }

    [Fact, Covers("RLY-4")]
    public void AMalformedStateVector_IsRefused_AndNothingIsWritten()
    {
        var relay = new YRelay();
        Assert.True(relay.TryApplyUpdate(U0).Ok);
        var output = new ArrayBufferWriter<byte>();
        Assert.Equal(YError.Malformed, relay.TryEncodeStateAsUpdate(output, Hex.Bytes("05 01")).Error);
        Assert.Equal(0, output.WrittenCount);
        Assert.Throws<YDecodeException>(() => relay.EncodeStateAsUpdate(Hex.Bytes("05 01")));
    }

    [Fact, Covers("RLY-6")]
    public void ConcurrentFolds_FromManyThreads_GiveTheSameState()
    {
        var updates = RelayWorkload.Updates(5, 200, UpdateFormat.V1);
        var sequential = new YRelay();
        foreach (var u in updates) Assert.True(sequential.TryApplyUpdate(u).Ok);

        var concurrent = new YRelay();
        Parallel.ForEach(Enumerable.Range(0, 4), new ParallelOptions { MaxDegreeOfParallelism = 4 }, part =>
        {
            for (var i = part; i < updates.Count; i += 4)
            {
                Assert.True(concurrent.TryApplyUpdate(updates[i]).Ok);
                _ = Encode(concurrent, UpdateFormat.V1, StateVector(concurrent));
            }
        });

        AssertSameDoc(DocOf(new[] { Encode(sequential, UpdateFormat.V1) }, UpdateFormat.V1),
            DocOf(new[] { Encode(concurrent, UpdateFormat.V1) }, UpdateFormat.V1), "concurrent folds");
    }

    // ------------------------------------------------------------------ RLY-7: the pending bound, prepare and commit (TASK-5.3)

    private static readonly byte[] Other = Hex.Bytes("01010202840101016300");   // client 2, clock 2: waits on its clocks 0 and 1

    private static byte[] As(byte[] v1, UpdateFormat format)
    {
        if (format == UpdateFormat.V1) return v1;
        var w = new ArrayBufferWriter<byte>();
        Assert.True(YUpdate.TryConvert(v1, UpdateFormat.V1, UpdateFormat.V2, w).Ok);
        return w.WrittenSpan.ToArray();
    }

    [Theory, Covers("RLY-7")]
    [InlineData(UpdateFormat.V1)]
    [InlineData(UpdateFormat.V2)]
    public void ThePendingBound_ExactlyAtTheLimitIsAccepted_OneOverIsRejected_AndAFilledGapFreesIt(UpdateFormat format)
    {
        // U2 alone waits on clocks 0 and 1, so all of it is pending: its V1 encoding is U2 itself, 10 bytes
        Assert.Equal(YError.LimitExceeded, new YRelay(new YLimits { MaxPendingBytes = U2.Length - 1 }).TryApplyUpdate(As(U2, format), format).Error);
        var relay = new YRelay(new YLimits { MaxPendingBytes = U2.Length });
        Assert.True(relay.TryApplyUpdate(As(U2, format), format).Ok);
        var before = Encode(relay, UpdateFormat.V1);
        Assert.Equal(YError.LimitExceeded, relay.TryApplyUpdate(As(Other, format), format).Error);
        HexAssert.Equal(before, Encode(relay, UpdateFormat.V1), "a rejected update changes nothing");

        // U0 fills clock 0 and U2 still waits behind the Skip at 1; U1 fills the gap and nothing is pending, so Other fits
        Assert.True(relay.TryApplyUpdate(As(U0, format), format).Ok);
        Assert.Equal(YError.LimitExceeded, relay.TryApplyUpdate(As(Other, format), format).Error);
        Assert.True(relay.TryApplyUpdate(As(U1, format), format).Ok);
        Assert.True(relay.TryApplyUpdate(As(Other, format), format).Ok);
        Assert.Equal(3, relay.GetStateVector()[1]);
    }

    [Fact, Covers("RLY-7")]
    public void ADeleteRangePastTheState_IsPending_UntilTheStructsArrive()
    {
        var delete = Hex.Bytes("000101010001");   // no structs; client 1, clock 0, length 1: 6 bytes
        Assert.Equal(YError.LimitExceeded, new YRelay(new YLimits { MaxPendingBytes = delete.Length - 1 }).TryApplyUpdate(delete).Error);

        var relay = new YRelay(new YLimits { MaxPendingBytes = delete.Length });
        Assert.True(relay.TryApplyUpdate(delete).Ok);
        var second = Hex.Bytes("000102010001");   // the same on client 2
        Assert.Equal(YError.LimitExceeded, relay.TryApplyUpdate(second).Error);
        Assert.True(relay.TryApplyUpdate(U0).Ok);     // client 1's clock 0 arrives: its range is no longer pending
        Assert.True(relay.TryApplyUpdate(second).Ok);
        Assert.Equal("", DocOf(new[] { Encode(relay, UpdateFormat.V1) }, UpdateFormat.V1).GetText("t").ToString());
    }

    [Fact, Covers("RLY-7", "RLY-4")]
    public void Prepare_ChangesNothing_UntilCommit_AndACommitIsSpentOnce()
    {
        var relay = new YRelay();
        Assert.True(relay.TryPrepareUpdate(U0, out var prepared).Ok);
        Assert.True(relay.IsEmpty);
        Assert.Equal(1, prepared!.StateVector[1]);
        relay.Commit(prepared);
        var direct = new YRelay();
        Assert.True(direct.TryApplyUpdate(U0).Ok);
        HexAssert.Equal(Encode(direct, UpdateFormat.V1), Encode(relay, UpdateFormat.V1), "prepare then commit is a fold");

        Assert.Throws<InvalidOperationException>(() => relay.Commit(prepared));                 // already committed
        Assert.True(relay.TryPrepareUpdate(U1, out var stale).Ok);
        Assert.True(relay.TryApplyUpdate(U2).Ok);
        Assert.Throws<InvalidOperationException>(() => relay.Commit(stale!));                   // the relay changed after it was prepared
        Assert.Throws<InvalidOperationException>(() => new YRelay().Commit(prepared));          // prepared by another relay

        Assert.Equal(YError.Malformed, relay.TryPrepareUpdate(U1[..^2], out var none).Error);
        Assert.Null(none);
    }

    // ------------------------------------------------------------------ TASK-9: folds proportional to the update

    [Theory, Covers("RLY-7")]
    [MemberData(nameof(Seeds))]
    public void ThePendingSizeKeptPerClient_IsThePendingSizeMeasuredOverTheWholeState(UpdateFormat format, int seed)
    {
        // shuffled, so clients gain and fill gaps and deleted ranges land before and after their client's state
        var relay = new YRelay();
        foreach (var update in Shuffled(RelayWorkload.Updates(seed, 120, format), seed))
        {
            Assert.True(relay.TryApplyUpdate(update, format).Ok);
            Assert.Equal(relay.PendingBytesRecomputed, relay.PendingBytes);
        }
    }

    [Theory, Covers("RLY-1")]
    [InlineData(UpdateFormat.V1)]
    [InlineData(UpdateFormat.V2)]
    public void UpdatesInOrder_KeepThePerClientForm_AndMergeNothingHeld(UpdateFormat format)
    {
        var relay = new YRelay();
        foreach (var update in RelayWorkload.Updates(3, 200, format))
        {
            Assert.True(relay.TryApplyUpdate(update, format).Ok);
            Assert.False(relay.IsFlat);
            Assert.Equal(0, relay.MergedHeldStructs);   // each update only appends to its client's history
        }
    }

    [Fact, Covers("RLY-1")]
    public void AFold_AllocatesInProportionToTheUpdate_NotTheState()
    {
        // a client typing one character at a time: the 1700th keystroke costs what the 300th did
        var doc = new Doc(new DocOptions { ClientId = 1 });
        var updates = new List<byte[]>();
        doc.Update += (u, _, _, _) => updates.Add(u.ToArray());
        var text = doc.GetText("t");
        for (var i = 0; i < 2100; i++) text.Insert(i % 7 == 0 ? 0 : text.Length, "ab"[i % 2].ToString());

        long Cost(int from, int count)
        {
            var relay = new YRelay();
            for (var i = 0; i < from; i++) Assert.True(relay.TryApplyUpdate(updates[i]).Ok);
            Assert.True(relay.TryApplyUpdate(updates[from]).Ok);   // warm up
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = from + 1; i <= from + count; i++) relay.TryApplyUpdate(updates[i]);
            return (GC.GetAllocatedBytesForCurrentThread() - before) / count;
        }

        // windows that cross no power of two, where the held lists double their capacity (amortized, it is the update's cost)
        var small = Cost(300, 50);
        var large = Cost(1700, 50);
        Assert.True(large < small * 2, $"a fold allocated {large} bytes on a 1700-update state and {small} on a 300-update one");
    }
}
