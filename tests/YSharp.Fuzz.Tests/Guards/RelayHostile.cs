using System;
using System.Buffers;
using Xunit;
using YSharp.TestKit.Assertions;

namespace YSharp.Fuzz.Guards;

/// <summary>
/// The hostile-input harness of <see cref="Hostile"/> for a <see cref="YRelay"/> (TASK-5.3): a guarded fold with the allocation bound, a
/// rejected update changing nothing (RLY-4), and a post-condition that the relay's state is a valid update in both formats, folds into a fresh
/// relay to the same bytes, applies to a <see cref="Doc"/>, and that a known-good update still folds in.
/// </summary>
internal static class RelayHostile
{
    /// <summary>
    /// Allocation a fold may make per held struct it merges or copies again (<see cref="YRelay.MergedHeldStructs"/>): the merge copies each
    /// into its output as a struct of about 180 bytes, and the per-client lists copy it again as they grow, with its V1 offset. A fold that only appends to its clients' histories merges none (TASK-9), so
    /// its bound is the input's alone; a fold that fills a gap merges that client's structs from the gap on, and one in the flat form, the
    /// whole state.
    /// </summary>
    public const long BytesPerMergedStruct = 768;

    public static byte[] StateVector(YRelay relay)
    {
        var w = new ArrayBufferWriter<byte>();
        relay.EncodeStateVector(w);
        return w.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Folds <paramref name="bytes"/> into <paramref name="relay"/> on the probe thread. The bound is k*N+C over the input plus
    /// <see cref="BytesPerMergedStruct"/> per held struct the fold merged again. A relay never rejects part of an update: the result is ok,
    /// Malformed or LimitExceeded, and the last two change nothing.
    /// </summary>
    public static YStatus ApplyTo(YRelay relay, string label, byte[] bytes, UpdateFormat format)
    {
        var svBefore = StateVector(relay);
        var stateBefore = relay.EncodeStateAsUpdate();
        var run = InputGuard.Run(label, () => relay.TryApplyUpdate(bytes, format));
        InputGuard.AssertAllocationWithinBound(label, run.AllocatedBytes - BytesPerMergedStruct * relay.MergedHeldStructs, bytes.Length);
        var status = run.Value;
        Assert.True(status.Ok || status.Error is YError.Malformed or YError.LimitExceeded, $"{label}: the relay returned {status.Error}. Input {Hex.Short(bytes)}");
        if (!status.Ok)
        {
            Assert.True(svBefore.AsSpan().SequenceEqual(StateVector(relay)) && stateBefore.AsSpan().SequenceEqual(relay.EncodeStateAsUpdate()),
                $"{label}: {status.Error} must change nothing (RLY-4), but the relay changed. Input {Hex.Short(bytes)}");
        }

        return status;
    }

    public static void AssertPostCondition(YRelay relay, string label)
    {
        var v1 = InputGuard.Run($"{label} (EncodeStateAsUpdate V1)", () => relay.EncodeStateAsUpdate()).Value;
        var v2 = InputGuard.Run($"{label} (EncodeStateAsUpdate V2)", () => relay.EncodeStateAsUpdate(format: UpdateFormat.V2)).Value;
        Assert.True(YUpdate.TryValidate(v1).Ok, $"{label}: the relay's V1 state is not a valid update.");
        Assert.True(YUpdate.TryValidate(v2, UpdateFormat.V2).Ok, $"{label}: the relay's V2 state is not a valid update.");

        // the whole state is one update, so the per-update limits do not apply to it (as UpdateLog.StoredLimits drops them for stored records)
        var again = new YRelay(relay.Limits with { MaxUpdateBytes = int.MaxValue, MaxStructsPerUpdate = int.MaxValue });
        var refold = again.TryApplyUpdate(v1);
        Assert.True(refold.Ok, $"{label}: a fresh relay with the same pending limit refuses the relay's own state ({refold.Error}).");
        Assert.True(v1.AsSpan().SequenceEqual(again.EncodeStateAsUpdate()), $"{label}: the relay's state folded into a fresh relay encodes differently.");

        // the relay keeps structs a Doc rejects (a parent that is not a type), so Rejected is allowed; nothing may fault the Doc
        var doc = Hostile.NewDoc();
        var applied = InputGuard.Run($"{label} (Doc applies the relay's state)", () => doc.TryApplyUpdate(v1)).Value;
        Assert.True(applied.Error is YError.None or YError.Rejected, $"{label}: a Doc refuses the relay's state with {applied.Error}.");
        Assert.False(doc.IsFaulted, $"{label}: the relay's state faulted a Doc.");

        var good = InputGuard.Run($"{label} (known-good update)", () => relay.TryApplyUpdate(Hostile.KnownGood)).Value;
        Assert.True(good.Ok, $"{label}: a known-good update no longer folds in ({good.Error}).");
    }

    /// <summary>A fresh relay under <paramref name="limits"/>, the fold of <paramref name="bytes"/>, the allowed outcomes, then the post-condition.</summary>
    public static YStatus ApplyAndCheck(string label, byte[] bytes, UpdateFormat format, YLimits? limits = null)
    {
        var relay = new YRelay(limits);
        var status = ApplyTo(relay, label, bytes, format);
        AssertPostCondition(relay, label);
        return status;
    }
}
