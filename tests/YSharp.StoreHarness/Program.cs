using System;
using System.Reflection;

namespace YSharp.StoreHarness;

/// <summary>
/// Child entry point for the spike harness: <c>dotnet YSharp.StoreHarness.dll measure &lt;candidate&gt; &lt;workload&gt; &lt;harness assembly&gt;</c>
/// loads the assembly that holds the harness (the test assembly), measures one workload in this process and writes one JSON line.
/// <c>SpikeProcess.MedianOfRuns</c> calls it once per run.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "measure")
        {
            Console.Error.WriteLine("usage: YSharp.StoreHarness measure <candidate> <workload> <harness assembly>");
            return 2;
        }

        var harness = Assembly.LoadFrom(args[3]);
        var process = harness.GetType("YSharp.StoreHarness.SpikeProcess", throwOnError: true)!;
        var measure = process.GetMethod("MeasureChildJson", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException("YSharp.StoreHarness.SpikeProcess", "MeasureChildJson");
        Console.WriteLine((string)measure.Invoke(null, new object[] { args[1], args[2] })!);
        return 0;
    }
}
