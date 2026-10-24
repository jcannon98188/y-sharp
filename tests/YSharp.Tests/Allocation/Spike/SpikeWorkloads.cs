using System;

namespace YSharp.StoreHarness;

/// <summary>
/// The three slow-tier workloads and the steady-state mix, written against <see cref="IStoreDriver"/>. They issue the same operations, in the same
/// order and with the same positions, as <c>MemoryWorkloads</c> does through the public API. The constants are checked against it in
/// <c>SpikeHarnessTests</c>.
/// </summary>
public static class SpikeWorkloads
{
    public const string TextInsert = "text-insert";
    public const string ArrayPush = "array-push";
    public const string MapSet = "map-set";
    public const string SteadyState = "steady-state";

    public const int Batch = 1000;
    public const int TextInserts = 1_000_000;
    public const int ArrayPushes = 1_000_000;
    public const int MapKeys = 100_000;
    public const int SteadyEdits = 100_000;
    public const int SteadyWarmupEdits = 2_000;
    public const int SteadyStartChars = 10_000;

    /// <summary>The item count of one run of <paramref name="workload"/>.</summary>
    public static int ItemsOf(string workload) => workload switch
    {
        TextInsert => TextInserts,
        ArrayPush => ArrayPushes,
        MapSet => MapKeys,
        SteadyState => SteadyEdits,
        _ => throw new ArgumentException("unknown workload " + workload, nameof(workload)),
    };

    /// <summary>Builds a document of <paramref name="items"/> operations for a slow-tier workload.</summary>
    public static void Build(IStoreDriver driver, string workload, int items)
    {
        switch (workload)
        {
            case TextInsert: TypeText(driver, items); break;
            case ArrayPush: PushScalars(driver, items); break;
            case MapSet: SetScalars(driver, items); break;
            default: throw new ArgumentException("not a build workload: " + workload, nameof(workload));
        }
    }

    /// <summary>Offset from the end of the text, 0 to 15, as <c>MemoryWorkloads.Text</c> computes it, so items fragment as they do under typing.</summary>
    public static int Back(int j) => (int)(unchecked((uint)j * 0x9E3779B1u) & 15);

    /// <summary>One character each, within 15 of the end; <c>MemoryWorkloads.Text</c>.</summary>
    public static void TypeText(IStoreDriver driver, int n)
    {
        for (var i = 0; i < n; i += Batch)
        {
            var from = i;
            for (var j = from; j < Math.Min(n, from + Batch); j++)
            {
                var index = Math.Max(0, driver.TextLength - Back(j));
                driver.InsertChar(index, (char)('a' + j % 26));
            }

            driver.Commit();
        }
    }

    /// <summary>One scalar per push; <c>MemoryWorkloads.ArrayOfScalars</c>.</summary>
    public static void PushScalars(IStoreDriver driver, int n)
    {
        for (var i = 0; i < n; i += Batch)
        {
            var from = i;
            for (var j = from; j < Math.Min(n, from + Batch); j++) driver.PushValue(j);
            driver.Commit();
        }
    }

    /// <summary>One scalar per key, keys "k0", "k1", ...; <c>MemoryWorkloads.MapOfScalars</c>.</summary>
    public static void SetScalars(IStoreDriver driver, int n)
    {
        for (var i = 0; i < n; i += Batch)
        {
            var from = i;
            for (var j = from; j < Math.Min(n, from + Batch); j++) driver.SetKey("k" + j, j);
            driver.Commit();
        }
    }

    /// <summary>Fills the text to its working size before measuring; <c>MemoryWorkloads.SteadyStateStart</c>, one character per insert.</summary>
    public static void SteadyStateStart(IStoreDriver driver, int chars = SteadyStartChars)
    {
        for (var i = 0; i < chars; i++) driver.InsertChar(i, 'a');
        driver.Commit();
    }

    /// <summary>
    /// Alternates a one-character insert near the end and a delete next to it, so the length stays flat. Each edit commits on its own, as the
    /// public-API version runs outside a transaction; <c>MemoryWorkloads.SteadyStateEdits</c>.
    /// </summary>
    public static void SteadyStateEdits(IStoreDriver driver, int edits)
    {
        for (var i = 0; i < edits; i++)
        {
            var back = Back(i);
            if ((i & 1) == 0) driver.InsertChar(Math.Max(0, driver.TextLength - back), 'x');
            else driver.DeleteAt(Math.Max(0, driver.TextLength - 1 - back));
            driver.Commit();
        }
    }
}
