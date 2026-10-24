using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.Fuzz.Builder;
using YSharp.Fuzz.Corpus;
using YSharp.Fuzz.Guards;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Assertions;

namespace YSharp.Fuzz.Tests;

/// <summary>
/// HOS-TRUNC (ADR 0013): truncation at every byte offset of every valid fixture update (V1 and V2), state vector, sync frame and
/// awareness frame. A cut strictly inside the structure gives <c>Malformed</c> and changes nothing; the full input still applies.
/// The oracle's per-offset outcome table is stored in the fixtures (ADR 0011) and compared in the smoke tier; this tier asserts
/// y-sharp's contract only.
/// </summary>
public sealed class TruncationTests
{
    private static readonly string[] Malformed = { "Malformed" };

    [Fact]
    public void Seeds_GeneratedFixtures_AreHarvested()
    {
        Assert.True(SeedUpdates.Collect().Count >= 50, "expected at least 50 distinct valid updates in fixtures/");
        Assert.Contains(SeedUpdates.Collect(), s => s.Format == UpdateFormat.V1);
        Assert.Contains(SeedUpdates.Collect(), s => s.Format == UpdateFormat.V2);
        Assert.NotEmpty(SeedUpdates.StateVectors());
    }

    [Fact, Covers("HOS-TRUNC", Layer = CoverLayer.Unit)]
    public void Updates_EveryProperPrefix_IsMalformedAndChangesNothing() => Stub.Run(() =>
    {
        var failures = new List<string>();
        var cuts = 0;
        foreach (var seed in SeedUpdates.Small())
        {
            var label = $"{seed.Source} {seed.Format}";
            var doc = Hostile.NewDoc();
            try
            {
                for (var cut = 0; cut < seed.Bytes.Length; cut++)
                {
                    cuts++;
                    var applied = Hostile.ApplyTo(doc, $"{label} cut {cut}/{seed.Bytes.Length}", seed.Bytes[..cut], seed.Format);
                    Assert.True(applied.Outcome == "Malformed", $"{label}: the {cut}-byte prefix of {seed.Bytes.Length} gave {applied.Outcome}");
                }
                Hostile.AssertPostCondition(doc, label);
                Assert.True(Hostile.NewDoc().TryApplyUpdate(seed.Bytes, null, seed.Format).Ok, $"{label}: the full update no longer applies");
            }
            catch (Xunit.Sdk.XunitException e) { failures.Add(e.Message); }
        }
        Assert.True(cuts > 1000, $"only {cuts} cuts were swept");
        if (failures.Count > 0) Assert.Fail($"{failures.Count} failing seeds:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(20))}");
    });

    [Fact, Covers("HOS-TRUNC", Layer = CoverLayer.Unit)]
    public void StateVectors_EveryProperPrefix_IsMalformed() => Stub.Run(() =>
    {
        var target = UpdateBuilder.V1(UpdateBuilder.TextInsert(1, "t", "hi"));
        foreach (var sv in SeedUpdates.StateVectors().Append(UpdateBuilder.StateVector((1, 5), (900, 3), (4294967295, 1))))
        {
            Assert.True(YUpdate.TryDiff(target, sv, new ArrayBufferWriter<byte>()).Ok, $"the full state vector {Hex.Short(sv)} was rejected");
            for (var cut = 0; cut < sv.Length; cut++)
            {
                var prefix = sv[..cut];
                var status = InputGuard.Run($"state vector {Hex.Short(sv)} cut {cut}", () => YUpdate.TryDiff(target, prefix, new ArrayBufferWriter<byte>())).Value;
                Assert.True(!status.Ok && status.Error == YError.Malformed, $"the {cut}-byte prefix of state vector {Hex.Of(sv)} gave {(status.Ok ? "ok" : status.Error.ToString())}");
            }
        }
    });

    [Fact, Covers("HOS-TRUNC", Layer = CoverLayer.Unit)]
    public void SyncFrames_EveryProperPrefixInsideTheFrame_IsMalformedAndChangesNothing() => Stub.Run(() =>
    {
        var frames = new List<(string Name, byte[] Frame)>();
        foreach (var seed in SeedUpdates.Small(200).Where(s => s.Format == UpdateFormat.V1).Take(40))
        {
            frames.Add(($"step2 of {seed.Source}", UpdateBuilder.SyncMessage(1, seed.Bytes)));
            frames.Add(($"update of {seed.Source}", UpdateBuilder.SyncMessage(2, seed.Bytes)));
        }
        foreach (var sv in SeedUpdates.StateVectors().Take(20)) frames.Add(("step1", UpdateBuilder.SyncMessage(0, sv)));
        Assert.NotEmpty(frames);

        foreach (var (name, frame) in frames)
        {
            var doc = Hostile.NewDoc();
            Assert.Equal("ok", SyncOutcome(doc, frame, $"{name} (full)"));
            for (var cut = 1; cut < frame.Length; cut++)
            {
                var fresh = Hostile.NewDoc();
                var outcome = SyncOutcome(fresh, frame[..cut], $"{name} cut {cut}/{frame.Length}");
                Assert.True(outcome == "Malformed", $"{name}: the {cut}-byte prefix of {frame.Length} gave {outcome}");
                Assert.True(Wire.Equal(Hostile.StateVector(fresh), Hostile.StateVector(Hostile.NewDoc())), $"{name}: a truncated frame changed the document");
            }
        }
    });

    [Fact, Covers("HOS-TRUNC", Layer = CoverLayer.Unit)]
    public void AwarenessFrames_EveryProperPrefix_IsMalformedAndChangesNothing() => Stub.Run(() =>
    {
        var frames = new[]
        {
            UpdateBuilder.AwarenessUpdate((7, 1, "{\"cursor\":3}")),
            UpdateBuilder.AwarenessUpdate((7, 2, "null")),
            UpdateBuilder.AwarenessUpdate((1, 1, "{}"), (300, 9, "{\"name\":\"ä\"}"), (4294967295, 4, "[1,2]")),
        };
        foreach (var frame in frames)
        {
            Assert.True(new Awareness(Hostile.NewDoc()).TryApplyUpdate(frame, null).Ok, $"the full frame {Hex.Of(frame)} was rejected");
            for (var cut = 0; cut < frame.Length; cut++)
            {
                var prefix = frame[..cut];
                var doc = Hostile.NewDoc();
                using var awareness = new Awareness(doc);
                var before = CorpusRunner.Snapshot(awareness);
                var result = InputGuard.Run($"awareness {Hex.Of(frame)} cut {cut}", () => awareness.TryApplyUpdate(prefix, null)).Value;
                Assert.True(!result.Ok && result.Error == YError.Malformed, $"the {cut}-byte prefix of awareness frame {Hex.Of(frame)} gave {Hostile.Label(result)}");
                Assert.Equal(before, CorpusRunner.Snapshot(awareness));
                Hostile.AssertPostCondition(doc, $"awareness cut {cut}");
            }
        }
    });

    private static string SyncOutcome(Doc doc, byte[] frame, string label)
    {
        var run = InputGuard.Run(label, () =>
        {
            ReadOnlySpan<byte> input = frame;
            var status = YSync.TryReadMessage(ref input, new ArrayBufferWriter<byte>(), doc, null, out _);
            return status.Ok ? "ok" : status.Error.ToString();
        });
        InputGuard.AssertAllocationWithinBound(label, run.AllocatedBytes, frame.Length);
        return run.Value;
    }
}

internal static class Wire
{
    public static bool Equal(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
}
