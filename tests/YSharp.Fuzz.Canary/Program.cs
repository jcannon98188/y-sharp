using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;
using YSharp.Fuzz.Builder;

namespace YSharp.Fuzz.Canary;

/// <summary>
/// Canary host for the HOS-DEEP document-depth cases (ADR 0013, ADR 0005 "No recursion on document depth"). Usage:
/// <c>YSharp.Fuzz.Canary &lt;case&gt; [depth]</c>. Exit codes: 0 passed (the last stdout line starts with <c>OK</c>), 1 the case failed
/// (stderr starts with <c>FAIL</c>), 2 unknown case. A crash (stack overflow, fatal runtime
/// error) is whatever the runtime exits with; the test project classifies it.
/// The case runs on a thread with a fixed 1 MB stack, the Windows default, so the result does not depend on the platform's main-thread size.
/// </summary>
internal static class Program
{
    public const int DefaultDepth = 1_000_000;

    private static int Main(string[] args)
    {
        var name = args.Length > 0 ? args[0] : "";
        var depth = args.Length > 1 && int.TryParse(args[1], out var d) ? d : DefaultDepth;
        var exit = 2;
        var thread = new Thread(() => exit = Run(name, depth), 1 << 20);
        thread.Start();
        thread.Join();
        return exit;
    }

    private static int Run(string name, int depth)
    {
        try
        {
            switch (name)
            {
                case "selftest-ok":
                    Console.WriteLine("OK selftest");
                    return 0;
                case "selftest-overflow":
                    // proves the harness: this really overflows the stack, and the test project must report it by name
                    return Recurse(0);
                case "selftest-fail":
                    throw new InvalidOperationException("deliberate failure");
                case "array-chain":
                    return Chain(UpdateBuilder.NestedTypesV1(1, depth, "d", 0), d => d.GetArray("d").Delete(0, 1), depth);
                case "map-chain":
                    return Chain(UpdateBuilder.NestedTypesV1(1, depth, "d", 1, "k"), d => d.GetMap("d").Delete("k"), depth);
                case "update-functions":
                    return UpdateFunctions(UpdateBuilder.NestedTypesV1(1, depth, "d", 0));
                default:
                    Console.Error.WriteLine($"FAIL unknown case '{name}'");
                    return 2;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"FAIL {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static int Recurse(int n) => n < 0 ? 0 : Recurse(n + 1) + 1;

    /// <summary>Apply, encode (V1 and V2), re-apply, delete the whole chain from the root and encode again: apply, delete, GC, traversal and encode on a document nested <paramref name="depth"/> deep.</summary>
    private static int Chain(byte[] update, Action<Doc> deleteTop, int depth)
    {
        var watch = Stopwatch.StartNew();
        var doc = new Doc();
        var applied = doc.TryApplyUpdate(update);
        if (!applied.Ok) return Fail($"apply of a {depth}-deep chain was rejected with {applied.Error}");
        if (doc.IsFaulted) return Fail("the document is faulted after apply");

        var v1 = doc.EncodeStateAsUpdate();
        var v2 = doc.EncodeStateAsUpdate(default, UpdateFormat.V2);
        if (v1.Length == 0 || v2.Length == 0) return Fail("encode produced nothing");

        var copy = new Doc();
        var again = copy.TryApplyUpdate(v2, null, UpdateFormat.V2);
        if (!again.Ok) return Fail($"the V2 encoding of the chain was rejected with {again.Error}");
        var sv = doc.GetStateVector();
        if (sv[1] != depth) return Fail($"state vector clock is {sv[1]}, expected {depth}");

        // deleting the first level deletes everything below it; collection must walk the chain iteratively
        deleteTop(doc);
        if (doc.IsFaulted) return Fail("the document is faulted after deleting the chain");
        if (doc.EncodeStateAsUpdate().Length == 0) return Fail("encode after delete produced nothing");
        doc.Destroy();
        Console.WriteLine($"OK depth={depth} v1={v1.Length} v2={v2.Length} ms={watch.ElapsedMilliseconds}");
        return 0;
    }

    private static int UpdateFunctions(byte[] update)
    {
        var merged = new ArrayBufferWriter<byte>();
        var status = YUpdate.TryMerge(new[] { (ReadOnlyMemory<byte>)update, update }, merged);
        if (!status.Ok) return Fail($"TryMerge rejected the chain: {status.Error}");
        var diff = new ArrayBufferWriter<byte>();
        status = YUpdate.TryDiff(update, new byte[] { 0 }, diff);
        if (!status.Ok) return Fail($"TryDiff rejected the chain: {status.Error}");
        var convert = new ArrayBufferWriter<byte>();
        status = YUpdate.TryConvert(update, UpdateFormat.V1, UpdateFormat.V2, convert);
        if (!status.Ok) return Fail($"TryConvert rejected the chain: {status.Error}");
        var sv = new ArrayBufferWriter<byte>();
        status = YUpdate.TryStateVectorFromUpdate(update, sv);
        if (!status.Ok) return Fail($"TryStateVectorFromUpdate rejected the chain: {status.Error}");
        Console.WriteLine($"OK merge={merged.WrittenCount} diff={diff.WrittenCount} v2={convert.WrittenCount} sv={sv.WrittenCount}");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("FAIL " + message);
        return 1;
    }
}
