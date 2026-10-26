using System;
using BenchmarkDotNet.Running;
using YSharp.Benchmarks.Comparison;

namespace YSharp.Benchmarks;

/// <summary>
/// <c>dotnet run -c Release --project bench/YSharp.Benchmarks -- --filter "*"</c> runs the benchmarks (BenchmarkDotNet options apply).
/// <c>compare &lt;baseline&gt; &lt;current&gt; [--threshold N] [--out file.md]</c> reports regressions against a stored run and always exits 0.
/// <c>table</c> prints the bytes-per-item table, y-sharp next to Yjs; <c>table-one &lt;workload&gt;</c> measures one workload (the table's child process).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "compare") return BenchmarkComparison.Run(args[1..], Console.Out);
        if (args.Length > 0 && args[0] == "table") return BytesPerItemTable.Run(Console.Out);
        if (args.Length > 1 && args[0] == "table-one") return BytesPerItemTable.MeasureOne(args[1], Console.Out);
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
