using System;
using System.Text.Json;
using YSharp.StoreSpike.Harness;


namespace YSharp.StoreSpike;

/// <summary>
/// Child entry point for the spike harness: <c>dotnet YSharp.StoreSpike.dll measure &lt;candidate&gt; &lt;workload&gt;</c> measures one workload in this
/// process and writes one JSON line. <see cref="SpikeProcess.MedianOfRuns"/> calls it once per run.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "measure")
        {
            var measurement = SpikeProcess.MeasureChild(args[1], args[2]);
            Console.WriteLine(JsonSerializer.Serialize(measurement));
            return 0;
        }

        Console.Error.WriteLine("usage: YSharp.StoreSpike measure <candidate> <workload>");
        return 2;
    }
}
