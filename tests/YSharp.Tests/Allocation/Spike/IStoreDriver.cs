namespace YSharp.StoreHarness;

/// <summary>What a store reports about its own work. <c>StructCount</c> counts every item the store created (ADR 0018, structs per operation).</summary>
public readonly record struct DriverStats(long StructCount, long Commits);

/// <summary>
/// The operations the M0 spike workloads use, on one document with a text "t", an array "a" and a map "m" (the names
/// <c>MemoryWorkloads</c> uses). Each storage candidate is one adapter over this interface, and so is the M2 store later.
/// Indexes are 0-based character positions. The harness reads <see cref="TextLength"/> before each edit, as the workloads do.
/// </summary>
public interface IStoreDriver
{
    /// <summary>Visible length of the text, in characters.</summary>
    int TextLength { get; }

    /// <summary>Inserts one character at <paramref name="index"/> (0 to <see cref="TextLength"/>).</summary>
    void InsertChar(int index, char value);

    /// <summary>Deletes the one character at <paramref name="index"/>.</summary>
    void DeleteAt(int index);

    /// <summary>Appends one scalar value to the array.</summary>
    void PushValue(double value);

    /// <summary>Sets <paramref name="key"/> in the map to a scalar value.</summary>
    void SetKey(string key, double value);

    /// <summary>Ends the current transaction. The workloads commit once per batch, or once per edit in the steady-state mix.</summary>
    void Commit();

    DriverStats Stats { get; }
}
