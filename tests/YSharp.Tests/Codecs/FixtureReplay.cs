using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Values;

namespace YSharp.Tests.Codecs;

/// <summary>
/// The C# half of the update-function step kinds (<c>mergeUpdates</c>, <c>diffUpdate</c>, <c>convertUpdate</c>, ADR 0016) and the
/// embed and nested-type insert kinds the codec fixtures use. A kind another area already registered is left alone.
/// </summary>
internal static class CodecSteps
{
    private sealed class Results
    {
        public Dictionary<string, byte[]> Bytes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<byte[]>> V2Updates { get; } = new(StringComparer.Ordinal);
    }

    private static readonly ConditionalWeakTable<OpsContext, Results> State = new();

    private static Results Of(OpsContext ctx) => State.GetValue(ctx, _ => new Results());

    public static IReadOnlyDictionary<string, byte[]> ResultBytes(OpsContext ctx) => Of(ctx).Bytes;

    public static IReadOnlyDictionary<string, List<byte[]>> V2Updates(OpsContext ctx) => Of(ctx).V2Updates;

    public static OpsRegistry CreateRegistry()
    {
        var r = StandardSteps.CreateRegistry();
        void Add(string kind, string[] required, string[] optional, Action<OpsContext, OpsStep> run)
        {
            if (!r.TryGet(kind, out _)) r.Register(new StepDefinition(kind, required, optional, run));
        }

        Add("mergeUpdates", new[] { "name", "updates" }, new[] { "encoding" }, (ctx, s) =>
        {
            var format = Format(s.OptString("encoding") ?? "v1");
            var list = s.Tagged("updates").Items.Select(u => (ReadOnlyMemory<byte>)u.Bytes).ToList();
            var w = new ArrayBufferWriter<byte>();
            Check(s, YUpdate.TryMerge(list, w, format));
            Of(ctx).Bytes[s.String("name")] = w.WrittenSpan.ToArray();
        });
        Add("diffUpdate", new[] { "name", "update", "sv" }, new[] { "encoding" }, (ctx, s) =>
        {
            var w = new ArrayBufferWriter<byte>();
            Check(s, YUpdate.TryDiff(s.Tagged("update").Bytes, s.Tagged("sv").Bytes, w, Format(s.OptString("encoding") ?? "v1")));
            Of(ctx).Bytes[s.String("name")] = w.WrittenSpan.ToArray();
        });
        Add("convertUpdate", new[] { "name", "update", "from" }, Array.Empty<string>(), (ctx, s) =>
        {
            var from = Format(s.String("from"));
            var to = from == UpdateFormat.V1 ? UpdateFormat.V2 : UpdateFormat.V1;
            var w = new ArrayBufferWriter<byte>();
            Check(s, YUpdate.TryConvert(s.Tagged("update").Bytes, from, to, w));
            Of(ctx).Bytes[s.String("name")] = w.WrittenSpan.ToArray();
        });

        Add("text.insertEmbed", new[] { "doc", "target", "at", "value" }, new[] { "attributes" }, (ctx, s) =>
        {
            var text = ctx.ResolveTarget(ctx.Doc(s.String("doc")), s.Element("target")) as YText
                       ?? throw new OpsScriptException("text.insertEmbed: target is not a text.");
            text.InsertEmbed(s.Int("at"), s.Tagged("value").ToYValue(), s.Has("attributes") ? s.Tagged("attributes").ToYValue() : default);
        });
        Add("array.insertType", new[] { "doc", "target", "at", "kind" }, new[] { "init" }, (ctx, s) =>
        {
            var array = ctx.ResolveTarget(ctx.Doc(s.String("doc")), s.Element("target")) as YArray
                        ?? throw new OpsScriptException("array.insertType: target is not an array.");
            array.Insert(s.Int("at"), new[] { (YEntry)MakeType(s) });
        });
        Add("map.insertType", new[] { "doc", "target", "key", "kind" }, new[] { "init" }, (ctx, s) =>
        {
            var map = ctx.ResolveTarget(ctx.Doc(s.String("doc")), s.Element("target")) as YMap
                      ?? throw new OpsScriptException("map.insertType: target is not a map.");
            map.Set(s.String("key"), (YEntry)MakeType(s));
        });
        return r;
    }

    private static YType MakeType(OpsStep s)
    {
        var init = s.Has("init") ? s.Tagged("init") : null;
        switch (s.String("kind"))
        {
            case "text":
                return new YText(init is { Kind: TaggedKind.String } ? init.String : null);
            case "array":
                return YArray.From(init is { Kind: TaggedKind.Array } ? init.Items.Select(v => (YEntry)v.ToYValue()) : Array.Empty<YEntry>());
            case "map":
                return new YMap(init is { Kind: TaggedKind.Object }
                    ? init.Pairs.Select(p => new KeyValuePair<string, YEntry>(p.Key, p.Value.ToYValue()))
                    : Array.Empty<KeyValuePair<string, YEntry>>());
            default:
                throw new OpsScriptException($"{s.Kind}: unknown type kind '{s.String("kind")}'.");
        }
    }

    private static UpdateFormat Format(string name) => name switch
    {
        "v1" => UpdateFormat.V1,
        "v2" => UpdateFormat.V2,
        _ => throw new OpsScriptException($"unknown encoding '{name}'."),
    };

    private static void Check(OpsStep s, YStatus status)
    {
        if (!status.Ok) throw new InvalidOperationException($"{s.Kind} '{s.OptString("name")}' failed with {status.Error}.");
    }
}

/// <summary>A finished replay of a fixture's script against y-sharp.</summary>
internal sealed record Replay(OpsContext Context, IReadOnlyDictionary<string, List<byte[]>> V1Updates, IReadOnlyDictionary<string, List<byte[]>> V2Updates);

/// <summary>
/// Replays a generated fixture (ADR 0011) in y-sharp and compares what the oracle recorded: update-event bytes, full and diff
/// state in both formats, state vectors and pure-function results are compared bytewise; the malformed-input and truncation
/// observations drive the strict-decoding contract of ADR 0005.
/// </summary>
internal static class FixtureReplay
{
    public static Fixture Load(string relativePath)
    {
        var root = RepoRoot.Find();
        return FixtureLoader.Load(Path.Combine(root, "fixtures", relativePath), PinnedVersions.Read(root));
    }

    public static Replay Run(Fixture fixture)
    {
        var registry = CodecSteps.CreateRegistry();
        var script = OpsScript.Parse(fixture.Script, registry);
        var interpreter = new OpsInterpreter(registry);
        var v2 = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
        interpreter.DocFactory = (ctx, spec) =>
        {
            var doc = new Doc(new DocOptions { ClientId = spec.ClientId, Gc = spec.Gc, TimeProvider = ctx.Clock });
            var l1 = new List<byte[]>();
            var l2 = new List<byte[]>();
            ctx.Updates[spec.Name] = l1;
            v2[spec.Name] = l2;
            doc.Update += (u, _, _, _) => l1.Add(u.ToArray());
            doc.UpdateV2 += (u, _, _, _) => l2.Add(u.ToArray());
            return doc;
        };
        var ctx = interpreter.Run(script);
        return new Replay(ctx, ctx.Updates.ToDictionary(k => k.Key, k => k.Value), v2);
    }

    private static byte[] BytesOf(JsonElement e) => TaggedValue.Read(e).Bytes;

    /// <summary>Update events, state, diffs and state vectors of every replica, V1 and V2.</summary>
    public static void AssertWire(Fixture fixture, Replay replay)
    {
        var obs = fixture.Observations;
        foreach (var rep in obs.GetProperty("updates").GetProperty("value").EnumerateObject())
        {
            AssertList(rep.Value.GetProperty("v1"), replay.V1Updates[rep.Name], $"{fixture.Path} updates v1 of {rep.Name}");
            AssertList(rep.Value.GetProperty("v2"), replay.V2Updates[rep.Name], $"{fixture.Path} updates v2 of {rep.Name}");
        }

        foreach (var rep in obs.GetProperty("state").GetProperty("value").EnumerateObject())
        {
            var doc = replay.Context.Doc(rep.Name);
            foreach (var (name, format) in new[] { ("v1", UpdateFormat.V1), ("v2", UpdateFormat.V2) })
            {
                var enc = rep.Value.GetProperty(name);
                HexAssert.Equal(BytesOf(enc.GetProperty("full")), doc.EncodeStateAsUpdate(default, format), $"{fixture.Path} {rep.Name} full {name}");
                foreach (var other in enc.GetProperty("diff").EnumerateObject())
                {
                    var sv = Wire.StateVector(replay.Context.Doc(other.Name));
                    HexAssert.Equal(BytesOf(other.Value), doc.EncodeStateAsUpdate(sv, format), $"{fixture.Path} {rep.Name} diff against {other.Name} {name}");
                }
            }
        }

        foreach (var rep in obs.GetProperty("sv").GetProperty("value").EnumerateObject())
        {
            var sv = Wire.StateVector(replay.Context.Doc(rep.Name));
            HexAssert.Equal(BytesOf(rep.Value.GetProperty("v1")), sv, $"{fixture.Path} {rep.Name} sv v1");
            HexAssert.Equal(BytesOf(rep.Value.GetProperty("v2")), sv, $"{fixture.Path} {rep.Name} sv v2");
        }

        if (obs.TryGetProperty("results", out var results))
        {
            var actual = CodecSteps.ResultBytes(replay.Context);
            foreach (var r in results.GetProperty("value").EnumerateObject())
                HexAssert.Equal(BytesOf(r.Value.GetProperty("bytes")), actual[r.Name], $"{fixture.Path} result {r.Name}");
        }
    }

    private static void AssertList(JsonElement expected, List<byte[]> actual, string label)
    {
        var want = expected.EnumerateArray().Select(BytesOf).ToList();
        Assert.True(want.Count == actual.Count, $"{label}: expected {want.Count} update events, got {actual.Count}.");
        for (var i = 0; i < want.Count; i++) HexAssert.Equal(want[i], actual[i], $"{label} #{i}");
    }

    /// <summary>Recorded divergences where y-sharp accepts what the oracle rejects (ADR 0003): a <c>diverge</c> case naming one is accepted.</summary>
    private static readonly HashSet<string> LenientDivergences = new(StringComparer.Ordinal) { "ENC-51" };

    /// <summary>
    /// The malformed-input contract: where the oracle accepts a <c>match</c> case y-sharp accepts it; where the oracle rejects, y-sharp
    /// rejects; a <c>diverge</c> or <c>js-crash</c> case is rejected, except a divergence in <see cref="LenientDivergences"/>, which is
    /// accepted. Every rejection is typed (never an exception from <c>TryApplyUpdate</c>) and changes nothing (ADR 0005 atomic decode).
    /// </summary>
    public static void AssertMalformed(Fixture fixture, Func<string, bool>? only = null)
    {
        var cases = fixture.Observations.GetProperty("malformed").GetProperty("value").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString()!;
            if (only != null && !only(name)) continue;
            var format = c.GetProperty("encoding").GetString() == "v2" ? UpdateFormat.V2 : UpdateFormat.V1;
            var bytes = BytesOf(c.GetProperty("bytes"));
            var classification = c.GetProperty("classification").GetString();
            var divergence = c.TryGetProperty("divergence", out var d) ? d.GetString() : null;
            var expectAccepted = (classification == "match" && c.GetProperty("outcome").GetString() == "ok")
                || (classification == "diverge" && divergence is not null && LenientDivergences.Contains(divergence));

            var (result, doc) = Wire.TryApply(bytes, format);
            Assert.True(expectAccepted == result.Ok,
                $"{fixture.Path} case '{name}' ({classification}, oracle {c.GetProperty("outcome").GetString()}): y-sharp {(result.Ok ? "accepted" : "rejected with " + result.Error)}.");
            if (!result.Ok)
            {
                Assert.True(Wire.IsEmpty(StateVector.Of(doc)), $"{fixture.Path} case '{name}': a rejected update changed the document.");
                Assert.Throws<YDecodeException>(() => new Doc().ApplyUpdate(bytes, null, format));
            }
        }
    }

    /// <summary>Every proper prefix of a valid update is rejected without a change (ADR 0011 truncation outcomes, ADR 0005).</summary>
    public static void AssertTruncation(Fixture fixture, Replay replay)
    {
        var truncation = fixture.Observations.GetProperty("truncation").GetProperty("value");
        foreach (var entry in truncation.EnumerateArray())
        {
            var format = entry.GetProperty("encoding").GetString() == "v2" ? UpdateFormat.V2 : UpdateFormat.V1;
            var doc = replay.Context.Doc(entry.GetProperty("replica").GetString()!);
            var full = doc.EncodeStateAsUpdate(default, format);
            Assert.Equal(entry.GetProperty("length").GetInt32(), full.Length);
            for (var cut = 0; cut < full.Length; cut++)
            {
                var (result, target) = Wire.TryApply(full.AsSpan(0, cut).ToArray(), format);
                Assert.False(result.Ok, $"{fixture.Path} {format}: the {cut}-byte prefix of {full.Length} was accepted.");
                Assert.True(Wire.IsEmpty(StateVector.Of(target)), $"{fixture.Path} {format}: the {cut}-byte prefix changed the document.");
            }
        }
    }
}
