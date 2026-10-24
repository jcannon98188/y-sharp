using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using YSharp.TestKit.Fixtures;

namespace YSharp.Tests.Allocation;

/// <summary>
/// The three slow-tier workloads of ADR 0009. They mirror <c>bench/yjs-memory/measure.mjs</c> operation for operation, so the figure it
/// records for Yjs is the ceiling for the same work here. Writes go in transactions of <see cref="Batch"/> operations.
/// </summary>
internal static class MemoryWorkloads
{
    public const int Batch = 1000;
    public const int TextInserts = 1_000_000;
    public const int ArrayPushes = 1_000_000;
    public const int MapKeys = 100_000;

    public static int ItemsOf(string workload) => workload switch
    {
        AllocationBudgets.TextInsert => TextInserts,
        AllocationBudgets.ArrayPush => ArrayPushes,
        AllocationBudgets.MapSet => MapKeys,
        _ => throw new ArgumentException("unknown workload " + workload),
    };

    public static Doc Run(string workload) => workload switch
    {
        AllocationBudgets.TextInsert => Text(TextInserts),
        AllocationBudgets.ArrayPush => ArrayOfScalars(ArrayPushes),
        AllocationBudgets.MapSet => MapOfScalars(MapKeys),
        _ => throw new ArgumentException("unknown workload " + workload),
    };

    /// <summary>One character each, at a position within 15 of the end, so items fragment as they do under real typing.</summary>
    public static Doc Text(int n)
    {
        var doc = new Doc();
        var text = doc.GetText("t");
        for (var i = 0; i < n; i += Batch)
        {
            var from = i;
            doc.Transact(_ =>
            {
                for (var j = from; j < Math.Min(n, from + Batch); j++)
                {
                    var back = (int)(unchecked((uint)j * 0x9E3779B1u) & 15);
                    text.Insert(Math.Max(0, text.Length - back), ((char)('a' + j % 26)).ToString());
                }
            });
        }

        return doc;
    }

    public static Doc ArrayOfScalars(int n)
    {
        var doc = new Doc();
        var arr = doc.GetArray("a");
        var one = new YEntry[1];
        for (var i = 0; i < n; i += Batch)
        {
            var from = i;
            doc.Transact(_ =>
            {
                for (var j = from; j < Math.Min(n, from + Batch); j++)
                {
                    one[0] = (double)j;
                    arr.Push(one);
                }
            });
        }

        return doc;
    }

    public static Doc MapOfScalars(int n)
    {
        var doc = new Doc();
        var map = doc.GetMap("m");
        for (var i = 0; i < n; i += Batch)
        {
            var from = i;
            doc.Transact(_ =>
            {
                for (var j = from; j < Math.Min(n, from + Batch); j++) map.Set("k" + j, (double)j);
            });
        }

        return doc;
    }

    /// <summary>100k edits on a document that has reached its working size: alternate a one-character insert near the end and a delete next to it, so length stays flat.</summary>
    public static void SteadyStateEdits(Doc doc, int edits)
    {
        var text = doc.GetText("t");
        for (var i = 0; i < edits; i++)
        {
            var back = (int)(unchecked((uint)i * 0x9E3779B1u) & 15);
            if ((i & 1) == 0) text.Insert(Math.Max(0, text.Length - back), "x");
            else text.Delete(Math.Max(0, text.Length - 1 - back), 1);
        }
    }

    public static Doc SteadyStateStart(int chars = 10_000)
    {
        var doc = new Doc();
        doc.GetText("t").Insert(0, new string('a', chars));
        return doc;
    }

    /// <summary>The Yjs bytes-per-item figure for a workload, from the baseline the oracle script records.</summary>
    public static double? YjsBytesPerItem(string workload)
    {
        var path = Path.Combine(RepoRoot.Find(), "bench", "yjs-memory", "yjs-bytes-per-item.json");
        if (!File.Exists(path)) return null;
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("workloads").GetProperty(workload).GetProperty("bytesPerItem").GetDouble();
    }

    public static string ReportDir() => Path.Combine(RepoRoot.Find(), "TestResults", "memory");

    public static void WriteReport(string name, IDictionary<string, object> values)
    {
        Directory.CreateDirectory(ReportDir());
        File.WriteAllText(Path.Combine(ReportDir(), name + ".json"), JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
    }
}
