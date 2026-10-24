using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Sdk;
using YSharp.Fuzz.Builder;
using YSharp.Fuzz.Guards;

namespace YSharp.Fuzz.Corpus;

/// <summary>Replays stored hostile cases through the guarded harness: outcome in the allowed set, atomic rejection, then the post-condition.</summary>
internal static class CorpusRunner
{
    private static readonly string[] EveryOutcome = { "ok", "Malformed", "LimitExceeded", "Rejected", "UnknownMessage" };

    private static readonly byte[] HiV1 = UpdateBuilder.V1(UpdateBuilder.TextInsert(1, "t", "hi"));

    /// <summary>Replays every case; failures are collected so one run names all of them. A stub <see cref="NotImplementedException"/> skips the test.</summary>
    public static void ReplayAll(IEnumerable<CorpusCase> cases, YLimits? limits = null)
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var c in cases)
        {
            count++;
            try { Run(c, limits); }
            catch (XunitException e) { failures.Add($"{c.Label}: {e.Message}"); }
        }
        Assert.True(count > 0, "the corpus selection is empty");
        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} of {count} corpus cases failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(25))}");
    }

    public static void Run(CorpusCase c, YLimits? limitsOverride = null)
    {
        var limits = limitsOverride ?? (c.Tiny ? Hostile.Tiny : null);
        // under a limits override the stored expectation was written for the defaults, so only the universal contract is asserted
        var allowed = limitsOverride != null ? EveryOutcome : c.Expect;
        switch (c.Api)
        {
            case "update": RunUpdate(c, limits, allowed); break;
            case "sv": RunStateVector(c, limits, allowed); break;
            case "sync": RunSync(c, limits, allowed); break;
            case "awareness": RunAwareness(c, limits, allowed); break;
            default: throw new InvalidOperationException($"{c.Label}: unknown api '{c.Api}'.");
        }
    }

    private static void RunUpdate(CorpusCase c, YLimits? limits, IReadOnlyCollection<string> allowed)
    {
        var applied = Hostile.ApplyAndCheck(c.Label, c.Bytes, c.Format, allowed, limits, c.Before);
        foreach (var next in c.After)
        {
            var again = InputGuard.Run(c.Label + " (follow-up)", () => applied.Doc.TryApplyUpdate(next, null, c.Format)).Value;
            Assert.True(allowed.Contains(Hostile.Label(again)), $"follow-up outcome {Hostile.Label(again)}, allowed {string.Join("/", allowed)}");
            Hostile.AssertPostCondition(applied.Doc, c.Label);
        }
        if (applied.Result.Error is YError.Malformed or YError.LimitExceeded)
        {
            // the throwing entry point wraps the same result (ERR-01)
            var twin = Hostile.NewDoc(limits);
            foreach (var b in c.Before) twin.ApplyUpdate(b, null, c.Format);
            var thrown = Assert.Throws<YDecodeException>(() => twin.ApplyUpdate(c.Bytes, null, c.Format));
            Assert.Equal(applied.Result.Error, thrown.Error);
        }
    }

    private static void RunStateVector(CorpusCase c, YLimits? limits, IReadOnlyCollection<string> allowed)
    {
        var doc = Hostile.NewDoc(limits);
        doc.ApplyUpdate(HiV1);
        var run = InputGuard.Run(c.Label, () => YUpdate.TryDiff(HiV1, c.Bytes, new ArrayBufferWriter<byte>()));
        InputGuard.AssertAllocationWithinBound(c.Label, run.AllocatedBytes, c.Bytes.Length);
        var outcome = run.Value.Ok ? "ok" : run.Value.Error.ToString();
        Assert.True(allowed.Contains(outcome), $"outcome {outcome}, allowed {string.Join("/", allowed)}");
        Hostile.AssertPostCondition(doc, c.Label);
    }

    private static void RunSync(CorpusCase c, YLimits? limits, IReadOnlyCollection<string> allowed)
    {
        var doc = Hostile.NewDoc(limits);
        var run = InputGuard.Run(c.Label, () =>
        {
            ReadOnlySpan<byte> input = c.Bytes;
            var reply = new ArrayBufferWriter<byte>();
            while (!input.IsEmpty)
            {
                var before = input.Length;
                var status = YSync.TryReadMessage(ref input, reply, doc, null, out _);
                if (!status.Ok) return status.Error.ToString();
                if (input.Length >= before) return "NoProgress";
            }
            return "ok";
        });
        InputGuard.AssertAllocationWithinBound(c.Label, run.AllocatedBytes, c.Bytes.Length);
        Assert.True(allowed.Contains(run.Value), $"outcome {run.Value}, allowed {string.Join("/", allowed)}");
        Hostile.AssertPostCondition(doc, c.Label);
    }

    private static void RunAwareness(CorpusCase c, YLimits? limits, IReadOnlyCollection<string> allowed)
    {
        var doc = Hostile.NewDoc(limits);
        using var awareness = new Awareness(doc);
        foreach (var bytes in new[] { c.Bytes }.Concat(c.After))
        {
            var snapshot = Snapshot(awareness);
            var run = InputGuard.Run(c.Label, () => awareness.TryApplyUpdate(bytes, null));
            InputGuard.AssertAllocationWithinBound(c.Label, run.AllocatedBytes, bytes.Length);
            var outcome = Hostile.Label(run.Value);
            Assert.True(allowed.Contains(outcome), $"outcome {outcome}, allowed {string.Join("/", allowed)}");
            if (!run.Value.Ok && run.Value.Error is YError.Malformed or YError.LimitExceeded)
                Assert.Equal(snapshot, Snapshot(awareness));
        }
        Hostile.AssertPostCondition(doc, c.Label);
    }

    /// <summary>States and clocks as one string, to show that a rejected awareness update changed nothing.</summary>
    public static string Snapshot(Awareness awareness) =>
        string.Join(";", awareness.GetStates().Keys.OrderBy(k => k).Select(k => $"s{k}"))
        + "|" + string.Join(";", awareness.Meta.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value.Clock}"));
}
