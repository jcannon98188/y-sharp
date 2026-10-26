using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Text.Json;
using SharpFuzz;

namespace YSharp.SharpFuzzHarness;

/// <summary>
/// Coverage-guided fuzz harness for y-sharp (ADR 0013). One input is a mode byte followed by the payload:
/// 0 V1 update, 1 V2 update, 2 sync message stream, 3 awareness update, 4 state vector (diffed against a small valid update).
/// Anything the contract allows (any ApplyResult, YDecodeException from the throwing form) is fine; everything else crashes the process,
/// which is what AFL++ and libFuzzer record: an unexpected exception, a hang (the fuzzer's timeout), an out-of-memory, a stack overflow,
/// or a failed post-condition (faulted document, state that cannot be encoded and re-applied, a known-good update that no longer applies).
/// <code>
/// Modes:   (none)                      AFL++ out-of-process loop on stdin (needs the instrumented YSharp.dll)
///          --libfuzzer                 libFuzzer in-process loop
///          --replay FILE               run one input and print the outcome (reproduce a crash)
///          --minimise FILE OUT         delta-debug a crashing input down to a minimal one
///          --export-seeds DIR          write the fixed corpus (fixtures/hostile) as mode-prefixed AFL seeds
/// </code>
/// A finding is minimised, then committed as one case of <c>tools/fixture-gen/scenarios/hostile-corpus.mjs</c> (see the nightly job).
/// </summary>
internal static class Program
{
    /// <summary>The known-good update of the post-condition, as in the fuzz suite: its client id and root key do not collide with anything a
    /// hostile input uses (the fixtures' client 1 and root "t" do).</summary>
    private static readonly byte[] KnownGood = BuildKnownGood();

    private static byte[] BuildKnownGood()
    {
        var doc = new Doc(new DocOptions { ClientId = 0x5eed5eed });
        doc.GetText("__post").Insert(0, "ok");
        return doc.EncodeStateAsUpdate();
    }

    private static int Main(string[] args)
    {
        switch (args)
        {
            case ["--replay", var file]:
                Console.WriteLine(Run(File.ReadAllBytes(file)));
                return 0;
            case ["--minimise", var file, var output]:
                File.WriteAllBytes(output, Minimise(File.ReadAllBytes(file)));
                return 0;
            case ["--export-seeds", var dir]:
                return ExportSeeds(dir);
            case ["--libfuzzer"]:
                Fuzzer.LibFuzzer.Run(span => Run(span.ToArray()));
                return 0;
            default:
                Fuzzer.OutOfProcess.Run(stream =>
                {
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    Run(memory.ToArray());
                });
                return 0;
        }
    }

    /// <summary>Runs one input against a fresh document and checks the post-condition. Returns a short outcome string; throws on a contract violation.</summary>
    public static string Run(byte[] input)
    {
        if (input.Length == 0) return "empty";
        var payload = input.AsSpan(1).ToArray();
        var doc = new Doc();
        string outcome;
        switch (input[0] % 5)
        {
            case 0:
            case 1:
            {
                var format = input[0] % 5 == 0 ? UpdateFormat.V1 : UpdateFormat.V2;
                var result = doc.TryApplyUpdate(payload, null, format);
                outcome = result.Ok ? "ok" : result.Error.ToString();
                if (!result.Ok && result.Error is YError.Malformed or YError.LimitExceeded)
                {
                    try { new Doc().ApplyUpdate(payload, null, format); throw new InvalidOperationException("ApplyUpdate accepted what TryApplyUpdate rejected"); }
                    catch (YDecodeException) { }
                }
                break;
            }
            case 2:
            {
                ReadOnlySpan<byte> rest = payload;
                var reply = new ArrayBufferWriter<byte>();
                outcome = "ok";
                for (var i = 0; i < 10_000 && !rest.IsEmpty; i++)
                {
                    var before = rest.Length;
                    var status = YSync.TryReadMessage(ref rest, reply, doc, null, out _);
                    if (!status.Ok) { outcome = status.Error.ToString(); break; }
                    if (rest.Length >= before) throw new InvalidOperationException("a sync message was read without consuming input");
                }
                break;
            }
            case 3:
            {
                using var awareness = new Awareness(doc);
                var result = awareness.TryApplyUpdate(payload, null);
                outcome = result.Ok ? "ok" : result.Error.ToString();
                break;
            }
            default:
            {
                var status = YUpdate.TryDiff(KnownGood, payload, new ArrayBufferWriter<byte>());
                outcome = status.Ok ? "ok" : status.Error.ToString();
                break;
            }
        }

        if (doc.IsFaulted) throw new InvalidOperationException("post-condition: the document is faulted");
        var state = doc.EncodeStateAsUpdate();
        if (!new Doc().TryApplyUpdate(state).Ok) throw new InvalidOperationException("post-condition: the encoded state is rejected");
        if (!doc.TryApplyUpdate(KnownGood).Ok) throw new InvalidOperationException("post-condition: a known-good update no longer applies");
        return outcome;
    }

    private static bool Crashes(byte[] input)
    {
        try { Run(input); return false; }
        catch (Exception) { return true; }
    }

    /// <summary>Greedy delta debugging: drop chunks, then single bytes, while the input still crashes.</summary>
    public static byte[] Minimise(byte[] input)
    {
        if (!Crashes(input)) throw new InvalidOperationException("the input does not crash; nothing to minimise");
        var current = input;
        for (var chunk = Math.Max(1, current.Length / 2); chunk >= 1; chunk /= 2)
        {
            var progress = true;
            while (progress)
            {
                progress = false;
                for (var at = 0; at + chunk <= current.Length; at += chunk)
                {
                    var candidate = current.Take(at).Concat(current.Skip(at + chunk)).ToArray();
                    if (candidate.Length > 0 && Crashes(candidate)) { current = candidate; progress = true; break; }
                }
            }
        }
        return current;
    }

    private static int ExportSeeds(string dir)
    {
        Directory.CreateDirectory(dir);
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "global.json"))) root = Path.GetDirectoryName(root);
        if (root == null) { Console.Error.WriteLine("global.json not found above the harness"); return 1; }
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "fixtures", "hostile"), "*.json"))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var c in json.RootElement.GetProperty("observations").GetProperty("malformed").GetProperty("value").EnumerateArray())
            {
                var api = c.TryGetProperty("api", out var a) ? a.GetString() : "update";
                var mode = api switch
                {
                    "update" => c.GetProperty("encoding").GetString() == "v2" ? (byte)1 : (byte)0,
                    "sync" => (byte)2,
                    "awareness" => (byte)3,
                    "sv" => (byte)4,
                    _ => (byte)255,
                };
                if (mode == 255) continue;
                var bytes = Hex(c.GetProperty("bytes").GetProperty("hex").GetString()!);
                File.WriteAllBytes(Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)}-{count++:D5}"), new[] { mode }.Concat(bytes).ToArray());
            }
        }
        Console.WriteLine($"{count} seeds written to {dir}");
        return 0;
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

}
