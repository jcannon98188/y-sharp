using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using YSharp.Fuzz.Builder;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Assertions;

namespace YSharp.Fuzz.Guards;

/// <summary>The result of one guarded hostile apply: the document it ran on and what happened.</summary>
internal sealed record Applied(Doc Doc, ApplyResult Result, byte[] StateBefore, byte[] StateAfter, long AllocatedBytes)
{
    public string Outcome => Hostile.Label(Result);
}

/// <summary>
/// The hostile-input harness (ADR 0013): guarded apply, the post-condition and the candidate-file writer. Every hostile
/// input of every layer (corpus, truncation sweeps, byte mutations, structure-aware builder, delivery schedules) ends in
/// <see cref="Hostile.Apply"/> followed by <see cref="Hostile.AssertPostCondition"/>.
/// </summary>
internal static class Hostile
{
    /// <summary>ADR 0013 tiny limits, used by the boundary tests and the nightly tiny pass.</summary>
    public static readonly YLimits Tiny = new()
    {
        MaxAnyDepth = 8,
        MaxStructsPerUpdate = 100,
        MaxPendingBytes = 1024,
        MaxStringBytes = 1024,
        MaxUpdateBytes = 64 * 1024,
    };

    /// <summary>The known-good update of the post-condition. Its client id and root key are chosen not to collide with anything a hostile input uses.</summary>
    public static readonly byte[] KnownGood = UpdateBuilder.V1(UpdateBuilder.TextInsert(0x5eed5eed, "__post", "ok"));

    public static Doc NewDoc(YLimits? limits = null) => new(new DocOptions { ClientId = 7, Limits = limits ?? YLimits.Default });

    public static string Label(ApplyResult r) => r.Ok ? "ok" : r.Error.ToString();

    public static byte[] StateVector(Doc doc)
    {
        var w = new ArrayBufferWriter<byte>();
        doc.EncodeStateVector(w);
        return w.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Applies <paramref name="bytes"/> to a new document (after <paramref name="before"/>) on the guarded probe thread. Enforces what holds for
    /// every input: no exception from <c>TryApplyUpdate</c> (ADR 0005), no hang, allocation within k*N+C (ADR 0009), and that a
    /// <c>Malformed</c> or <c>LimitExceeded</c> result changed nothing (atomic decode).
    /// </summary>
    public static Applied Apply(string label, byte[] bytes, UpdateFormat format = UpdateFormat.V1, YLimits? limits = null, IEnumerable<byte[]>? before = null)
    {
        var doc = NewDoc(limits);
        foreach (var b in before ?? Array.Empty<byte[]>())
            Assert.True(doc.TryApplyUpdate(b, null, format).Ok, $"{label}: a 'before' update was rejected, the case is invalid.");
        return ApplyTo(doc, label, bytes, format);
    }

    /// <summary>The same checks as <see cref="Apply"/> on a document the caller keeps (a sweep over one seed reuses one document, since a rejection must change nothing).</summary>
    public static Applied ApplyTo(Doc doc, string label, byte[] bytes, UpdateFormat format = UpdateFormat.V1)
    {
        var svBefore = StateVector(doc);
        var stateBefore = doc.EncodeStateAsUpdate();

        var run = InputGuard.Run(label, () => doc.TryApplyUpdate(bytes, null, format));
        InputGuard.AssertAllocationWithinBound(label, run.AllocatedBytes, bytes.Length);

        var result = run.Value;
        if (!result.Ok && result.Error is YError.Malformed or YError.LimitExceeded)
        {
            Assert.True(svBefore.AsSpan().SequenceEqual(StateVector(doc)) && stateBefore.AsSpan().SequenceEqual(doc.EncodeStateAsUpdate()),
                $"{label}: {result.Error} must change nothing (atomic decode), but the document changed. Input {Hex.Short(bytes)}");
        }
        return new Applied(doc, result, svBefore, StateVector(doc), run.AllocatedBytes);
    }

    /// <summary>
    /// The post-condition of ADR 0013: after any hostile input the document is not faulted, <c>EncodeStateAsUpdate</c> works and
    /// its output applies to a fresh document, and a known-good update still applies. This is what proves a failed apply leaves a consistent document.
    /// </summary>
    public static void AssertPostCondition(Doc doc, string label)
    {
        Assert.False(doc.IsFaulted, $"{label}: the document is faulted (ERR-21: no input may fault a document).");
        var state = InputGuard.Run($"{label} (EncodeStateAsUpdate)", () => doc.EncodeStateAsUpdate()).Value;
        var replay = InputGuard.Run($"{label} (re-apply of the encoded state)", () => NewDoc().TryApplyUpdate(state)).Value;
        Assert.True(replay.Ok, $"{label}: the document's own encoded state is rejected with {replay.Error}.");
        var good = InputGuard.Run($"{label} (known-good update)", () => doc.TryApplyUpdate(KnownGood)).Value;
        Assert.True(good.Ok, $"{label}: a known-good update no longer applies ({good.Error}).");
        Assert.Equal("ok", doc.GetText("__post").ToString());
        Assert.False(doc.IsFaulted, $"{label}: the document is faulted after the known-good update.");
    }

    /// <summary>Apply, assert the allowed outcomes, then the post-condition. <paramref name="allowed"/> uses the corpus names: ok, Malformed, LimitExceeded, Rejected, UnknownMessage.</summary>
    public static Applied ApplyAndCheck(string label, byte[] bytes, UpdateFormat format, IReadOnlyCollection<string> allowed, YLimits? limits = null, IEnumerable<byte[]>? before = null)
    {
        var applied = Apply(label, bytes, format, limits, before);
        Assert.True(allowed.Contains(applied.Outcome), $"{label}: outcome {applied.Outcome}, allowed {string.Join("/", allowed)}. Input {Hex.Short(bytes)}");
        AssertPostCondition(applied.Doc, label);
        return applied;
    }

    // ------------------------------------------------------------------ candidate files

    /// <summary>
    /// Writes a failing input as a corpus candidate (ADR 0013: a failure writes a corpus candidate file) and returns its path. The file is
    /// not a fixture: to commit it, minimise it, add it to <c>tools/fixture-gen/scenarios/hostile-corpus.mjs</c> under its class and
    /// run <c>npm run gen</c>; the folder's git history is the log.
    /// </summary>
    public static string WriteCandidate(string hosClass, byte[] bytes, UpdateFormat format, long seed, string failure)
    {
        var dir = Environment.GetEnvironmentVariable("YSHARP_FUZZ_CANDIDATES") is { Length: > 0 } d
            ? d
            : Path.Combine(RepoRoot.Find(), "TestResults", "hostile-candidates");
        Directory.CreateDirectory(dir);
        var sha = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        var path = Path.Combine(dir, $"{hosClass.ToLowerInvariant()}-{sha}.json");
        var json = JsonSerializer.Serialize(new
        {
            @class = hosClass,
            encoding = format == UpdateFormat.V2 ? "v2" : "v1",
            seed,
            discovered = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            bytes = Hex.Of(bytes),
            failure,
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }
}
