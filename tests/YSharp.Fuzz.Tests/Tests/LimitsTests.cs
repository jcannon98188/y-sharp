using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.Fuzz.Builder;
using YSharp.Fuzz.Corpus;
using YSharp.Fuzz.Guards;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Fuzz.Tests;

/// <summary>
/// Limit boundaries (ADR 0013 Limits in tests, ADR 0005): under the defaults and under a tiny <see cref="YLimits"/>, exactly at the
/// limit is accepted and one over is rejected with <c>LimitExceeded</c>. Where the default limit is too large to build in the gating
/// tier (tens of MB), the boundary is shown with a length claim against a short body instead: a claim one over the limit is a limit
/// rejection, a claim at the limit is not (the body is short, so it is an EOF error).
/// <para>
/// Semantics (ADR 0005, decided): a limit is exceeded when the value is greater than the limit; the value is the decoded quantity and the
/// check runs on the length claim before anything is sized from it; it applies on every decode path (apply, merge, diff, convert, state
/// vector, validate; V1 and V2 columns); a rejection leaves the document unchanged. <c>MaxAnyDepth</c> counts nested containers (an array
/// holding an array is depth 2; the leaf scalar adds none); <c>MaxStructsPerUpdate</c> counts every struct including GC and Skip;
/// <c>MaxStringBytes</c> is the UTF-8 length of one string; <c>MaxUpdateBytes</c> is the whole buffer of one input update (trailing bytes
/// count); <c>MaxPendingBytes</c> is the V1 encoding of the retained pending state after the update.
/// </para>
/// </summary>
[Requires(Feature.V2Functions)]
public sealed class LimitsTests
{
    private static YLimits Default => YLimits.Default;

    private static string Outcome(YLimits limits, byte[] update, UpdateFormat format = UpdateFormat.V1) =>
        Hostile.ApplyAndCheck($"limits boundary ({update.Length} bytes)", update, format, new[] { "ok", "Malformed", "LimitExceeded", "Rejected" }, limits).Outcome;

    private static void AssertAccepted(YLimits limits, byte[] update, string what) => Assert.True(Outcome(limits, update) == "ok", $"{what}: exactly at the limit must be accepted");

    private static void AssertLimit(YLimits limits, byte[] update, string what) => Assert.True(Outcome(limits, update) == "LimitExceeded", $"{what}: one over the limit must be LimitExceeded");

    private static byte[] Gcs(int n) => UpdateBuilder.V1(UpdateModel.Single(1, 0, Enumerable.Range(0, n).Select(_ => Struct.Gc(1)).ToArray()));

    // ------------------------------------------------------------------ numeric sanity (no implementation needed)

    [Fact, Covers("ERR-08", Layer = CoverLayer.Unit)]
    [LibraryIndependent]
    public void Defaults_FallInASaneNumericRange_AndTinyIsBelowThem()
    {
        var d = Default;
        Assert.InRange(d.MaxAnyDepth, 16, 4096);
        Assert.InRange(d.MaxStructsPerUpdate, 100_000, 100_000_000);
        Assert.InRange(d.MaxPendingBytes, 1 << 20, 1 << 30);
        Assert.InRange(d.MaxStringBytes, 1 << 20, 1 << 30);
        Assert.InRange(d.MaxUpdateBytes, 1 << 20, int.MaxValue);
        var t = Hostile.Tiny;
        Assert.True(t.MaxAnyDepth < d.MaxAnyDepth && t.MaxStructsPerUpdate < d.MaxStructsPerUpdate && t.MaxPendingBytes < d.MaxPendingBytes
                    && t.MaxStringBytes < d.MaxStringBytes && t.MaxUpdateBytes < d.MaxUpdateBytes);
        Assert.Equal(Default, YLimits.Default);   // a record: the singleton compares by value
    }

    [Fact, Covers("ERR-08", Layer = CoverLayer.Unit)]
    public void DocOptions_Limits_AreCarriedIntoTheDocument()
    {
        Assert.Equal(Hostile.Tiny, Hostile.NewDoc(Hostile.Tiny).Limits);
        Assert.Equal(YLimits.Default, new Doc().Limits);
    }

    [Fact, Covers("HOS-LEN", "ERR-08", Layer = CoverLayer.Unit)]
    public void Defaults_ValidFixtureCorpora_AreNeverRejected()
    {
        var failures = new List<string>();
        var seeds = SeedUpdates.Collect();
        Assert.True(seeds.Count > 50);
        foreach (var group in seeds.GroupBy(s => (s.Source.Split(':')[0], s.Format)))
        {
            // the updates of one fixture arrive in order on one document, so diffs find their predecessors
            var doc = Hostile.NewDoc();
            foreach (var seed in group)
            {
                var result = doc.TryApplyUpdate(seed.Bytes, null, seed.Format);
                if (!result.Ok) failures.Add($"{seed}: {Hostile.Label(result)}");
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} valid updates rejected under the default limits:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(10))}");
    }

    // ------------------------------------------------------------------ Any depth (ERR-12)

    [Theory, Covers("HOS-DEEP", "ERR-12", Layer = CoverLayer.Unit)]
    [InlineData(false)]
    [InlineData(true)]
    public void AnyDepth_AtTheLimitIsAccepted_OneOverIsRejected(bool tiny)
    {
        var limits = tiny ? Hostile.Tiny : Default;
        AssertAccepted(limits, UpdateBuilder.DeepAnyArrays(limits.MaxAnyDepth), $"Any depth {limits.MaxAnyDepth}");
        AssertLimit(limits, UpdateBuilder.DeepAnyArrays(limits.MaxAnyDepth + 1), $"Any depth {limits.MaxAnyDepth + 1}");
    }

    [Fact, Covers("HOS-DEEP", "ERR-12", Layer = CoverLayer.Unit)]
    public void AnyDepth_100kDeep_IsRejectedOnASmallStackWithoutRecursing()
    {
        // the probe thread has a 256 KB stack: recursion proportional to the input depth would overflow it long before 100000 frames
        var bytes = UpdateBuilder.DeepAnyArrays(100_000);
        var applied = Hostile.ApplyAndCheck("Any depth 100000", bytes, UpdateFormat.V1, new[] { "LimitExceeded" });
        Assert.Equal("LimitExceeded", applied.Outcome);
    }

    [Fact, Covers("HOS-DEEP", "ERR-12", Layer = CoverLayer.Unit)]
    public void AnyDepth_ObjectsAndMixedNesting_AreAlsoLimited()
    {
        // {"a":[{"a":[...]}]} nested past the limit: the depth rule is the container depth, whatever the container kind
        var any = new ByteSink();
        for (var i = 0; i < 300; i++)
        {
            if (i % 2 == 0) any.Byte(117).VarUint(1);
            else any.Byte(118).VarUint(1).VarString("a");
        }
        any.Byte(126);
        Hostile.ApplyAndCheck("mixed Any depth 300", UpdateBuilder.MapValueUpdate(1, any.ToArray()), UpdateFormat.V1, new[] { "LimitExceeded" });
    }

    // ------------------------------------------------------------------ structs per update (HOS-AMP, HOS-LEN)

    [Fact, Covers("HOS-AMP", "HOS-LEN", Layer = CoverLayer.Unit)]
    public void StructCount_Tiny_AtTheLimitIsAccepted_OneOverIsRejected()
    {
        var max = Hostile.Tiny.MaxStructsPerUpdate;
        AssertAccepted(Hostile.Tiny, Gcs(max), $"{max} structs");
        AssertLimit(Hostile.Tiny, Gcs(max + 1), $"{max + 1} structs");
        // the same count spread over several clients is still counted per update
        var twoClients = UpdateBuilder.V1(UpdateModel.Of(
            new ClientBlock(1, 0, Enumerable.Range(0, max / 2 + 1).Select(_ => Struct.Gc(1)).ToArray()),
            new ClientBlock(2, 0, Enumerable.Range(0, max / 2 + 1).Select(_ => Struct.Gc(1)).ToArray())));
        AssertLimit(Hostile.Tiny, twoClients, "structs of two clients");
    }

    [Fact, Covers("HOS-AMP", "HOS-LEN", "ERR-09", Layer = CoverLayer.Unit)]
    public void StructCount_Default_AClaimOverTheLimitIsALimitRejection_AClaimAtTheLimitIsNot()
    {
        var limit = (ulong)Default.MaxStructsPerUpdate;
        byte[] Claim(ulong n) => UpdateBuilder.V1(new UpdateModel(new[] { new ClientBlock(1, 0, new[] { Struct.Gc(1) }, NumStructsOverride: n) }, Array.Empty<DeleteClient>()));
        AssertLimit(Default, Claim(limit + 1), "claimed structs over the limit");
        Assert.True(Outcome(Default, Claim(limit)) == "Malformed", "a claim at the limit passes the limit check and then runs off the end of the body");
    }

    // ------------------------------------------------------------------ string and update size

    [Fact, Covers("HOS-LEN", Layer = CoverLayer.Unit)]
    public void StringBytes_Tiny_AtTheLimitIsAccepted_OneOverIsRejected()
    {
        byte[] WithString(int bytes) => UpdateBuilder.V1(UpdateUpdate.TextOfBytes(bytes));
        AssertAccepted(Hostile.Tiny, WithString(Hostile.Tiny.MaxStringBytes), "string of MaxStringBytes");
        AssertLimit(Hostile.Tiny, WithString(Hostile.Tiny.MaxStringBytes + 1), "string of MaxStringBytes + 1");
    }

    [Fact, Covers("HOS-LEN", "ERR-09", Layer = CoverLayer.Unit)]
    public void StringBytes_Default_AClaimOverTheLimitIsALimitRejection()
    {
        // item with a string whose claimed length is MaxStringBytes + 1 and no body: nothing may be allocated for the claim
        var claim = (ulong)Default.MaxStringBytes + 1;
        var bytes = new ByteSink().VarUint(1).VarUint(1).VarUint(1).VarUint(0).Byte(4).VarUint(1).VarString("t").VarUint(claim).ToArray();
        AssertLimit(Default, bytes, "claimed string over MaxStringBytes");
    }

    [Fact, Covers("HOS-LEN", Layer = CoverLayer.Unit)]
    [Requires(Feature.V2, Feature.UpdateFunctions, Feature.V2Functions)]
    public void UpdateBytes_Tiny_AtTheLimitIsAccepted_OneOverIsRejected()
    {
        var valid = UpdateBuilder.V1(UpdateBuilder.TextInsert(1, "t", "hi"));
        byte[] Padded(int total) => valid.Concat(new byte[total - valid.Length]).ToArray();   // trailing bytes stay accepted (ERR-04)
        AssertAccepted(Hostile.Tiny, Padded(Hostile.Tiny.MaxUpdateBytes), "update of MaxUpdateBytes");
        AssertLimit(Hostile.Tiny, Padded(Hostile.Tiny.MaxUpdateBytes + 1), "update of MaxUpdateBytes + 1");
    }

    // ------------------------------------------------------------------ every decode path, V1 and V2 (B1)

    public static TheoryData<string, UpdateFormat> Paths()
    {
        var data = new TheoryData<string, UpdateFormat>();
        foreach (var path in new[] { "apply", "merge", "diff", "convert", "stateVector", "validate" })
            foreach (var format in new[] { UpdateFormat.V1, UpdateFormat.V2 })
                data.Add(path, format);
        return data;
    }

    private static YError ErrorOf(string path, byte[] update, UpdateFormat format, YLimits limits)
    {
        var sink = new ArrayBufferWriter<byte>();
        var other = format == UpdateFormat.V1 ? UpdateFormat.V2 : UpdateFormat.V1;
        var run = InputGuard.Run($"limits on {path} ({format})", () => path switch
        {
            "apply" => Hostile.NewDoc(limits).TryApplyUpdate(update, null, format).Error,
            "merge" => YUpdate.TryMerge(new[] { (ReadOnlyMemory<byte>)update }, sink, format, limits).Error,
            "diff" => YUpdate.TryDiff(update, new byte[] { 0 }, sink, format, limits).Error,
            "convert" => YUpdate.TryConvert(update, format, other, sink, limits).Error,
            "stateVector" => YUpdate.TryStateVectorFromUpdate(update, sink, format, limits).Error,
            "validate" => YUpdate.TryValidate(update, format, limits).Error,
            _ => throw new ArgumentException(path),
        });
        return run.Value;
    }

    private static byte[] Encode(UpdateModel model, UpdateFormat format) => format == UpdateFormat.V1 ? UpdateBuilder.V1(model) : UpdateBuilder.V2(model);

    [Theory, MemberData(nameof(Paths)), Covers("HOS-AMP", "HOS-LEN", "ERR-08", Layer = CoverLayer.Unit)]
    public void StructCount_OnEveryDecodePath_LimitIsAccepted_LimitPlusOneIsRejected(string path, UpdateFormat format)
    {
        var max = Hostile.Tiny.MaxStructsPerUpdate;
        Assert.Equal(YError.None, ErrorOf(path, Encode(UpdateModel.Single(1, 0, Enumerable.Range(0, max).Select(_ => Struct.Gc(1)).ToArray()), format), format, Hostile.Tiny));
        Assert.Equal(YError.LimitExceeded, ErrorOf(path, Encode(UpdateModel.Single(1, 0, Enumerable.Range(0, max + 1).Select(_ => Struct.Gc(1)).ToArray()), format), format, Hostile.Tiny));
    }

    [Theory, MemberData(nameof(Paths)), Covers("HOS-LEN", "ERR-08", Layer = CoverLayer.Unit)]
    [Requires(Feature.V2)]
    public void StringBytes_OnEveryDecodePath_LimitIsAccepted_LimitPlusOneIsRejected(string path, UpdateFormat format)
    {
        var max = Hostile.Tiny.MaxStringBytes;   // V2 keeps strings in one column: the limit is checked on the claimed length of each string
        Assert.Equal(YError.None, ErrorOf(path, Encode(UpdateBuilder.TextInsert(1, "t", new string('a', max)), format), format, Hostile.Tiny));
        Assert.Equal(YError.LimitExceeded, ErrorOf(path, Encode(UpdateBuilder.TextInsert(1, "t", new string('a', max + 1)), format), format, Hostile.Tiny));
    }

    [Theory, MemberData(nameof(Paths)), Covers("HOS-LEN", "ERR-08", Layer = CoverLayer.Unit)]
    public void UpdateBytes_OnEveryDecodePath_LimitIsAccepted_LimitPlusOneIsRejected(string path, UpdateFormat format)
    {
        var valid = Encode(UpdateBuilder.TextInsert(1, "t", "hi"), format);
        byte[] Padded(int total) => valid.Concat(new byte[total - valid.Length]).ToArray();
        Assert.Equal(YError.None, ErrorOf(path, Padded(Hostile.Tiny.MaxUpdateBytes), format, Hostile.Tiny));
        Assert.Equal(YError.LimitExceeded, ErrorOf(path, Padded(Hostile.Tiny.MaxUpdateBytes + 1), format, Hostile.Tiny));
    }

    // ------------------------------------------------------------------ pending bytes (ERR-10)

    [Fact, Covers("HOS-AMP", "ERR-10", Layer = CoverLayer.Unit)]
    public void PendingBytes_RetainedStateAtTheLimitIsAccepted_OneOverIsRejected()
    {
        // one update whose only struct waits for a missing client: the retained pending state is that update's own V1 encoding
        var waiting = UpdateBuilder.V1(UpdateModel.Single(1000, 0, Struct.InRoot("t", Content.Str("x"), origin: new Id(999_999, 0))));
        var accepted = Hostile.ApplyAndCheck("pending at the limit", waiting, UpdateFormat.V1, new[] { "ok" }, Hostile.Tiny with { MaxPendingBytes = waiting.Length });
        Assert.Equal("ok", accepted.Outcome);
        Hostile.ApplyAndCheck("pending one over the limit", waiting, UpdateFormat.V1, new[] { "LimitExceeded" }, Hostile.Tiny with { MaxPendingBytes = waiting.Length - 1 });
    }

    [Fact, Covers("HOS-AMP", "HOS-REF", "ERR-06", "ERR-10", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text)]
    public void PendingBytes_Tiny_TheLimitRejectsNewUpdates_AndKeepsWhatWasPending()
    {
        const ulong missing = 999_999;
        byte[] Waiting(int i) => UpdateBuilder.V1(UpdateModel.Single(1000 + (ulong)i, 0, Struct.InRoot("t", Content.Str("x"), origin: new Id(missing, 0))));
        var doc = Hostile.NewDoc(Hostile.Tiny);
        var accepted = 0;
        var firstRejection = -1;
        for (var i = 0; i < 400; i++)
        {
            var result = InputGuard.Run($"pending flood {i}", () => doc.TryApplyUpdate(Waiting(i))).Value;
            if (result.Ok)
            {
                Assert.True(firstRejection < 0, $"update {i} was accepted after update {firstRejection} hit the pending limit");
                accepted++;
            }
            else
            {
                Assert.Equal(YError.LimitExceeded, result.Error);
                if (firstRejection < 0) firstRejection = i;
            }
        }
        Assert.True(accepted >= 1, "the first waiting update must fit in 1 KB of pending state");
        Assert.True(firstRejection > 0, "400 updates must hit MaxPendingBytes = 1 KB");
        Assert.Equal("", doc.GetText("t").ToString());   // nothing integrated: every struct waits for the missing client

        // the dependency arrives: everything that was kept integrates, the rejected updates did not displace it
        var dependency = UpdateBuilder.V1(UpdateModel.Single(missing, 0, Struct.InRoot("t", Content.Str("D"))));
        Assert.True(doc.TryApplyUpdate(dependency).Ok);
        Assert.Equal(accepted + 1, doc.GetText("t").ToString().Length);
        Hostile.AssertPostCondition(doc, "pending limit");
    }
}

internal static class UpdateUpdate
{
    /// <summary>An update with one root-text string of exactly <paramref name="utf8Bytes"/> UTF-8 bytes (ASCII).</summary>
    public static UpdateModel TextOfBytes(int utf8Bytes) => UpdateBuilder.TextInsert(1, "t", new string('a', utf8Bytes));
}
