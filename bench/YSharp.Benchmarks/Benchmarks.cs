using System;
using System.Buffers;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace YSharp.Benchmarks;

// Benchmark-only measurements of ADR 0009: none of these gate anything. They run against the API stub until the implementation exists
// (each benchmark then throws NotImplementedException and BenchmarkDotNet reports it), and the nightly job tolerates that.

internal static class Work
{
    public const int Batch = 1000;

    public static void Text(Doc doc, int n)
    {
        var text = doc.GetText("t");
        for (var i = 0; i < n; i += Batch)
        {
            var from = i;
            doc.Transact(_ => { for (var j = from; j < Math.Min(n, from + Batch); j++) text.Insert(text.Length, "x"); });
        }
    }

    public static void Array(Doc doc, int n)
    {
        var arr = doc.GetArray("a");
        var one = new YEntry[1];
        for (var i = 0; i < n; i += Batch)
        {
            var from = i;
            doc.Transact(_ => { for (var j = from; j < Math.Min(n, from + Batch); j++) { one[0] = (double)j; arr.Push(one); } });
        }
    }

    public static void Map(Doc doc, int n)
    {
        var map = doc.GetMap("m");
        for (var i = 0; i < n; i += Batch)
        {
            var from = i;
            doc.Transact(_ => { for (var j = from; j < Math.Min(n, from + Batch); j++) map.Set("k" + j, (double)j); });
        }
    }

    /// <summary>A document with text, array and map content of roughly <paramref name="items"/> items in total.</summary>
    public static Doc Build(uint clientId, int items)
    {
        var doc = new Doc(new DocOptions { ClientId = clientId });
        Text(doc, items / 3);
        Array(doc, items / 3);
        Map(doc, items / 3);
        return doc;
    }
}

[MemoryDiagnoser]
public class ThroughputBenchmarks
{
    private const int N = 10_000;

    [Benchmark] public void TextAppend() => Work.Text(new Doc(), N);
    [Benchmark] public void ArrayPush() => Work.Array(new Doc(), N);
    [Benchmark] public void MapSet() => Work.Map(new Doc(), N);

    [Benchmark]
    public void ApplyUpdatesV1()
    {
        var source = Work.Build(1, 3_000);
        var update = source.EncodeStateAsUpdate();
        new Doc().ApplyUpdate(update);
    }
}

[MemoryDiagnoser]
public class MergeDiffBenchmarks
{
    private byte[] _a = Array.Empty<byte>();
    private byte[] _b = Array.Empty<byte>();
    private byte[] _stateVector = Array.Empty<byte>();

    [GlobalSetup]
    public void Setup()
    {
        var a = Work.Build(1, 30_000);
        var b = Work.Build(2, 30_000);
        _a = a.EncodeStateAsUpdate();
        _b = b.EncodeStateAsUpdate();
        var w = new ArrayBufferWriter<byte>();
        a.EncodeStateVector(w);
        _stateVector = w.WrittenSpan.ToArray();
    }

    [Benchmark]
    public int Merge()
    {
        var output = new ArrayBufferWriter<byte>();
        YUpdate.TryMerge(new ReadOnlyMemory<byte>[] { _a, _b }, output);
        return output.WrittenCount;
    }

    [Benchmark]
    public int Diff()
    {
        var output = new ArrayBufferWriter<byte>();
        YUpdate.TryDiff(_b, _stateVector, output);
        return output.WrittenCount;
    }
}

/// <summary>Building a 1M-item document, the figure that stands behind the bytes-per-item table.</summary>
[MemoryDiagnoser]
[SimpleJob(iterationCount: 3, warmupCount: 1)]
public class BuildLargeDocBenchmarks
{
    [Benchmark] public Doc Build1MItems() => Work.Build(1, 1_000_000);
}

/// <summary>Cost of the document lock (ADR 0006): the empty transaction, the try form and the read form.</summary>
[MemoryDiagnoser]
public class LockCostBenchmarks
{
    private Doc _doc = new();

    [GlobalSetup] public void Setup() => _doc = new Doc();

    [Benchmark(Baseline = true)] public void WriteTransaction() => _doc.Transact(0, static (_, _) => { });
    [Benchmark] public void TryWriteTransaction() => _doc.TryTransact(static _ => { });
    [Benchmark] public void ReadTransaction() => _doc.ReadTransact(static _ => { });
}

/// <summary>
/// The y-sharp-versus-Yjs table: the same operations as the Yjs script <c>bench/yjs-memory/measure.mjs</c>, timed here, with the Yjs
/// bytes-per-item figure printed next to y-sharp's by <c>table</c>. Yjs times come from running that script's workloads in Node, not from this process.
/// </summary>
[MemoryDiagnoser]
public class YjsComparisonBenchmarks
{
    private const int N = 100_000;

    [Benchmark] public void TextInserts() => Work.Text(new Doc(), N);
    [Benchmark] public void ArrayPushes() => Work.Array(new Doc(), N);
    [Benchmark] public void MapSets() => Work.Map(new Doc(), N);
}

/// <summary>The bytes-per-item table: y-sharp's managed heap per item next to the Yjs figure, printed by the <c>table</c> command.</summary>
internal static class BytesPerItemTable
{
    public static int Run(System.IO.TextWriter output)
    {
        var yjs = ReadYjs();
        output.WriteLine("| Workload | Items | y-sharp bytes/item | Yjs bytes/item |");
        output.WriteLine("|---|---|---|---|");
        foreach (var (name, items, build) in new (string, int, Action<Doc>)[]
                 {
                     ("text-insert", 1_000_000, d => Work.Text(d, 1_000_000)),
                     ("array-push", 1_000_000, d => Work.Array(d, 1_000_000)),
                     ("map-set", 100_000, d => Work.Map(d, 100_000)),
                 })
        {
            string ours;
            try
            {
                var before = Heap();
                var doc = new Doc();
                build(doc);
                var after = Heap();
                GC.KeepAlive(doc);
                ours = ((after - before) / (double)items).ToString("0.00");
            }
            catch (NotImplementedException) { ours = "n/a (API stub)"; }
            output.WriteLine($"| {name} | {items} | {ours} | {(yjs.TryGetValue(name, out var y) ? y.ToString("0.00") : "n/a")} |");
        }

        return 0;
    }

    private static long Heap()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(true);
    }

    private static Dictionary<string, double> ReadYjs()
    {
        var result = new Dictionary<string, double>();
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        if (dir == null) return result;
        var path = System.IO.Path.Combine(dir.FullName, "bench", "yjs-memory", "yjs-bytes-per-item.json");
        if (!System.IO.File.Exists(path)) return result;
        using var json = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
        foreach (var w in json.RootElement.GetProperty("workloads").EnumerateObject())
            result[w.Name] = w.Value.GetProperty("bytesPerItem").GetDouble();
        return result;
    }
}
